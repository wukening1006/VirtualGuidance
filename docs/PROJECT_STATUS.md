# VirtualGuidance 项目现状梳理

> 盘点日期：2026-07-12  
> 工作目录：`D:\SoftwareFiles\MyCode\Unity\VirtualGuidance`  
> 盘点方式：读取 Unity 项目配置、`Assets/Scripts` 自研代码、`Assets/Configs` 共享配置、`backend` 后端代码与测试；未启动 Unity Editor 做编译检查。

## 1. 总览结论

VirtualGuidance 目前是一个以 **Unity XR 虚拟导览前端 + Python FastAPI 后端 agent pipeline** 为核心的研究原型项目。项目重点不是传统游戏流程，而是围绕 **RDW-grounded virtual guidance** 做实验条件对比：Unity 端采集用户 physical/virtual 状态，后端根据物理空间风险、语义目标和实验 condition 返回导览动作；Unity 端再通过 narration、gaze cue、avatar 指向、movement hint、展品高亮和 rejection panel 执行或拒绝 guidance。

当前系统已经具备可运行 MVP 的主要骨架：

- Unity 端有自研的 physical space、risk evaluator、guidance client/executor、debug UI 和场景安装器。
- 后端有确定性 RDW state、affordance、mock guide、safety verifier、JSONL logger 和 FastAPI API。
- `Assets/Configs` 是 Unity 与后端共享的配置中心，包含 4 个 physical tracking space 与 8 个展品语义。
- 后端测试在指定 pytest 临时目录后全部通过：`14 passed`。

同时也存在几个影响后续交付的工程风险：

- `ProjectSettings/EditorBuildSettings.asset` 里没有登记任何 Build Scene。
- PICO XR 与 Live Preview 包使用本机绝对 `file:D:/...` 路径，换机器会失效。
- 自研 Unity 脚本没有 asmdef，后续规模扩大时编译边界和依赖边界会变弱。
- Unity 端 JSON 配置解析暂用正则，适合当前小 schema，但不适合复杂 JSON 扩展。
- 目前只跑了后端 pytest；Unity 编译、Play Mode、XR 真机链路还未在本次盘点中验证。

## 2. 技术基线

### 2.1 Unity 版本与渲染管线

- Unity Editor：`2022.3.62f3c1`
- 项目模板来源：`com.unity.template.3d@8.1.3`
- 产品名：`VirtualGuidance`
- Company：`DefaultCompany`
- 图形管线：Built-in Render Pipeline
- `GraphicsSettings.m_CustomRenderPipeline` 为空，未配置 URP/HDRP。
- Color Space：Linear（`m_ActiveColorSpace: 1`）
- Active Input Handling：Both（`activeInputHandler: 2`）

### 2.2 主要 Unity 包

来自 `Packages/manifest.json`：

- `com.unity.xr.interaction.toolkit`：`2.6.5`
- `com.unity.xr.management`：`4.5.4`
- `com.unity.xr.openxr`：`1.14.3`
- `com.unity.xr.picoxr`：本机路径 `file:D:/SoftwareFiles/Unity/PICO Unity Integration SDK-3.4.0-20260226`
- `com.unity.pico.livepreview`：本机路径 `file:D:/SoftwareFiles/Unity/Unity Live Preview Plugin-1.0.5-20250211`
- `com.unity.textmeshpro`：`3.0.7`
- `com.unity.probuilder`：`5.2.4`
- `com.unity.timeline`：`1.7.7`
- `com.unity.visualscripting`：`1.9.4`
- `com.unity.ugui`：`1.0.0`
- `com.besty.unity-skills`：GitHub URL package，用于 Unity Skills 自动化能力。

### 2.3 构建与平台设置

- Android package id：`com.DefaultCompany.VirtualGuidance`
- Android Min SDK：29
- Android scripting backend：IL2CPP
- Android target architecture：ARM64
- 使用项目内 keystore：`user.keystore`
- Android 自定义 keystore 已启用。
- Build Settings 当前没有任何场景：`m_Scenes: []`

