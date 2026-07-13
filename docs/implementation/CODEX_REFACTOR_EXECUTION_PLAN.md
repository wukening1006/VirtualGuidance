# VirtualGuidance 重构实施计划

> 审计日期：2026-07-13  
> 审计依据：实际仓库内容与 `VirtualGuidance_项目系统说明与架构设计.md`  
> 状态：实施前草案，尚未执行  
> 核心原则：先保持行为与 GUID，再整理目录；目录迁移与功能开发严格分离。

## 1. 审计边界

本计划锁定以下约束：

- 不改变 Unity 与后端现有 HTTP/JSON 数据契约。
- 不在目录迁移阶段修改类名、序列化字段或公开方法。
- 不修改 `.unity` 场景内容。
- 不添加第三方依赖。
- 不在目录迁移阶段创建 asmdef。
- 不把设计文档中的建议类视为现有实现。
- 所有 Unity 资产必须与原 `.meta` 一起移动，GUID 不得重新生成。
- `Library/`、`Temp/`、生成日志、第三方 Samples 和 PICO 资源不属于自研迁移范围。

## 2. 当前技术基线

- Unity：`2022.3.62f3c1`
- 渲染管线：Built-in Render Pipeline
- XR：XR Interaction Toolkit 2.6.5、OpenXR 1.14.3、PICO 本地路径包
- 自研 Unity C#：19 个，全部位于默认 `Assembly-CSharp`
- 自研 asmdef：不存在
- 自研 Unity Tests：不存在
- Python 后端：FastAPI + Pydantic，13 个 Python 文件
- 共享配置：`Assets/Configs` 下 5 个 JSON
- 业务场景：`Assets/Scenes/DualWorldGallery.unity`
- 模板场景：`Assets/Scenes/SampleScene.unity`
- Build Settings：`m_Scenes: []`，当前没有正式登记的主场景
- 当前工作树不是干净基线：
  - `Assets/Scenes/DualWorldGallery.unity` 已有未提交修改
  - `docs/PROJECT_STATUS.md` 已暂存新增
- `docs/PROJECT_STATUS.md` 记录 2026-07-12 后端测试为 `14 passed`；本轮因只读约束未重新执行。

## 3. 当前目录与目标目录差异

| 当前实际目录 | 文件数/状态 | 文档目标 |
|---|---:|---|
| `Scripts/Data` | 3 | 拆到 Guidance、RDW、Navigation |
| `Scripts/Guidance` | 8 | 保留表现层；AgentClient、DebugPanel 移出 |
| `Scripts/PhysicalSpace` | 4 | 拆到 RDW、Perception、Diagnostics |
| `Scripts/Scene` | 1 | 移到 Guidance |
| `Scripts/Utils` | 3 | 移到 Diagnostics |
| `Scripts/Experiment` | 空目录 | 目标模块尚未实现 |
| Core、Task、Navigation、Perception、RDW、Integration、Diagnostics | 目录不存在 | 需要按实际文件逐步建立 |
| `Assets/Prefarbs` | 1 个 Prefab，目录拼写错误 | `Assets/Prefabs/Exhibits` |
| `Assets/UI` | 不存在 | 文档建议 Runtime、Researcher |
| `backend/pipeline.py`、`services/`、`prompts/` | 不存在 | 文档目标结构 |

不创建无内容目录。目标目录只在首个真实文件迁入或新增时建立。

## 4. Unity 自研脚本迁移清单

每个 `.cs` 的 `.meta` 必须同步移动。风险标记：`S` 为主场景直接引用，`P` 为 Prefab 引用，`R` 为运行时动态发现，`-` 为无序列化引用。

