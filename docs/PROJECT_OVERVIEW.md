# VirtualGuidance 项目说明书

> 基于当前 `refactor` 分支代码、`docs/implementation/CODEX_REFACTOR_*` 系列文档及 git 历史整理，成文日期 2026-07-14。

## 1. 项目是什么

一套面向 XR 展厅导览场景的**冗余空间重定向（RDW）引导实验平台**，由两部分组成：

- **Unity 前端**（`Assets/`）：XR 展厅场景、用户姿态/运动感知、物理空间风险评估、旋转增益（rotation gain）重定向、导览执行与实验数据记录。
- **Python 后端**（`backend/`）：FastAPI 服务，接收前端上报的物理风险状态，跑一条确定性的 Agent 流水线，返回"是否可以引导、往哪引导"的决策，并记录日志。

核心研究问题：在物理空间受限（虚实空间不一致）时，如何安全地把用户重定向到目标展品，同时避免撞到真实边界/障碍物。项目设计了 6 种实验条件（包含多个刻意"阉割"安全信息的 baseline + 1 个完整方案）用于对比验证"完整安全校验链路"的必要性。

## 2. 技术栈

| | |
|---|---|
| Unity | 2022.3.62f3c1，Built-in 渲染管线 |
| XR | XR Interaction Toolkit 2.6.5 + XR Management 4.5.4 + OpenXR 1.14.3 + PICO XR SDK 3.4.0 |
| 脚本组织 | 所有自研脚本在默认 `Assembly-CSharp`，无 asmdef 分割 |
| 后端 | FastAPI（≥0.115）+ Uvicorn + Pydantic v2，Python |
| 测试 | Unity EditMode 测试（`Assets/Tests/Editor`）+ pytest（`backend/tests`） |

⚠️ 风险提示：`Packages/manifest.json` 中 PICO 相关两个包用的是本机绝对路径（`file:D:/SoftwareFiles/Unity/...`），换机器/换目录会直接装不上包，需要改成相对路径或私有包仓库。

## 3. 项目架构

### 3.1 Unity 端目录结构（`Assets/Scripts/`）

```
Core/          启动与全局状态：SystemBootstrapper（入口，最先执行）、GuidanceCoordinator（编排中枢）
Guidance/      引导 DTO + 执行链：GuidanceExecutor（唯一执行闸门）、NarrationPlayer、
               GazeCueController、MovementHintController、GuideAvatarController、
               ExhibitHighlightManager、GuidanceSceneInstaller
RDW/           重定向与风险：PhysicalSpaceManager、PhysicalRiskEvaluator、RotationGainController、
               RDWController、LocalGuidanceValidator、PauseResetController
Navigation/    目标选择与到达判定：NavigationTargetManager、SemanticTargetSelector、ArrivalDetector
Perception/    用户状态感知：UserPoseTracker、MotionDetector、UserStateSnapshot
Task/          任务路线状态机：GuidanceTaskController、GuidanceTaskDefinition
Integration/   外部对接：AgentClient（后端 HTTP 客户端）、XRTrackingAdapter（真实头显姿态映射）
Experiment/    实验工具：ExperimentConditionMatrix、ExperimentDashboard、ExperimentLogger、ExperimentSession
Diagnostics/   调试面板：BoundaryVisualizer、GuidanceDebugPanel、RiskDebugPanel、RejectionPanel 等
```

### 3.2 运行时数据流

```
UserPoseTracker / XRTrackingAdapter  →  MotionDetector（运动/转身检测）
        ↓
PhysicalSpaceManager + PhysicalRiskEvaluator  → 实时几何风险计算（0.2s 一次）
        ↓
GuidanceCoordinator  →  AgentClient  →  POST /api/guidance （后端）
        ↓ 收到响应
LocalGuidanceValidator（本地二次校验，防止用旧的/已不安全的引导）
        ↓ 通过
GuidanceExecutor（唯一执行闸门，认 verifier.approved）
        ↓
NarrationPlayer / GazeCueController / MovementHintController / GuideAvatarController
        ↓（若触发临界风险）
RDWController → PauseResetController（清空引导、取消旋转增益、展示拒绝原因）
```