这意味着当前项目可以在 Editor 中打开场景调试，但正式构建前需要将主场景加入 Build Settings。

## 3. 仓库与目录结构

主要目录如下：

- `Assets/`：Unity 资源、场景、脚本、XR 设置、TextMeshPro、XRI samples。
- `Assets/Scripts/`：当前自研 Unity 业务脚本。
- `Assets/Configs/`：Unity 与后端共享的 physical space 和 scene semantics JSON。
- `Assets/Scenes/`：Unity 场景，目前包含 `SampleScene.unity` 和 `DualWorldGallery.unity`。
- `backend/`：FastAPI 后端、agent pipeline、schema、测试。
- `analysis/`：存在 Python cache，未看到本次盘点可读的核心源码文件输出。
- `docs/`：文档目录，本文件位于此处。
- `output/`：历史输出目录，包含 `analysis`、`doc` 等子目录。

Git 状态：

- `git status --short -uno` 无输出，说明 tracked files 在忽略 untracked 时是干净的。
- `git status --short` 会因 `.pytest_cache/` 权限问题产生 warning，建议后续清理或修复该目录权限。

## 4. 场景现状

### 4.1 `Assets/Scenes/SampleScene.unity`

`SampleScene` 基本是模板级场景，只看到：

- `Main Camera`
- `Directional Light`

它更像保留的默认场景，不是当前业务主场景。

### 4.2 `Assets/Scenes/DualWorldGallery.unity`

`DualWorldGallery` 是当前实际主场景。场景内能看到这些核心对象：

- `DualWorldGallery`
- `Environment`
- `Exhibits`
- `Managers`
- `GuidanceManager`
- `PhysicalSpaceManager`
- `PhysicalRiskEvaluator`
- `BoundaryVisualizer`
- `UserPoseTracker`
- `XR_Origin_or_UserStart`
- `User`
- `Main Camera`
- `GuideAvatar`
- `PointingArm_Debug`
- `ResearcherCanvas`
- `ResearcherDebugPanel`
- `TaskPanel`
- `RiskDebugPanel`
- `GuidanceTextPanel`
- `SceneSemanticExporter`
- `ExperimentDashboard`
- `ExperimentLogger`

注意：场景里出现了 `ExperimentDashboard`、`ExperimentLogger`、`SceneSemanticExporter` 对象名称，但当前 `Assets/Scripts` 中没有对应 C# 源码。它们可能是历史组件残留、插件组件、未提交脚本或场景 YAML 中的空对象。后续建议在 Unity Inspector 里确认是否存在 Missing Script。

## 5. Unity 自研脚本结构

当前自研 C# 脚本集中在 `Assets/Scripts`：

```text
Assets/Scripts
├─ Data
│  ├─ ExhibitMetadata.cs
│  ├─ GuidanceTypes.cs
│  └─ PhysicalSpaceTypes.cs
├─ Guidance
│  ├─ AgentClient.cs
│  ├─ GazeCueController.cs
│  ├─ GuidanceDebugPanel.cs
│  ├─ GuidanceExecutor.cs
│  ├─ GuidanceSceneInstaller.cs
│  ├─ GuideAvatarController.cs
│  ├─ MovementHintController.cs
│  └─ NarrationPlayer.cs
├─ PhysicalSpace
│  ├─ BoundaryVisualizer.cs
│  ├─ PhysicalRiskEvaluator.cs
│  ├─ PhysicalSpaceManager.cs
│  └─ UserPoseTracker.cs
├─ Scene
│  └─ ExhibitHighlightManager.cs
└─ Utils
   ├─ PhysicalSpaceDebugController.cs
   ├─ RejectionPanel.cs
   └─ RiskDebugPanel.cs
```

目前未发现自研 asmdef。项目中只有 XR Interaction Toolkit Samples 自带 asmdef。这表示自研脚本仍在默认 Assembly-CSharp 编译单元内。

### 5.1 Data 层

`GuidanceTypes.cs`