| 当前文件 | 目标文件 | 引用/处理 |
|---|---|---|
| `Data/GuidanceTypes.cs` | `Guidance/GuidanceTypes.cs` | `-`，锁定 HTTP DTO |
| `Data/PhysicalSpaceTypes.cs` | `RDW/PhysicalSpaceTypes.cs` | `-`，包含现有 `PhysicalRiskState` |
| `Data/ExhibitMetadata.cs` | `Navigation/ExhibitMetadata.cs` | `P`，展品 Prefab 引用 |
| `PhysicalSpace/PhysicalSpaceManager.cs` | `RDW/PhysicalSpaceManager.cs` | `S` |
| `PhysicalSpace/PhysicalRiskEvaluator.cs` | `RDW/PhysicalRiskEvaluator.cs` | `S` |
| `PhysicalSpace/UserPoseTracker.cs` | `Perception/UserPoseTracker.cs` | `S` |
| `PhysicalSpace/BoundaryVisualizer.cs` | `Diagnostics/BoundaryVisualizer.cs` | `S` |
| `Guidance/AgentClient.cs` | `Integration/AgentClient.cs` | `S` |
| `Guidance/GuidanceExecutor.cs` | 原位保留 | `S` |
| `Guidance/NarrationPlayer.cs` | 原位保留 | `S` |
| `Guidance/GazeCueController.cs` | 原位保留 | `S` |
| `Guidance/GuideAvatarController.cs` | 原位保留 | `S` |
| `Guidance/MovementHintController.cs` | 原位保留 | `S` |
| `Guidance/GuidanceDebugPanel.cs` | `Diagnostics/GuidanceDebugPanel.cs` | `R`，由 Installer 查找/补齐 |
| `Guidance/GuidanceSceneInstaller.cs` | 暂留 `Guidance/` | `S`，文档迁移表未列出但仓库实际存在 |
| `Scene/ExhibitHighlightManager.cs` | `Guidance/ExhibitHighlightManager.cs` | `S` |
| `Utils/PhysicalSpaceDebugController.cs` | `Diagnostics/PhysicalSpaceDebugController.cs` | `R` |
| `Utils/RejectionPanel.cs` | `Diagnostics/RejectionPanel.cs` | `S` |
| `Utils/RiskDebugPanel.cs` | `Diagnostics/RiskDebugPanel.cs` | `S` |

`GuidanceSceneInstaller` 不应仅因名称相似而直接替换为 `SystemBootstrapper`。替换涉及职责和运行行为，应留到 Core 阶段。

## 5. 后端自研文件目标

| 当前文件 | 建议目标 | 说明 |
|---|---|---|
| `backend/__init__.py` | 原位 | 保持包入口 |
| `backend/app.py` | 原位 | 后续只保留 API；Pipeline 另行提取 |
| `backend/schemas.py` | 原位 | 数据契约锁定 |
| `backend/agents/__init__.py` | 原位 | 随导入路径更新 |
| `backend/agents/rdw_state.py` | `agents/rdw_state_agent.py` | 独立重命名阶段 |
| `backend/agents/rdw_affordance.py` | `agents/rdw_affordance_agent.py` | 独立重命名阶段 |
| `backend/agents/safety_verifier.py` | `agents/safety_verifier_agent.py` | 独立重命名阶段 |
| `backend/agents/logger.py` | `services/guidance_logger.py` | 职责属于 service |
| `backend/agents/guide_mock.py` | 暂时原位 | 这是确定性 Mock，不能假称 MLLM Agent |
| `backend/tests/__init__.py` | 原位 | 测试包 |
| `backend/tests/test_agents.py` | 原位 | 更新导入，不改变断言语义 |
| `backend/tests/test_api.py` | 原位 | 作为契约回归测试 |
| `backend/tests/test_rdw_state.py` | 原位 | 风险算法回归测试 |
| `backend/requirements.txt` | 原位 | 不增加依赖 |

`backend/pipeline.py` 是从 `app.py` 提取的新文件，不是已有文件迁移。`guide_mllm_agent.py`、`model_provider.py` 和 prompts 是未来新增实现，不能由重命名 Mock 伪造。

## 6. 其他自研资产

- 5 个 `Assets/Configs/*.json`：原位保留，不改变字段或内容。
- 2 个 `Assets/Scenes/*.unity`：原位保留，不在目录重构中保存或重序列化。
- 10 个 `Assets/Materials/DG_*.mat`：原位保留。
- `Assets/Prefarbs/Exihibition.prefab`：
  - 目标为 `Assets/Prefabs/Exhibits/Exihibition.prefab`
  - 首次只改目录，不同时修正 `Exihibition` 文件名拼写
  - 必须携带原 `.meta`
