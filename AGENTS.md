# AGENTS.md

本文件是写给 Codex / Unity-Skills / 其他 AI Coding Agent 的项目级协作说明。  
目标：让 Agent 在不反复询问背景的情况下，安全、稳定、分阶段地推进 **RDW-Grounded MLLM Virtual Guidance** Unity 实验系统。

> 文件应放在仓库根目录。若子目录存在 `AGENTS.override.md`，以更靠近当前工作目录的说明为准。

---

## 1. 项目概述

本项目实现一个 **RDW-Grounded MLLM 虚拟导览实验系统**。

核心思想：

- 虚拟展厅保持不变。
- 用户导览任务保持不变。
- 真实 physical tracking space 可以切换。
- 系统利用 RDW / tracking-space / reset-risk 信息干预 MLLM 的导览输出。
- 目标不是让大模型直接做底层 RDW 控制，而是让物理空间约束影响导览路线、讲解文本、视线提示、avatar 行为与安全验证结果。

最终 demo 应展示：

> 同一虚拟场景、同一导览任务，因为 physical space 不同，系统输出不同但合理的路线、话术、视线提示和 avatar 行为。

---

## 2. 当前开发目标

当前版本优先实现一个可运行、可复现、可记录日志的最小实验系统。

### 第一阶段目标

- 搭建 `DualWorldGallery.unity` 通用虚拟展厅。
- 放置 8 个展品 E1-E8。
- 每个展品包含 `ViewPoint`、`GazeTarget`、`ExhibitMetadata.cs`。
- 实现基础 physical space 配置与可视化。
- 实现 mock / rule-based guidance，第一版不依赖真实 MLLM。

### 后续阶段目标

- 接入多 Agent 后端。
- 支持 RDWStateAgent、RDWAffordanceAgent、GuideMLLMAgent、SafetyVerifierAgent。
- 支持 baseline mode 切换。
- 支持实验日志 JSONL 输出。
- 支持 demo 录屏与后续用户实验。

---

## 3. 重要边界

必须遵守：

- 不要把本项目写成普通展厅应用 demo；它是实验系统。
- 不要绑定具体文化遗产、摄影展、博物馆主题；第一版保持通用知识 / 科技 / 历史发展展厅。
- 不追求复杂美术资产；优先保证实验可控、布局准确、日志完整。
- 不让 LLM 直接判断底层安全边界。
- 不让 LLM 直接执行碰撞规避、边界控制或 RDW gain 控制。
- Unity 只执行 `SafetyVerifierAgent.approved == true` 的 guidance。
- 如果 safety verifier 拒绝输出，Unity 应显示拒绝原因并请求重新生成或进入 pause/reset 流程。
- 不要删除用户已有场景、素材、配置、Git 历史或日志，除非用户明确要求。
- 不要大规模重构无关代码。
- 不要新增大型第三方依赖，除非先说明原因并获得确认。

---

## 4. 推荐仓库结构

```text
Assets/
├── Scenes/
│   └── DualWorldGallery.unity
├── Scripts/
│   ├── ExhibitMetadata.cs
│   ├── SceneSemanticExporter.cs
│   ├── PhysicalSpaceManager.cs
│   ├── BoundaryVisualizer.cs
│   ├── UserPoseTracker.cs
│   ├── AgentClient.cs
│   ├── GuideAvatarController.cs
│   ├── GazeCueController.cs
│   ├── MovementHintController.cs
│   ├── NarrationPlayer.cs
│   ├── ExperimentDashboard.cs
│   └── ExperimentLogger.cs
├── Configs/
│   ├── scene_semantics.json
│   ├── physical_wide_room.json
│   ├── physical_narrow_room.json
│   ├── physical_obstacle_room.json
│   └── physical_l_shaped_room.json
└── Logs/

backend/
├── app.py
├── agents/
│   ├── rdw_state_agent.py
│   ├── rdw_affordance_agent.py
│   ├── guide_mllm_agent.py
│   └── safety_verifier_agent.py
├── prompts/
└── logs/
```

说明：

- `Assets/Scenes/`：Unity 场景。
- `Assets/Scripts/`：Unity C# 脚本。
- `Assets/Configs/`：Unity 和后端共享配置。
- `Assets/Logs/`：Unity 端实验日志。
- `backend/`：Python mock service 或真实多 Agent 后端。

---

## 5. Unity 场景规范

### 场景名称

```text
DualWorldGallery.unity
```

### 坐标规范