- 定义 Unity 与后端 `/api/guidance` 对齐的 DTO。
- 核心类型：`GuidanceRequest`、`GuidanceResponse`、`VerifierResult`、`GuidanceRequestContext`。
- 默认 condition：`ours_full`
- 默认 task：`T2_main_target`
- 默认 current exhibit：`E1`
- 默认 semantic next target：`E5`
- 提供 Vector2/Vector3 到 float array 的工具方法。

`PhysicalSpaceTypes.cs`

- 定义 `PhysicalObstacle`、`PhysicalSpaceConfig`、`PhysicalRiskState`。
- `PhysicalSpaceConfigParser` 使用正则读取 JSON，支持当前固定格式：
  - `id`
  - `boundary`
  - `obstacles`
- 优点：无需引入 Unity JSON 额外依赖。
- 风险：复杂 JSON、转义字符、嵌套 schema、字段顺序变化可能让解析变脆。

`ExhibitMetadata.cs`

- 场景内展品语义元数据组件。
- 字段包括展品 ID、展示名、tags、description、semantic role、related exhibits、view point、gaze target。
- 后续可作为 scene semantics 自动导出与动态 guidance 的桥。

### 5.2 Physical Space 子系统

`PhysicalSpaceManager.cs`

- 从 `Assets/Configs` 加载 physical space JSON。
- 默认空间：`wide_room`
- 当前配置文件列表：
  - `physical_wide_room.json`
  - `physical_narrow_room.json`
  - `physical_obstacle_room.json`
  - `physical_l_shaped_room.json`
- 通过 `SetPhysicalSpace(id)` 切换空间，并发出 `CurrentSpaceChanged` 事件。

`UserPoseTracker.cs`

- 当前主要使用模拟 physical pose。
- 默认 physical position：`(1, 4.3)`
- 默认 yaw：`0`，即朝向 +Z。
- virtual pose 可绑定 `XR_Origin_or_UserStart`，未绑定时自动查找该对象。
- 后续可替换为真实 XR tracking 输入。

`PhysicalRiskEvaluator.cs`

- 按固定频率计算 physical risk，默认 0.2 秒一次。
- 同时考虑边界多边形和圆形障碍物。
- 计算 forward/left/right 三个方向的 clearance。
- 阈值：
  - max clearance：3.0 m
  - unsafe：小于 0.8 m
  - safe：大于等于 1.2 m
  - critical：小于 0.45 m
- 如果用户在 boundary 外，直接标记所有方向 unsafe，`reset_required = true`。

`BoundaryVisualizer.cs`

- 在虚拟展厅旁边绘制 physical debug view。
- 显示 boundary、obstacle、user marker、forward/left/right 方向线。
- 方向颜色表达风险状态：
  - red：unsafe
  - green：safe
  - orange：medium/unknown

`PhysicalSpaceDebugController.cs`

- 运行时用键盘数字键切换 physical space：
  - `1`：wide_room
  - `2`：narrow_room
  - `3`：obstacle_room
  - `4`：l_shaped_room

### 5.3 Guidance 子系统

`AgentClient.cs`

- Unity 到后端的 HTTP client。
- 默认 endpoint：`http://127.0.0.1:8000/api/guidance`
- 默认 timeout：8 秒。
- 负责组装 `GuidanceRequest` 并解析 `GuidanceResponse`。
- 暴露 UnityEvent：
  - `GuidanceReceived`
  - `GuidanceRequestFailed`
  - `GuidanceRequestStarted`
- 有 `requestInFlight` 防重复请求。

`GuidanceExecutor.cs`

- Guidance 执行唯一入口。
- 先检查 `response.verifier.approved`。
- 如果 rejected，只显示 rejection reason，不执行 narration、avatar、movement、高亮或 gaze cue。
- 如果 approved，分发到：
  - `NarrationPlayer`
  - `GuideAvatarController`
  - `MovementHintController`
  - `ExhibitHighlightManager`
  - `GazeCueController`
  - `GuidanceDebugPanel`

`GuidanceSceneInstaller.cs`