`GuidanceTaskController` 驱动任务路线状态机（E1→E3→E5→Completed，配置驱动而非硬编码），`RotationGainController` 独立管理旋转增益的启停/取消（默认关闭，`gainEnabled = false`），并在 `PauseResetController` 触发临界重置时自动取消。

### 3.3 后端流水线（`backend/`）

```
RDWStateAgent → RDWAffordanceAgent → GuideMockAgent → SafetyVerifierAgent → GuidanceLogger
```

`GuideMockAgent` 按 6 种实验条件故意实现不同行为（有的不给物理约束、有的只给原始间隙、有的跳过最终校验……），这是实验设计本身而非未完成——目的是对比出"完整链路（ours_full）"相比各 baseline 更安全。当前是确定性 mock，尚无真实的 Guide MLLM Agent。

### 3.4 配置与数据（`Assets/Configs/`，Unity/后端共用）

- 4 套物理空间：`wide_room`、`narrow_room`、`obstacle_room`、`l_shaped_room`
- `scene_semantics.json`：8 个展品（E1-E8）语义信息
- 场景：`Assets/Scenes/DualWorldGallery.unity`（已注册为 Build Scene 0），`SampleScene.unity` 为默认模板未使用

## 3.5 Unity 端配置与组件挂载现状（重点）

本节来自对 `ProjectSettings`、`Packages/manifest.json`、脚本 `.meta` GUID、
`DualWorldGallery.unity` 序列化 YAML，以及 Unity Editor 实时 `scene_context` 的交叉核对。
检查时 Editor 未处于 Play Mode，但活动场景 `isDirty = true`，因此需要区分三种状态：

1. **场景持久化挂载**：组件和引用已经写入 `.unity` 文件，编辑状态下可在 Inspector 中看到。
2. **Editor 未保存改动**：当前 Editor 内可见，但还没有写回 `.unity` 文件；关闭场景时如果不保存会丢失。
3. **运行时自动装配**：组件没有写入场景，由 `GuidanceSceneInstaller.Awake()` 在进入 Play Mode 后动态添加和注入。

当前工程三者并存。编辑器当前能看到或模拟运行，不代表磁盘场景和正式构建已经包含相同配置。

### 3.5.1 工程级配置

| 配置项 | 当前值 | 说明 |
|---|---|---|
| Unity | `2022.3.62f3c1` | LTS 版本 |
| 产品名 | `VirtualGuidance` | `companyName` 仍为 `DefaultCompany` |
| Android application id | `com.DefaultCompany.VirtualGuidance` | 正式打包前应改成自己的反向域名 |
| Android 脚本后端 | IL2CPP | `scriptingBackend.Android = 1` |
| 输入系统 | Both | `activeInputHandler = 2`，旧输入和新 Input System 同时启用 |
| 色彩空间 | Linear | `m_ActiveColorSpace = 1` |
| Build Scene 0 | `Assets/Scenes/DualWorldGallery.unity` | 唯一启用的构建场景 |
| XR Loader 资产 | PXR、PXR PT、OpenXR 均存在 | 资产存在不等于主场景已接入 XR Origin |
| PICO 配置对象 | 已写入 Build Settings 的 `m_configObjects` | 包括 Live Preview、PXR Settings、XR Management loader settings、OpenXR settings |

依赖中最需要注意的是以下两个本机绝对路径：

```json
"com.unity.pico.livepreview": "file:D:/SoftwareFiles/Unity/Unity Live Preview Plugin-1.0.5-20250211",
"com.unity.xr.picoxr": "file:D:/SoftwareFiles/Unity/PICO Unity Integration SDK-3.4.0-20260226"
```

在其他电脑打开项目时，如果不存在完全相同的目录，Package Manager 会解析失败。因此当前工程不是可直接迁移的干净环境。

### 3.5.2 主场景中已经持久化的自研组件

主场景当前共持久化引用了 16 个自研 `MonoBehaviour`。关键挂载如下：