- `Assets/Scripts/AGENT.md`：保留在 `Assets/Scripts/`
- `docs/PROJECT_STATUS.md`：原位保留
- `backend/logs/*.jsonl`：生成数据，不参与目录重构

## 7. GUID、场景与 Prefab 审计

`DualWorldGallery.unity` 当前包含 26 个 MonoBehaviour 块：

- 14 个可解析到现有自研脚本
- 10 个可解析到 Unity UI/TextMeshPro 包
- 2 个 GUID 无法在 `Assets` 或已缓存 Package 中解析

未解析项均位于当前 `Experiment` 对象：

| GUID | fileID | 序列化信号 | 判断 |
|---|---:|---|---|
| `2258bd4f02d64855a244664d71db7081` | `1220635952` | `autoFlush`、`context`、`logPath` | 疑似实验日志组件，但源码不存在 |
| `e7b2472d417d49f19b5d83f0c10aee1c` | `1220635953` | participant、condition、task、`experimentLogger` | 疑似实验控制组件，但源码不存在 |

以上只能判定为“静态无法解析”，最终 Missing Script 状态必须由 Unity Editor 确认。不得为匹配场景而手工制造同 GUID 的新 `.meta`。

其他引用结论：

- 主场景直接引用 14 个现有自研 MonoBehaviour。
- `Exihibition.prefab` 引用 `ExhibitMetadata.cs`，其 GUID 为 `4433b0837b74ea04283dd4c30f291c86`。
- 主场景包含该 Prefab 的 8 个实例，因此移动 Prefab 本身属于高影响资产迁移。
- Prefab 内 3 个 MonoBehaviour 块目前都可静态解析。
- 未发现 `m_Script: {fileID: 0}` 形式的显式空脚本引用。
- `GuidanceDebugPanel` 与 `PhysicalSpaceDebugController` 没有直接序列化引用，但可能由运行时安装逻辑发现或添加。

## 8. 文档与仓库不一致项

1. 文档描述的是目标架构，不是当前实现。
2. 文档目标存在九个 Unity 子系统；仓库实际只有五个非空业务目录。
3. 文档的迁移表没有正式列入 `GuidanceSceneInstaller.cs`。
4. 文档列出独立 `PhysicalRiskState.cs`，实际类位于 `PhysicalSpaceTypes.cs`；拆文件属于代码修改，不能混入目录迁移。
5. 文档列出 ExperimentDashboard 和 ExperimentLogger；当前源码不存在，场景却有两个不可解析 GUID。
6. 文档建议 `Prefabs/Exhibits`，当前实际目录为拼写错误的 `Prefarbs`。
7. 文档建议 `pipeline.py`、services 和 prompts；当前 Pipeline、配置加载和日志职责仍分散在现有文件中。
8. 文档建议 Guide MLLM Agent；当前只有确定性 `GuideMockAgent`。
9. 文档建议的 request/response 含 `final_target`、`current_target`、`request_id`、`state_version`、RDW state 等字段；当前 HTTP 契约不包含这些字段。
10. Build Settings 没有场景，不能把 `DualWorldGallery` 视为已配置构建入口。
11. 当前没有自研 asmdef 或 Unity 自动化测试。
12. Unity 编译、Play Mode、PICO 构建与 XR 真机状态本轮均未知。

## 9. 数据契约冻结

当前 Unity 请求字段保持：

`condition`、`task_id`、`physical_space_id`、`current_exhibit`、`user_physical_position`、`user_physical_forward`、`participant_id`、`user_task`、`semantic_next_target`、`user_virtual_position`、`user_virtual_forward`。

当前响应字段保持：

`selected_target`、`route_decision`、`narration`、`gaze_cue`、`gaze_target_id`、`avatar_action`、`movement_instruction`、`physical_intent`、`verifier`。

后续 Core 可在 Unity 内部维护 final/current target 和本地状态版本，但不得在本计划内扩展 HTTP JSON。运行时过期检查应绑定本地请求快照，并利用当前单请求 in-flight 约束。

## 10. 尚不存在的目标模块