- Unity 使用 X-Z 平面作为地面。
- Y 轴为高度。
- 虚拟展厅尺寸：`12m x 12m`。
- 地面坐标范围：`X = -6 到 6`，`Z = 0 到 12`。
- 用户初始位置：`(0, 0, 1.0)`。
- 用户初始朝向：`+Z`。

### 展厅布局

展厅必须支持：

```text
中轴前方主路线 + 左右语义替代路线 + 后方辅助目标 + 出口总结目标
```

不要把 E1-E8 放成单一直线。E3/E4/E6/E7 必须形成左右和后方的可替代语义目标。

### 展品坐标

| ID | 名称 | 位置 | 语义作用 |
|---|---|---:|---|
| UserStart | User Start | `(0,0,1.0)` | 用户出生点 |
| E1 | Introduction | `(0,0,2.0)` | 起始展品 |
| E2 | Early Development | `(0,0,4.2)` | 自然下一站 |
| E3 | Related Mechanism | `(-3.6,0,4.8)` | 左侧相关目标 |
| E4 | Alternative Example | `(3.6,0,4.8)` | 右侧替代目标 |
| E5 | Key Target | `(0,0,7.8)` | 主要目标展品 |
| E6 | Side Context | `(-3.8,0,8.2)` | 左后方辅助目标 |
| E7 | Comparison Object | `(3.8,0,8.2)` | 右后方对比目标 |
| E8 | Summary | `(0,0,10.6)` | 总结 / 出口目标 |

### 展品 prefab 结构

每个展品统一使用以下结构：

```text
Exhibit_E3_RelatedMechanism
├── Model_or_ImageBoard
├── TitleText
├── DescriptionPanel
├── ViewPoint
├── GazeTarget
└── ExhibitMetadata.cs
```

每个展品都必须包含：

- 可见模型或图片板，可先用 cube / plane / image board 占位。
- 标题文本。
- 简短介绍文本。
- `ViewPoint`。
- `GazeTarget`。
- `ExhibitMetadata.cs`。

### ViewPoint 与 GazeTarget 坐标

| 展品 | ViewPoint | GazeTarget |
|---|---:|---:|
| E1 | `(0,0,1.2)` | `(0,1.5,2.0)` |
| E2 | `(0,0,3.3)` | `(0,1.5,4.2)` |
| E3 | `(-2.5,0,4.8)` | `(-3.6,1.5,4.8)` |
| E4 | `(2.5,0,4.8)` | `(3.6,1.5,4.8)` |
| E5 | `(0,0,6.8)` | `(0,1.5,7.8)` |
| E6 | `(-2.7,0,8.2)` | `(-3.8,1.5,8.2)` |
| E7 | `(2.7,0,8.2)` | `(3.8,1.5,8.2)` |
| E8 | `(0,0,9.6)` | `(0,1.5,10.6)` |

---

## 6. Unity Hierarchy 建议

```text
DualWorldGallery
├── Environment
│   ├── Floor_12x12
│   ├── Wall_North
│   ├── Wall_South
│   ├── Wall_East
│   └── Wall_West
├── User
│   └── XR_Origin_or_UserStart
├── GuideAvatar
├── Exhibits
│   ├── Exhibit_E1_Introduction
│   ├── Exhibit_E2_EarlyDevelopment
│   ├── Exhibit_E3_RelatedMechanism
│   ├── Exhibit_E4_AlternativeExample
│   ├── Exhibit_E5_KeyTarget
│   ├── Exhibit_E6_SideContext
│   ├── Exhibit_E7_ComparisonObject
│   └── Exhibit_E8_Summary
├── Managers
│   ├── SceneSemanticExporter
│   ├── PhysicalSpaceManager
│   ├── BoundaryVisualizer
│   ├── UserPoseTracker
│   ├── AgentClient
│   ├── ExperimentDashboard
│   └── ExperimentLogger
└── UI
    ├── TaskPanel
    ├── RiskDebugPanel
    ├── GuidanceTextPanel
    └── ResearcherDebugPanel
```

---

## 7. ExhibitMetadata 约定

`ExhibitMetadata.cs` 至少包含：

```text
id
name
tags
description
semantic_role
related_exhibits
view_point
gaze_target
```

命名规则：

```text
Exhibit_E1_Introduction
Exhibit_E2_EarlyDevelopment
Exhibit_E3_RelatedMechanism
Exhibit_E4_AlternativeExample
Exhibit_E5_KeyTarget
Exhibit_E6_SideContext
Exhibit_E7_ComparisonObject
Exhibit_E8_Summary
```

语义角色建议：