| 场景对象/职责 | 已挂组件 | 已序列化的关键引用或参数 |
|---|---|---|
| 物理空间管理对象 | `PhysicalSpaceManager` | 默认空间 `wide_room`；配置目录 `Assets/Configs`；4 个物理空间 JSON 文件 |
| 用户位姿对象 | `UserPoseTracker` | `virtualUserRoot` 指向现有 `Main Camera`；模拟物理坐标 `(1, 4.3)`；模拟 yaw `0` |
| `PhysicalRiskEvaluator` | `PhysicalRiskEvaluator` | 引用 `PhysicalSpaceManager`、`UserPoseTracker`；评估周期 `0.2s`；危险/安全/临界距离分别为 `0.8m / 1.2m / 0.45m` |
| `GuidanceManager` | `GuidanceSceneInstaller`、`AgentClient`、`GuidanceExecutor` | 引导系统的主要运行时装配根节点 |
| `GuidanceManager` | `NarrationPlayer`、`MovementHintController`、`GazeCueController` | 文本表现组件，具体 TMP 引用由 Installer 查找并注入 |
| `GuidanceManager` | `GuideAvatarController`、`ExhibitHighlightManager` | 虚拟导览员、指向动作和展品高亮 |
| `GuidanceManager` | `RejectionPanel`、`GuidanceDebugPanel` | 请求失败、安全拒绝和调试状态展示 |
| 调试对象 | `BoundaryVisualizer` | 已引用物理空间、位姿和风险评估器；可视化原点 `(7, 0.04, 0)` |
| 风险面板 | `RiskDebugPanel` | 已引用风险评估器和 `RiskDebugPanel_Text` |
| 名为 `ExperimentDashboard` 的对象 | `PhysicalSpaceDebugController` | 已引用物理空间和风险评估器；它目前并没有挂 `ExperimentDashboard` 组件 |

`GuidanceManager` 上的多个组件在场景 YAML 中只有脚本 GUID，没有持久化字段值。这意味着它们依赖脚本字段初始化值以及 Installer 的运行时注入。以当前代码默认值计：

- `AgentClient.guidanceEndpoint`：`http://127.0.0.1:8000/api/guidance`
- `AgentClient.requestTimeoutSeconds`：`8`
- `GuidanceSceneInstaller.installOnAwake`：`true`
- `createMissingCoreComponents`：`true`
- `createDebugUiIfMissing`：`true`
- `createRequestButton`：`true`
- `routeResponsesThroughCoordinator`：`true`
- `validateSceneOnInstall`：`true`

这些默认值可以支撑开发期自愈式启动，但正式实验前应让 Unity 重新保存场景，使关键配置在 Inspector 和 YAML 中都显式可见。

### 3.5.3 进入 Play Mode 后才会补齐的组件

`GuidanceSceneInstaller` 在 `Awake` 中按下列顺序执行：

```text
查找场景引用
  -> 在 GuidanceManager 上补齐缺失组件
  -> 查找或创建调试 UI
  -> 调用各组件 Configure(...) 注入依赖
  -> 注册 AgentClient / Coordinator / UI 事件
  -> 输出场景契约缺失警告
```

当前主场景没有持久化挂载、但会被 Installer 动态添加到 `GuidanceManager` 的核心组件包括：

| 模块 | 运行时补齐组件 | 主要注入关系 |
|---|---|---|
| Core | `GuidanceCoordinator` | AgentClient、Executor、Task、Navigation、Risk、Validator、Reset、RotationGain |
| Task | `GuidanceTaskController` | 使用代码内默认 `GuidanceTaskDefinition`，随后可被 Dashboard 覆盖 |
| Navigation | `NavigationTargetManager` | Play 时扫描所有 `ExhibitMetadata` 并建立索引 |
| Navigation | `SemanticTargetSelector` | 注入 NavigationTargetManager |
| Navigation | `ArrivalDetector` | 注入 UserPoseTracker 和 NavigationTargetManager |
| Perception | `MotionDetector` | 注入 UserPoseTracker |
| Integration | `XRTrackingAdapter` | 注入 UserPoseTracker；未指定时使用 `Camera.main` 作为头部 Transform |
| RDW | `LocalGuidanceValidator` | 注入 NavigationTargetManager，负责后端响应落地前的本地复验 |
| RDW | `PauseResetController` | 注入 Executor、RejectionPanel 和 RotationGainController |
| RDW | `RDWController` | 注入风险评估器和 PauseResetController |
| RDW | `RotationGainController` | 注入 UserPoseTracker 和 MotionDetector |
| Experiment | `ExperimentLogger` | 默认写入项目根目录 `Logs/TestRuns/*.jsonl` |
| Experiment | `ExperimentDashboard` | 注入空间、位姿、风险、任务、Coordinator、Client 和 Logger，并立即应用当前实验选择 |