| 子系统 | 当前存在 | 尚不存在或未独立实现 |
|---|---|---|
| Core | 无 | 5 个目标脚本全部缺失 |
| Task | 无 | 4 个目标脚本全部缺失 |
| Navigation | ExhibitMetadata | Manager、Target、Selector、ArrivalDetector |
| Perception | UserPoseTracker | MotionDetector、UserStateSnapshot |
| RDW | Types、Manager、RiskEvaluator | Affordance、Controller、RotationGain、Reset；RiskState 未独立成文件 |
| Guidance | DTO、Executor、6 个表现组件 | Planner、RuntimeValidator |
| Integration | AgentClient | ConfigurationLoader、XRTrackingAdapter |
| Experiment | 仅空目录 | 4 个目标源码均不存在 |
| Diagnostics | 5 个现有工具 | StateDebugPanel、SceneHealthChecker |
| Backend | 确定性 Pipeline 功能 | 独立 pipeline/services/prompts、真实 MLLM/provider |

## 11. 分阶段实施计划

验证环境：`F` 文件/静态检查，`B` 后端测试，`UE` Unity Editor，`PM` Play Mode，`XR` XR 设备。

| 阶段 | 修改范围 | 明确不修改 | 前置条件 | 验证与完成标准 | 回滚 | 环境 |
|---|---|---|---|---|---|---|
| P0 基线冻结 | 建立专用分支/提交；记录 GUID、路径、契约和当前错误 | 不移动资产、不保存场景 | 先处理当前 dirty worktree 的归属 | GUID manifest 完整；工作树基线可复现；记录 Unity 是否编译及现有 Missing 数 | 无业务回滚；删除审计产物或回到基线提交 | F、UE |
| P1 阻塞项确认 | 确认两个未解析 GUID 的来源；登记 `DualWorldGallery` 为 Build Scene | 不猜测或伪造脚本；不改场景对象 | P0 完成；可取得原始脚本时必须连原 `.meta` 恢复 | 严格标准为零 Missing；若源码无法取得，只能批准“现有 2 个、迁移后不得增加”的临时标准 | 恢复本阶段提交；Build Settings 单独回退 | UE |
| P2 低引用脚本迁移 | 移动 GuidanceTypes、PhysicalSpaceTypes、GuidanceDebugPanel、PhysicalSpaceDebugController 及 `.meta` | 不拆类、不加 asmdef、不改契约 | P1 的 Missing 标准已确定 | GUID 前后相同；仅出现 move；Unity 无新增编译错误；后端测试不受影响 | 通过 Unity 移回原路径并保留 `.meta` | F、UE、B |
| P3 场景/Prefab 引用脚本迁移 | 移动 ExhibitMetadata、AgentClient、PhysicalSpaceManager、RiskEvaluator、UserPoseTracker、BoundaryVisualizer、HighlightManager、RejectionPanel、RiskDebugPanel | 不移动场景和 Prefab；不保存场景；Installer 留在 Guidance | P2 通过；记录迁移前引用计数 | 主场景 14 个自研引用仍可解析；Prefab 引用有效；无新增 Missing；approved/rejected Play 流程不变 | 按清单反向移动；不得重新导入生成新 meta | F、UE、PM、B |
| P4 Prefab 目录修正 | 移动 `Prefarbs/Exihibition.prefab` 到 `Prefabs/Exhibits`，携带 `.meta` | 不重命名 Prefab；不应用/清除 overrides；不改场景 | P3 通过；记录 Prefab GUID 和 8 个实例 | GUID 不变；8 个实例仍指向同一 Prefab；Overrides 数量和内容不变 | 通过 Unity 移回原目录 | F、UE、PM |
| P5 后端目录整理 | 提取 `pipeline.py`；移动 logger；按目标重命名三个 agent 文件；更新 imports/tests | 不改 endpoint、Pydantic 字段、条件行为、依赖版本；Mock 不冒充 MLLM | 当前后端测试可在隔离临时目录运行 | 全部现有测试通过；OpenAPI schema 与 JSON fixture 键集合不变；日志 schema 不变 | 回退单一后端提交并恢复 imports | F、B |
| P6 Core 与启动入口 | 新增内部状态、上下文、协调器；逐步接管 Installer 的编排职责 | 不改 HTTP DTO；不删除 Installer；不修改场景 | P3/P5 稳定；有 Unity 编译基线 | 单一入口驱动 Idle→Navigating；无重复请求；旧请求按钮流程仍可用 | 默认关闭新协调器或回退阶段提交 | UE、PM |
| P7 Task 与 Navigation | 新增 final/current target、到达检测、语义候选选择 | 不改变后端 task_id/semantic_next_target 契约 | P6 稳定；展品 E1–E8 元数据可读 | E1→E3→E5→Completed 可重复；候选只来自现有语义配置 | 关闭新规划路径并恢复旧默认上下文 | UE、PM |
| P8 风险干预与运行时验证 | 新增 RDW affordance、本地 Guidance 过期与执行前验证 | 不让 MLLM 决定风险；不改后端 schema | P7 稳定；RiskEvaluator 基线可复现 | 前方转危险后旧 forward Guidance 不执行；无安全候选时不继续导航 | feature flag 关闭干预路径或回退提交 | UE、PM、B |
| P9 Rotation Gain | 新增 RDWController、真实转向检测、Gain 生命周期和反馈 | 不在话术发出时立即启动 Gain；不改场景资产 | P8 稳定；XRTrackingAdapter 明确；Gain 限值批准 | 模拟器验证状态机；真机验证实际转向后启动、完成/反向/停止时终止 | Gain 默认禁用；回退 Gain 阶段提交 | UE、PM、XR |
| P10 Pause/Reset | 新增 critical 本地停止、取消 Guidance/Gain、恢复流程 | 不等待后端处理紧急风险 | P9 的停止与取消可靠 | 全方向危险时停止；reset 后重新评估；无残留 Gain | 禁用 Reset 自动恢复并回退提交 | UE、PM、XR |
| P11 Experiment 完善 | 恢复或新增合法 Experiment 源码、session logging、Dashboard、条件矩阵 | 不复用两个未知 GUID；不改变既有 baseline 语义 | P0 未解析组件已处理；P10 闭环稳定 | B1–B5/Ours、T1–T4、四空间可复现；日志字段稳定 | 关闭实验入口并回退阶段提交 | B、UE、PM；最终需 XR |