| ID | semantic_role | tags | related_exhibits |
|---|---|---|---|
| E1 | introduction | intro, overview, start | E2, E3, E4 |
| E2 | forward_next | early, development, timeline | E1, E3, E5 |
| E3 | left_related_anchor | mechanism, related, left_redirect | E1, E2, E6 |
| E4 | right_alternative_anchor | alternative, example, right_redirect | E1, E2, E7 |
| E5 | main_target | key, important, main | E2, E6, E7, E8 |
| E6 | side_context | context, review, reset_camouflage | E3, E5, E8 |
| E7 | comparison_object | comparison, contrast, turn | E4, E5, E8 |
| E8 | summary | summary, ending, exit | E5, E6, E7 |

---

## 8. Physical Space 配置

Unity 端需要支持运行时切换 physical tracking space，并在研究者 debug 视图中显示：

- boundary
- obstacles
- user physical pose
- safe / unsafe directions
- clearance
- boundary_risk
- reset_risk

### 必须支持的空间

| physical_space | 用途 | 期望行为 |
|---|---|---|
| `wide_room` | 正常宽敞空间 | 允许自然向前导览 |
| `narrow_room` | 前方边界风险 | 阻止 forward guidance，改为左右语义重定向 |
| `obstacle_room` | 障碍物空间 | 避开被障碍阻塞的方向 |
| `l_shaped_room` | 非矩形空间 | 根据可行方向调整导览顺序和转向提示 |

### JSON 示例

```json
{
  "id": "wide_room",
  "boundary": [[0,0],[5,0],[5,5],[0,5]],
  "obstacles": []
}
```

```json
{
  "id": "narrow_room",
  "boundary": [[0,0],[2,0],[2,5],[0,5]],
  "obstacles": []
}
```

```json
{
  "id": "obstacle_room",
  "boundary": [[0,0],[4,0],[4,4],[0,4]],
  "obstacles": [
    {"id":"obstacle_01", "center":[2.0,2.2], "radius":0.45}
  ]
}
```

```json
{
  "id": "l_shaped_room",
  "boundary": [[0,0],[4,0],[4,1.8],[2,1.8],[2,4],[0,4]],
  "obstacles": []
}
```

---

## 9. 必须复现的冲突状态

### Conflict A：语义目标在前方，但真实前方危险

设置：

```text
current_exhibit = E1
semantic_next_target = E2 或 E5
physical_space = narrow_room
forward_clearance < 0.8m
```

MLLM-only 预期问题：

```text
输出“继续向前走”，导致边界风险或 reset。
```

Proposed 预期行为：

```text
选择 E3 或 E4，优先 E3；输出自然过渡话术；avatar 指向安全方向。
```

### Conflict B：左侧目标语义相关，但左侧有障碍

设置：

```text
current_exhibit = E1 或 E2
left semantic target = E3
physical_space = obstacle_room
left_or_left_forward_blocked = true
```

Proposed 预期行为：

```text
拒绝左侧动作，选择 E4 或触发 pause/reset。
```

### Conflict C：所有自然方向都危险

设置：

```text
forward_clearance low
left_clearance low
right_clearance low
```

Proposed 预期行为：

```text
不再伪装自然导览，触发 pause 或 explicit reset，并记录 reset_required。
```

---

## 10. 多 Agent 后端边界

后端包含四类 Agent：

| Agent | 职责 | 备注 |
|---|---|---|
| RDWStateAgent | 计算 clearance、boundary_risk、reset_risk、safe/unsafe directions | 规则 / 几何计算优先 |
| RDWAffordanceAgent | 将 RDW state 转为可用于导览的 affordance | 生成 intervention_type、preferred_direction、semantic_anchor |
| GuideMLLMAgent | 生成导览路线、讲解、gaze cue、avatar action、movement instruction | 可先 mock，后接真实模型 |
| SafetyVerifierAgent | 验证输出是否安全 | Unity 只执行 approved 输出 |

注意：

- 第一版可以全部使用 mock / rule-based 后端。
- 不要把 safety verifier 写成纯自然语言判断。
- 安全相关字段必须来自确定性计算或明确规则。
- GuideMLLMAgent 可以生成自然语言，但不能绕过 verifier。

---

## 11. Unity 与后端通信格式

### Unity 发送状态 JSON

```json
{
  "condition": "ours_full",
  "task_id": "T2_main_target",
  "user_task": "Take me to the most important exhibit.",
  "physical_space_id": "narrow_room",
  "current_exhibit": "E1",
  "semantic_next_target": "E5",
  "user_virtual_position": [0.0, 0.0, 0.0],
  "user_virtual_forward": [0.0, 0.0, 1.0],
  "user_physical_position": [1.0, 0.0, 4.3],
  "user_physical_forward": [0.0, 0.0, 1.0],
  "nearby_exhibits": [
    {"id":"E3", "relative_direction":"left", "semantic_relevance":0.82},
    {"id":"E4", "relative_direction":"right", "semantic_relevance":0.56}
  ]
}
```