- 场景装配器，用于补齐组件、自动连事件、创建临时 debug UI 和 request button。
- 有 fallback：如果场景中存在 physical/guidance 依赖但没有 installer，会在场景加载后尝试挂载。
- 当前适合 MVP 和调试，但正式实验 UI 接管后建议关闭临时 request button。

表现层控制器：

- `NarrationPlayer.cs`：显示后端 narration，目前不做 TTS/口型。
- `MovementHintController.cs`：显示 movement instruction，不直接移动用户。
- `GazeCueController.cs`：显示短暂弱视线提示。
- `GuideAvatarController.cs`：把 avatar action 映射为 debug 指向动作。
- `ExhibitHighlightManager.cs`：根据 `selected_target` 高亮展品。
- `RejectionPanel.cs`：显示 verifier 拒绝原因或请求错误。
- `GuidanceDebugPanel.cs`：显示 response 关键字段。
- `RiskDebugPanel.cs`：显示 physical risk state。

## 6. 共享配置现状

### 6.1 Physical Space 配置

位于 `Assets/Configs`：

| 文件 | ID | 形状/用途 |
| --- | --- | --- |
| `physical_wide_room.json` | `wide_room` | 5m x 5m，无障碍，宽敞基线空间 |
| `physical_narrow_room.json` | `narrow_room` | 2m x 5m，用于触发狭窄前向风险 |
| `physical_obstacle_room.json` | `obstacle_room` | 4m x 4m，包含圆形障碍物 `obstacle_01` |
| `physical_l_shaped_room.json` | `l_shaped_room` | L 形非矩形空间，用于验证 polygon 判断与 reset |

这些配置同时被 Unity 与后端使用，是当前实验一致性的关键。

### 6.2 Scene Semantics

`Assets/Configs/scene_semantics.json` 描述 `dual_world_gallery`，包含 8 个展品：

| ID | 名称 | 语义角色 | 大致方向 |
| --- | --- | --- | --- |
| E1 | Introduction | introduction | 起点/概览 |
| E2 | Early Development | forward_next | 前方下一展品 |
| E3 | Related Mechanism | left_related_anchor | 左侧重定向锚点 |
| E4 | Alternative Example | right_alternative_anchor | 右侧替代锚点 |
| E5 | Key Target | main_target | 主目标 |
| E6 | Side Context | side_context | 左侧上下文 |
| E7 | Comparison Object | comparison_object | 右侧比较对象 |
| E8 | Summary | summary | 结尾/总结 |

后端 `/health` 会读取该文件并报告 `exhibit_count = 8`。

## 7. 后端现状

### 7.1 技术栈

`backend/requirements.txt`：

- FastAPI：`>=0.115,<1.0`
- Uvicorn：`>=0.30,<1.0`
- Pydantic：`>=2.0,<3.0`
- Pytest：`>=8.0,<9.0`
- HTTPX：`>=0.27,<1.0`

后端版本号在 `backend/app.py`：

- `VERSION = "0.3.0"`

### 7.2 API

`backend/app.py` 暴露：

- `GET /health`
  - 返回后端 status、version、physical spaces、exhibit count。
- `POST /api/guidance`
  - 输入 Unity 发送的 `GuidanceRequest`。
  - 输出 `GuidanceResponse`，附带 verifier 结果。

后端默认配置目录：

- `Assets/Configs`

后端默认日志目录：

- `backend/logs`

### 7.3 Pipeline 顺序

后端 pipeline 是确定性的轻量 agent 编排：

```text
GuidanceRequest
  -> RDWStateAgent.evaluate(...)
  -> RDWAffordanceAgent.build(...)
  -> GuideMockAgent.build(...)
  -> SafetyVerifierAgent.verify(...)
  -> GuidanceLogger.write(...)
  -> GuidanceResponse
```

各模块职责：