## 12. 验证分级

仅文件和后端验证可以覆盖：

- 路径、`.meta`、GUID 一致性
- Git move 检查和契约 key 快照
- 后端 import、API、risk、verifier、日志回归
- 目标目录与迁移 manifest 完整性

必须使用 Unity Editor：

- C# 实际编译状态
- Missing Script 和序列化引用
- Prefab 源与 overrides
- Build Settings
- Unity 重新导入后 GUID 解析

必须使用 Play Mode：

- Installer 自动接线
- Guidance approved/rejected 分支
- Risk 事件、状态机、到达检测和取消逻辑
- Prefab 高亮、Avatar、Gaze、Movement UI

必须使用 XR 设备：

- physical/virtual pose 映射
- 用户真实开始转向的判定
- Rotation Gain 的方向、幅度、舒适度和停止条件
- critical 风险、pause/reset 的真实安全性
- PICO 构建、权限、Live Preview/真机链路

XR Simulator 可用于开发预检，但不能替代 Gain 和 Reset 的真机验收。

## 13. 全局完成标准

- 所有迁移资产 GUID 与基线一致。
- 主场景和 Prefab 没有新增 Missing Script。
- 已知两个未解析 Experiment GUID 已恢复原始来源，或被明确登记为迁移前遗留阻塞。
- Unity 与后端现有请求/响应字段完全不变。
- 后端现有条件行为和测试结果不退化。
- approved/rejected Guidance 的用户可见行为不变。
- 目录迁移、后端整理、Core、Navigation、RDW、Gain、Reset、Experiment 各有独立提交。
- 任一阶段都可在不覆盖其他阶段和用户现有场景工作的前提下单独回滚。

## 14. 当前建议的执行顺序

先完成 P0 和 P1。两个 Experiment GUID、当前 dirty 场景以及空 Build Settings 是目录迁移前的三个主要门槛。

P2–P4 只做 Unity 资产归位，不开发新功能。P5 独立整理后端。P6 之后才开始实现设计文档中的新架构能力。

在 P4 完成之前，不创建 asmdef、不重命名类、不拆分 DTO、不替换 `GuidanceSceneInstaller`，也不修正 `Exihibition` 文件名拼写。