### 后端返回 guidance JSON

```json
{
  "selected_target": "E3",
  "route_decision": "visit_left_related_exhibit_before_forward_target",
  "narration": "Before we move to the main exhibit, let's first look at the related object on your left.",
  "gaze_cue": "look_left",
  "gaze_target_id": "E3_GazeTarget",
  "avatar_action": "point_left",
  "movement_instruction": "turn_left_slowly",
  "physical_intent": "avoid_forward_boundary_and_reduce_reset",
  "verifier": {
    "approved": true,
    "reason": "Left turn avoids unsafe forward boundary."
  }
}
```

---

## 12. Baseline 与实验模式

ExperimentDashboard 至少支持以下模式：

| condition | 含义 |
|---|---|
| `mllm_only` | 只使用用户任务、展品语义和虚拟目标，不给 physical boundary / RDW state |
| `mllm_raw_physical` | 给模型原始物理坐标、边界、clearance 数值 |
| `mllm_risk_labels` | 给模型 boundary_risk、reset_risk、unsafe_direction 等标签 |
| `scripted_reset_guide` | 普通导览 + 危险后固定 reset 提示 |
| `ours_without_verifier` | 使用 RDW affordance + MLLM guidance，但不做最终验证 |
| `ours_full` | RDWState + RDWAffordance + MLLM + SafetyVerifier |

所有 baseline 必须在同一虚拟场景、同一任务、同一 physical space 下运行，保证对比可复现。

---

## 13. 实验任务

优先实现：

```text
Task ID: T2_main_target
User Request: Take me to the most important exhibit.
Start Exhibit: E1
Semantic Target: E5
```

预期路线：

| physical_space | 预期路线 | 含义 |
|---|---|---|
| `wide_room` | `E1 -> E2 -> E5` | 前方安全，允许沿虚拟语义主线前进 |
| `narrow_room` | `E1 -> E3 -> E5` | 真实前方风险高，先引导到左侧相关展品 |
| `obstacle_room` | `E1 -> E4 -> E5` | 左侧或左前方有障碍，选择右侧替代展品 |

后续任务：

| task_id | 用户请求 | 用途 |
|---|---|---|
| `T1_overview` | Give me a short overview of the key exhibits. | 一般导览 |
| `T2_main_target` | Take me to the most important exhibit. | 主冲突任务 |
| `T3_compare` | Help me compare two related exhibits. | 左右比较与转向 |
| `T4_next_step` | What should I see next? | 开放式导览 |

---

## 14. 日志要求

每次决策必须写入 JSONL。不要只记录 proposed，也要记录 baseline。

建议字段：

```json
{
  "timestamp": "2026-xx-xxTxx:xx:xx",
  "participant_id": "P001",
  "condition": "ours_full",
  "physical_space": "narrow_room",
  "task_id": "T2_main_target",
  "current_exhibit": "E1",
  "semantic_next_target": "E5",
  "selected_target": "E3",
  "clearance_forward": 0.55,
  "clearance_left": 1.80,
  "clearance_right": 0.90,
  "boundary_risk": "high",
  "reset_risk": "high",
  "unsafe_directions": ["forward"],
  "safe_directions": ["left"],
  "intervention_type": "semantic_redirection",
  "narration": "Before we move to the main exhibit...",
  "movement_instruction": "turn_left_slowly",
  "avatar_action": "point_left",
  "verifier_approved": true,
  "verifier_rejection_reason": null,
  "explicit_reset_triggered": false
}
```

日志必须便于后续统计：

- physically invalid instruction rate
- explicit reset count
- verifier rejection rate
- semantic redirection count
- task completion
- naturalness subjective score

---

## 15. C# 编码规范

- 使用清晰的单一职责脚本，不要把所有逻辑写进一个 Manager。
- 类名、方法名使用 PascalCase。
- 私有字段使用 `[SerializeField] private ...`，避免无必要的 public 字段。
- 配置数据优先来自 JSON 或 Inspector，不要把实验参数硬编码在逻辑里。
- Update 中避免重型计算；边界检测、风险计算可按固定频率或事件触发。
- UI、日志、导览执行、物理空间计算尽量解耦。
- 所有关键脚本添加简短中文注释，说明职责和输入输出。
- 涉及实验数据的字段命名要和 JSON schema 保持一致。
- 不要随意修改 Unity ProjectSettings、Packages、XR 配置，除非任务明确要求。