- `RDWStateAgent`：读取 physical config，计算 forward/left/right clearance、risk label、safe/unsafe directions、reset_required。
- `RDWAffordanceAgent`：把 risk state 转成导览 affordance。前方 unsafe 时优先语义重定向到左/右相关展品。
- `GuideMockAgent`：按实验 condition 生成确定性 guidance，包含 baseline 与 proposed condition。
- `SafetyVerifierAgent`：只对 `ours_full` 应用最终安全门；baseline 保留原样输出，方便比较 unsafe guidance。
- `GuidanceLogger`：把 request、risk、draft、verifier 写入稳定 JSONL 日志。

### 7.4 实验条件

`backend/schemas.py` 定义的 condition：

- `B1_mllm_only`
- `B2_raw_physical_data`
- `B3_risk_labels`
- `B4_scripted_reset`
- `B5_ours_without_verifier`
- `ours_full`

关键语义：

- Baseline condition 会保留可能 unsafe 的输出，用于实验对比。
- `ours_full` 才会执行 final SafetyVerifier。
- `B5_ours_without_verifier` 内有稳定失败案例，用来证明缺少最终 verifier 的风险。

## 8. 测试与验证状态

### 8.1 后端测试

本次执行：

```powershell
python -m pytest backend\tests --basetemp=.tmp\pytest
```

结果：

```text
14 passed, 2 warnings
```

覆盖范围：

- `test_rdw_state.py`
  - narrow room 前方 unsafe
  - narrow room 左侧 safe pose
  - obstacle room 前方障碍
  - obstacle room 左侧障碍
  - L-shaped notch outside 触发 reset
- `test_agents.py`
  - affordance 在前方 unsafe 时选择语义重定向
  - verifier 拒绝 unsafe forward movement
  - verifier 拒绝 unsafe selected target
  - B5 稳定输出缺 verifier 的 unsafe forward
- `test_api.py`
  - health 报告配置加载状态
  - B1/B4/B5/ours_full API 行为
  - JSONL 日志记录

### 8.2 测试环境注意事项

直接运行：

```powershell
python -m pytest backend\tests
```

首次失败原因不是业务逻辑，而是 pytest 默认临时目录无权限：

```text
PermissionError: C:\Users\WKN\AppData\Local\Temp\pytest-of-WKN
```

指定仓库内临时目录后通过：

```powershell
python -m pytest backend\tests --basetemp=.tmp\pytest
```

仍有 warning：

- Starlette/FastAPI TestClient 关于 `httpx` 的弃用提示。
- `.pytest_cache` 目录无权限导致无法写 cache。

建议后续修复 `.pytest_cache` 权限，或在 CI/本地脚本中固定 `--basetemp` 与禁用 cache。

### 8.3 尚未验证

本次未执行：

- Unity Editor 编译检查。
- Unity Test Runner。
- Play Mode 路径测试。
- Android/PICO 真机构建。
- PICO Live Preview 运行。
- 后端服务与 Unity Play Mode 的端到端手测。

## 9. 当前架构信号

### 9.1 已形成的架构边界

项目已经自然分成几层：

- 配置层：`Assets/Configs`
- Unity 数据契约层：`Assets/Scripts/Data`
- Unity physical risk 层：`Assets/Scripts/PhysicalSpace`
- Unity guidance 执行层：`Assets/Scripts/Guidance`
- Unity scene utility 层：`Assets/Scripts/Scene`、`Assets/Scripts/Utils`
- Python backend agent 层：`backend/agents`
- Python schema/API 层：`backend/schemas.py`、`backend/app.py`

这个分层适合继续保持。后续建议不要把后端策略逻辑迁回 Unity，也不要让 Unity 表现层绕过 `GuidanceExecutor` 直接执行 response。

### 9.2 值得保留的约定

- Unity DTO 字段名与 Pydantic schema 对齐，避免额外映射层。
- `Assets/Configs` 同时服务 Unity 与后端，减少实验参数漂移。
- Guidance 执行统一经过 verifier gate。
- Baseline 和 proposed condition 使用同一日志 schema。
- 风险计算是确定性的，便于复现实验条件。
- public 类、字段、方法已有中文 XML 注释，符合 `Assets/Scripts/AGENT.md` 规范。

## 10. 主要风险与建议优先级

### P0：Build Settings 没有场景