还有两个容易误判的场景对象：

- 场景中存在名为 `ExperimentDashboard` 的 GameObject，但其自研组件实际是 `PhysicalSpaceDebugController`。
- 场景中存在名为 `ExperimentLogger` 的空 GameObject，但没有挂 `ExperimentLogger`。

Installer 使用的是 `FindObjectOfType<T>()` 和 `GuidanceManager.AddComponent<T>()`，不会因为 GameObject 名称匹配就把组件加到这两个占位对象上。因此进入 Play 后，真正的 `ExperimentDashboard` 和 `ExperimentLogger` 都位于 `GuidanceManager`，两个同名/近似同名对象仍留在层级中。这会增加排查成本，正式整理场景时应统一归属。

### 3.5.4 表现层和 UI 引用的自动查找规则

当 Inspector 引用为空时，Installer 按**对象名称**查找以下对象：

| 字段 | 查找名称 | 用途 |
|---|---|---|
| `avatarRoot` | `GuideAvatar` | 虚拟导览员根节点 |
| `pointingArm` | `PointingArm_Debug` | 指向动作节点 |
| `narrationText` | `GuidanceTextPanel_Text` | 导览解说文本 |
| `movementHintText` | `MovementHintPanel_Text` | 移动提示文本 |
| `gazeCueText` | `GazeCuePanel_Text` | 注视提示文本 |
| `rejectionText` | `RejectionPanel_Text` | 安全拒绝/网络失败文本 |
| `debugText` | `GuidanceDebugPanel_Text` | 调试状态文本 |

缺少文本时，Installer 会创建 `GuidanceDebugCanvas`、对应 TMP 文本、`EventSystem + StandaloneInputModule`，并可创建临时 `Request Guidance` 按钮。这是桌面开发调试方案，不是 XR 世界空间交互方案。

目前场景已有一个 World Space Canvas，绑定现有 `Main Camera`，但场景中未发现持久化的 `EventSystem`。如果 Installer 创建调试按钮，它会在运行时补一个旧输入模块的 EventSystem。

### 3.5.5 XR 挂载现状与真机缺口

**磁盘中已保存的场景状态：**只有旧普通 `Main Camera`，场景 YAML 中没有 XROrigin、XR Interaction Manager、左右手 Controller 或 Input Action Manager。

**Unity Editor 当前未保存状态：**已经部分接入 XRI/PICO，实时场景共 123 个对象，新增层级如下：

```text
XR Interaction Manager
└─ XRInteractionManager

XR Origin (XR Rig)
├─ XROrigin
├─ InputActionManager（已挂 1 个 InputActionAsset）
├─ PXR_Manager
└─ Camera Offset
   ├─ Main Camera（MainCamera、TrackedPoseDriver）
   ├─ Left Controller（ActionBasedController、XRRayInteractor、LineVisual）
   └─ Right Controller（ActionBasedController、XRRayInteractor、LineVisual）
```

实时参数中，`XROrigin` 的 Tracking Origin Mode 为 `Device`，Camera Y Offset 为 `1.1176`。旧场景相机仍在 `DualWorldGallery/User/XR_Origin_or_UserStart/Main Camera` 下，但已经设为 inactive，并改成 `Camera0` tag；新 XR 相机是当前 `MainCamera`。

实时引用检查还发现两个直接影响运行的缺口：

- `UserPoseTracker.virtualUserRoot` **仍指向旧的 inactive Main Camera**，还没有持久化改指到 `XR Origin (XR Rig)/Camera Offset/Main Camera`。
- `UI/ResearcherCanvas` 是 World Space Canvas，但实时上下文中 `worldCamera` 为空，尚未确认使用新 XR 相机渲染和交互。