---

## 16. Python 后端规范

- Python 后端可以先实现 mock service。
- 每个 Agent 独立文件，职责清晰。
- 使用类型标注。
- 输入输出字段与 Unity JSON schema 保持一致。
- mock 逻辑要能稳定复现 Conflict A/B/C。
- SafetyVerifierAgent 不要只返回自然语言，应返回结构化字段：

```json
{
  "approved": true,
  "reason": "...",
  "revision_instruction": null
}
```

---

## 17. Agent 工作流程

处理任务时按以下顺序：

1. 先阅读相关文件和本 `AGENTS.md`。
2. 对复杂任务先给出修改计划。
3. 优先小步修改，不要一次性大规模改动。
4. 修改后检查编译错误、命名错误、引用丢失、JSON 字段不一致。
5. 如果涉及 Unity 场景对象，说明需要在 Inspector 中绑定哪些引用。
6. 如果不能运行 Unity 测试，如实说明没有运行，不要假装通过。
7. 输出时总结：改了哪些文件、实现了什么、还需要用户在 Unity 中做什么。

---

## 18. 测试与验收

### 场景验收

- 运行后用户能从 UserStart 看到 E1、E2、E3、E4。
- 8 个展品位置正确，不重叠。
- 每个展品都有 ViewPoint 和 GazeTarget。
- E2/E5 明显位于前方。
- E3/E4 明显位于左右两侧。
- Scene View 中研究者能看清整体布局。

### 系统验收

- 可切换 `wide_room`、`narrow_room`、`obstacle_room`、`l_shaped_room`。
- 靠近边界时 risk 从 low 变为 high / critical。
- 在 narrow_room 中，当 `forward_clearance < 0.8m` 时：
  - `mllm_only` 可以输出 forward。
  - `ours_full` 不能输出 forward。
  - `ours_full` 应选择左 / 右侧语义相关展品。
- 当所有方向危险时，系统必须触发 pause/reset，而不是强行生成自然导览。
- 每次决策都写入 JSONL。

---

## 19. Git 与文件安全

- 修改前先查看当前工作区状态。
- 不要提交 Library、Temp、Obj、Build、Logs 等 Unity 自动生成目录。
- 不要提交本地 `.env`、密钥、token、账号配置。
- 不要删除用户素材或 ThirdParty 资源。
- 不要把大型二进制文件加入 Git，除非项目已经配置 Git LFS 且用户明确要求。
- 提交信息建议使用：

```text
feat: add physical space manager
fix: correct exhibit viewpoint positions
docs: update scene implementation guide
refactor: split guidance execution controllers
```

---

## 20. 常用实施顺序

推荐按阶段推进：

```text
场景与展品
↓
ExhibitMetadata 与 scene_semantics.json
↓
PhysicalSpaceManager 与 BoundaryVisualizer
↓
UserPoseTracker 与 RiskDebugPanel
↓
Mock AgentClient 与 guidance JSON 执行
↓
Narration / GazeCue / MovementHint / AvatarAction
↓
ExperimentDashboard 与 baseline mode
↓
ExperimentLogger 与 JSONL
↓
真实后端 / MLLM 接入
```

不要一开始就接真实 MLLM。先确保规则版、mock 版、日志版稳定可跑。

---

## 21. 给 Agent 的最终提醒

本项目的关键不是“让用户走到某个展品”，而是验证：

> RDW / physical tracking space / reset-risk 信息如何改变 MLLM 虚拟导览输出。

因此实现时必须始终保留三条主线：

1. 语义导览目标是否合理。
2. 物理方向是否安全。
3. 系统是否能用自然导览方式进行语义重定向，并完整记录证据。

# Codex 项目开发规范

## 语言与注释规范

- 本项目所有新生成或修改的 C# 脚本，必须使用规范的中文注释。
- 注释要解释“为什么这样做”和“该模块负责什么”，不要只翻译代码表面含义。
- 类、结构体、枚举、接口、公开字段、公开属性、公开方法必须添加 XML 中文注释。
- 关键私有字段、关键私有方法、复杂判断、状态切换、坐标换算、碰撞预测、Reset 触发逻辑必须添加中文行注释。
- 不要为非常简单、自解释的代码添加无意义注释，例如 `i++`、`return true`。
- 注释必须准确，不能写与代码行为不一致的说明。
- 修改旧脚本时，如果周围缺少必要注释，需要顺手补齐与本次修改相关的中文注释。

## C# XML 注释格式

公开类、字段、方法使用如下格式：

```csharp
/// <summary>
/// 中文说明：描述该类/方法/字段的职责。
/// </summary>