`EditorBuildSettings.asset` 的 `m_Scenes` 为空。正式构建前至少应加入：

- `Assets/Scenes/DualWorldGallery.unity`

否则 Android/PICO 构建入口不明确。

### P0：确认 Unity 场景是否有 Missing Script

`DualWorldGallery.unity` 中能看到 `ExperimentDashboard`、`ExperimentLogger`、`SceneSemanticExporter` 等对象名称，但当前自研脚本目录没有对应源码。建议在 Unity Console/Inspector 中确认：

- 是否存在 Missing Script。
- 是否来自插件或历史对象。
- 是否需要补回源码或清理对象。

### P1：本机绝对路径包依赖

两个包使用本机 `file:D:/...`：

- PICO Unity Integration SDK
- PICO Live Preview Plugin

这会影响他人 clone、CI、换机器和长期复现。建议：

- 固定到团队共享路径、UPM tarball、Git package 或 vendor package。
- 在文档中写清版本和安装位置。

### P1：Unity 端 JSON parser 后续要替换

当前 `PhysicalSpaceConfigParser` 对固定小 JSON 可用，但不建议长期扩展。后续可以考虑：

- 使用 Unity `JsonUtility`，前提是调整 JSON 结构为 Unity 可直接反序列化格式。
- 使用 Newtonsoft.Json for Unity，如果项目接受依赖。
- 由后端生成 Unity 友好的 ScriptableObject/asset。

### P1：补 Unity 编译与 Play Mode 验证

后端测试已通过，但 Unity 侧还没有自动测试。本项目核心风险在 Unity 与后端接口、场景引用和运行时对象接线，建议新增：

- Editor test：验证 `Assets/Configs` 能被 Unity parser 读取。
- PlayMode test：验证 `GuidanceExecutor` 对 approved/rejected response 的行为。
- Scene contract check：验证主场景关键对象和组件存在。

### P2：建立 asmdef 边界

当前自研脚本没有 asmdef。随着系统变大，建议拆出：

- `VirtualGuidance.Data`
- `VirtualGuidance.PhysicalSpace`
- `VirtualGuidance.Guidance`
- `VirtualGuidance.Tests`

先不要过度拆分，等 Unity 测试落地时一起做更自然。

### P2：后端 TestClient 依赖 warning

测试通过但有：

```text
StarletteDeprecationWarning: Using httpx with starlette.testclient is deprecated; install httpx2 instead.
```

短期可忽略；若后续 CI 严格处理 warning，需要升级/调整测试依赖。

## 11. 建议下一步路线

1. 先修工程入口：把 `DualWorldGallery.unity` 加入 Build Settings，并确认主场景无 Missing Script。
2. 固化运行说明：补 `README.md`，写清后端启动、Unity Play Mode、PICO SDK 路径、测试命令。
3. 加 Unity 侧最小自动检查：scene contract + parser test + executor verifier gate test。
4. 做一次 Unity/后端端到端手测：启动 `uvicorn backend.app:app --reload`，Play Mode 点击 Request Guidance，验证 rejected/approved 路径。
5. 再考虑重构：替换 JSON parser、补 asmdef、整理 PICO 本地包依赖。

## 12. 快速命令备忘

后端测试：

```powershell
python -m pytest backend\tests --basetemp=.tmp\pytest
```

后端服务启动：

```powershell
uvicorn backend.app:app --reload
```

Unity 默认请求地址：

```text
http://127.0.0.1:8000/api/guidance
```

健康检查：

```text
GET http://127.0.0.1:8000/health
```

运行时 physical space 切换：

```text
1 -> wide_room
2 -> narrow_room
3 -> obstacle_room
4 -> l_shaped_room
```

## 13. 当前判断

这个项目已经从“概念草图”进入了“可验证 MVP 骨架”阶段。后端策略链路和测试相对扎实，Unity 端也已经有清晰的执行路径和调试可视化。现在最值得投入的不是大改架构，而是补齐工程化闭环：Build Settings、场景健康检查、Unity 自动测试、运行说明和本地包依赖固化。