`XRTrackingAdapter` 在检测到有效头显后会通过 `Camera.main` 找到新 XR 相机并调用 `ConfigureVirtualUserRoot`，因此运行时可能临时纠正第一项；但正式实验不能依赖设备连接后的隐式修正，Inspector 引用仍应显式改好并保存。

这说明 XR Origin、左右手射线和输入 Action 的基础挂载已经开始做了，但尚未保存，也尚未构成完整可验收状态。实时场景仍未发现：

- 持久化 `EventSystem` 与 XR UI Input Module
- 已确认绑定到新 XR 相机的 World Space Canvas
- 已确认绑定到左右手射线的 XR UI 交互链
- 真机 Loader 初始化和输入 Action 实际响应证据

因此准确结论是：**XR、OpenXR、PICO 包和 Loader 资产已安装，Editor 内已部分搭好 XR Rig，但这些改动尚未保存，UI 和真机链路仍未闭环。**

`XRTrackingAdapter` 会尝试通过 `XRNode.Head` 读取设备位姿，并把本地 `(x,z,yaw)` 写入 `UserPoseTracker`。这段逻辑已经具备，但它不能替代 XR Origin 的 tracking origin、相机偏移、控制器、输入 Action 和 UI 射线配置。

正式 PICO 接入应把当前 Editor 中的结构保存并整理为：

```text
XR Origin (Action-based)
├─ Camera Offset
│  ├─ Main Camera
│  ├─ LeftHand Controller
│  └─ RightHand Controller
├─ XR Interaction Manager
└─ Input Action Manager

Systems
├─ PhysicalSpaceManager
├─ UserPoseTracker
├─ PhysicalRiskEvaluator
└─ GuidanceManager
   ├─ GuidanceSceneInstaller
   ├─ AgentClient
   ├─ GuidanceCoordinator
   ├─ Task / Navigation / RDW components
   ├─ ExperimentDashboard
   └─ ExperimentLogger
```

推荐引用关系：

1. `UserPoseTracker.virtualUserRoot` 指向 XR Origin 下的 `Main Camera`。
2. `XRTrackingAdapter.headsetTransform` 同样指向该相机。
3. World Space Canvas 的 `worldCamera` 指向该相机，并使用 `Tracked Device Graphic Raycaster`。
4. EventSystem 使用 `XR UI Input Module`，避免仅依赖 `StandaloneInputModule`。
5. `Input Action Manager` 挂载 `XRI Default Input Actions`。
6. PICO 真机和 Live Preview 分别验证 Loader 是否实际启动，不要仅以 Asset 存在作为成功依据。

### 3.5.6 建议的正式场景接线顺序

当前自动装配适合快速开发，但实验版本建议逐步转为显式接线：

1. 先备份场景，并在 Unity 中打开 `DualWorldGallery`。
2. 保存并验证当前 XR Origin、相机、左右手控制器、Interaction Manager、Input Action Manager，再补齐 XR UI EventSystem。
3. 将上述运行时补齐的 13 个组件显式挂到 `GuidanceManager`。
4. 在 `GuidanceSceneInstaller` Inspector 中逐项拖入核心组件、表现对象和 TMP 文本引用。
5. 将 `ExperimentDashboard`、`ExperimentLogger` 统一放在 `GuidanceManager`，删除或重命名无组件的占位对象。
6. 确认 `AgentClient` 地址：编辑器连本机可用 `127.0.0.1`；PICO 真机不能用该地址访问电脑后端，应改为开发电脑局域网 IP，例如 `http://192.168.x.x:8000/api/guidance`，并保证防火墙和后端监听地址允许局域网访问。
7. 显式接线稳定后，将 `createMissingCoreComponents` 关闭，以便漏挂组件时立即暴露问题。
8. 正式实验 UI 接管请求后，将 `createDebugUiIfMissing` 和 `createRequestButton` 关闭。
9. 保持 `routeResponsesThroughCoordinator = true`，确保响应经过任务状态、本地安全复验和 Reset 逻辑，而不是直接进入 Executor。
10. 保存场景后退出并重新打开 Unity，确认所有引用仍存在，再跑 EditMode、PlayMode、Live Backend 和 PICO 真机验证。

### 3.5.7 Unity 端最小验收清单

- [ ] Console 无编译错误、Missing Script、空引用异常。
- [ ] Build Settings 只启用预期场景，Android 使用正确包名和 IL2CPP。
- [ ] XR Loader 在目标平台实际初始化成功。
- [ ] 头显移动/转头时，`UserPoseTracker` 的物理位置和朝向持续更新。
- [ ] `PhysicalRiskEvaluator` 每 `0.2s` 更新，边界距离与可视化一致。
- [ ] 4 个物理空间 JSON 都能切换并正确显示边界/障碍物。
- [ ] 8 个展品的 `ExhibitMetadata`、ViewPoint、GazeTarget 均有效。
- [ ] Agent 请求能从 Unity 到达后端；真机使用电脑局域网 IP，不使用 `127.0.0.1`。
- [ ] 后端 approved 响应先经过 `GuidanceCoordinator + LocalGuidanceValidator` 再进入 `GuidanceExecutor`。
- [ ] critical 风险会触发 Pause/Reset、清空引导并取消 Rotation Gain。
- [ ] Narration、Gaze、Movement、Avatar、Highlight、Rejection UI 都能在头显中读取和操作。
- [ ] 选择实验条件后，`Logs/TestRuns` 生成 JSONL，并包含 participant、condition、task、space、risk、response 字段。
- [ ] 关闭自动补齐后重新运行，场景仍能完整工作；这才说明 Inspector 接线真正完成。

## 4. 当前进度（截至 `refactor` 分支 HEAD，commit `70966c7`）

### 4.1 已完成

- **目录重构**：脚本从旧的 `Data/PhysicalSpace/Scene/Utils` 平铺结构，按功能迁移到现在的 12 个模块目录，全程保持 GUID 与 HTTP/JSON 契约不变。
- **运行时核心层从零搭建**：main 分支时完全不存在的 Core/Task/Navigation/Perception/RDW 控制器层，在 refactor 分支中全部补齐（`XRTrackingAdapter`、`ArrivalDetector`、`NavigationTargetManager`、`SemanticTargetSelector`、`MotionDetector`、`LocalGuidanceValidator`、`PauseResetController`、`RDWAffordance`、`RDWController`、`RotationGainController`、`GuidanceTaskController`、`GuidanceTaskDefinition`）。
- **旋转增益生命周期**：状态机 Idle→WaitingForUserTurn→Applying→Completed，含方向一致性检测（用户反向则取消）与临界重置联动，已完成并有 EditMode 测试覆盖。
- **真实头显姿态映射**：`XRTrackingAdapter` 已实现从 `XRNode.Head` 读取真实设备位姿并映射进物理追踪空间的逻辑，EditMode 测试通过，但**未在真实 PICO 设备上验证**。
- **后端流水线拆分**：`app.py` 精简，逻辑拆到 `pipeline.py` + `services/guidance_logger.py`，agent 文件重命名规范化。
- **测试基建**：
  - Unity EditMode 测试 10/10 通过（任务路线、引导校验拒绝逻辑、暂停/重置、运动检测、旋转增益全生命周期、临界重置取消增益、XR 姿态转换、实验矩阵生成、DTO 字段名契约锁定）。
  - 自建 Editor 菜单工具 `Tools > VirtualGuidance > Run Live Backend Validation`：进入 Play Mode 后打真实后端，6/6 检查通过（含真实到达检测 E1→E3→E5、10 条 JSONL 日志）。
  - 后端 pytest 15 个通过（风险几何、affordance 重定向、verifier 拒绝场景、API 契约/健康检查/日志）。
- **实验工具**：6 条件 × 4 任务 × 4 空间（96 组合）矩阵、运行时 Dashboard、JSONL 日志（`Logs/TestRuns/`，特意放在 `Assets/` 外，避免被当资源导入）。
- **Unity 工程健康度**：0 编译错误，主场景 0 缺失脚本（PICO 包自身 prefab 里的 2 个缺失脚本与本项目代码无关），PlayMode 冒烟测试无错误/警告。

### 4.2 尚未验证 / 未完成

- **多条件正式实验数据**尚未采集（工具已就位，但未跑真实被试）。
- **PICO 真机构建**未做（Android/IL2CPP 构建配置存在，但未验证实际打包）。
- **真实头显硬件验证**缺失：真实用户转身检测、旋转增益的真机舒适度、临界风险下的暂停/重置安全性，均未在真实设备上观察到。
- **已保存场景中无 XROrigin / XRInteractionManager / EventSystem**；当前 Editor 的 dirty scene 已加入 XROrigin、XRInteractionManager、左右手射线和 InputActionManager，但尚未保存，仍缺 XR UI EventSystem 和真机闭环验证。
- `RDW/RDWAffordance.cs` 是一个纯 DTO 壳（`interventionType`、`preferredDirection` 等字段无逻辑），目前未见被消费，可能是给未来"Unity 本地 affordance 计算"预留的占位。
- `GuidanceSceneInstaller` 里有个临时调试按钮开关 `createRequestButton`，注释明确写着等 `ExperimentDashboard` 完全接管请求后应关闭——已知的临时开发用具，非遗留问题。

### 4.3 已知风险

- PICO / Live Preview 两个包用本机绝对路径引用，换环境无法直接构建。
- 无 asmdef，全部脚本共用默认装配单元，编译隔离和后续维护成本会随规模上升。
- 后端 `GuideMockAgent` 仍是确定性 mock，真实 Guide MLLM Agent 在路线图上但尚未开工。

## 5. 分支说明

- `main`：refactor 之前的 MVP 快照（commit `6fb4529`），脚本扁平分布，无 Core/Task/Navigation/RDW 控制器层，无旋转增益、无真实 XR 姿态映射、无 Unity 测试。
- `refactor`（当前分支）：在保持 HTTP/JSON 契约、类名、GUID 不变的前提下，按 12 阶段执行计划（P0-P11）从零搭建目标架构，每阶段都有对应的验证记录文档。相比 main 共变更 95 个文件（+3028/-874 行）。

## 6. 相关文档索引

- [`docs/implementation/CODEX_REFACTOR_EXECUTION_PLAN.md`](implementation/CODEX_REFACTOR_EXECUTION_PLAN.md) — 重构总纲：不可变更约束、脚本迁移表、目标模块清单、12 阶段计划
- [`docs/implementation/CODEX_REFACTOR_P1_CONFIRMATION.md`](implementation/CODEX_REFACTOR_P1_CONFIRMATION.md) — P1 阶段确认（场景/编译/Build Scene 注册）
- [`docs/implementation/CODEX_REFACTOR_VALIDATION_REPORT.md`](implementation/CODEX_REFACTOR_VALIDATION_REPORT.md) — 最新验证报告（P1-P11 全部验证证据，含"仍需真机验证"清单）

## 7. 交给 GPT 时的使用说明

可以直接把本文件作为项目上下文，并附上下面这段要求：

```text
请先阅读整份 PROJECT_OVERVIEW.md，再回答或修改项目。

约束：
1. 以 3.5 节的 Unity 挂载现状为最高优先级事实。
2. 严格区分“磁盘已保存场景”“当前 Editor 未保存 dirty scene”“Play Mode 运行时自动装配”。
3. 不要因为当前 Play Mode 能运行，就假设 Inspector 引用已经持久化完成。
4. 不要绕过 GuidanceCoordinator、LocalGuidanceValidator 或 GuidanceExecutor 的安全链路。
5. 修改 XR 配置时优先补齐并保存现有 XR Rig，不要重新创建第二套 XR Origin。
6. PICO 真机访问电脑后端时不能使用 127.0.0.1，必须使用电脑局域网地址并检查防火墙。
7. 提方案时列出需要挂到哪个 GameObject、Inspector 哪个字段、引用哪个对象，以及验证方法。
8. 未经确认不要删除现有场景对象、改变脚本 GUID、HTTP/JSON 字段名或实验条件定义。
```
