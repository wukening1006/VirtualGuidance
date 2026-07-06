# AGENT.md

## 项目名称

RDW-Grounded MLLM Virtual Guidance

## 项目目标

本项目要开发一个虚拟导览实验系统。

核心目标是展示：

同一个虚拟展厅、同一个导览任务，在不同 physical tracking space 条件下，系统会根据真实空间边界、障碍物和 reset risk，改变 MLLM 的导览路线、讲解话术、视线提示、avatar 动作和移动建议。

系统重点不是让 LLM 直接判断安全，而是：

1. 由规则模块计算 RDW / physical space 状态。
2. 由 affordance 模块生成可用的导览策略。
3. 由 MLLM 或 mock guide 生成自然导览。
4. 由 Safety Verifier 做最终安全检查。
5. Unity 只执行通过验证的 guidance。

---

## 开发原则

### 1. 先跑通闭环，再优化效果

优先完成：

```text
Unity state → 后端 guidance → verifier → Unity 执行 → JSONL 日志
```

不要一开始追求复杂模型、复杂美术或复杂 RDW 算法。

### 2. 不要让 LLM 直接判断安全

以下内容必须由规则或几何模块计算：

```text
clearance
boundary_risk
reset_risk
safe_directions
unsafe_directions
verifier_approved
```

LLM 只负责生成自然语言导览和动作表达。

### 3. 所有输出必须可记录

每次导览决策都必须写入 JSONL 日志。

不能只记录 Proposed，也要记录所有 baseline。

### 4. Unity 只执行 approved guidance

如果后端返回：

```json
{
  "approved": false
}
```

Unity 不允许：

```text
移动用户
高亮目标
播放 movement hint
执行 avatar 指向动作
```

只显示 rejection reason，并请求重新生成或触发 pause/reset。

---

## 项目结构

推荐目录：

```text
Assets/
├── Scenes/
│   └── DualWorldGallery.unity
├── Scripts/
│   ├── Data/
│   ├── Scene/
│   ├── PhysicalSpace/
│   ├── Guidance/
│   ├── Experiment/
│   └── Utils/
├── Configs/
└── Logs/

backend/
├── app.py
├── schemas.py
├── agents/
├── prompts/
├── configs/
├── logs/
└── tests/

analysis/
├── parse_logs.py
├── compute_metrics.py
└── plot_results.py
```

---

## Unity 侧主要职责

Unity 负责实验运行和可视化。

需要实现：

```text
虚拟展厅
8 个展品
ViewPoint / GazeTarget
physical space 加载
边界和障碍物可视化
用户虚拟位姿和物理位姿
ExperimentDashboard
AgentClient
avatar 动作
gaze cue
movement hint
narration
JSONL 日志
```

Unity 不负责最终安全判断。

---

## 后端主要职责

后端负责生成 guidance。

推荐 agent pipeline：

```text
RDWStateAgent
  ↓
RDWAffordanceAgent
  ↓
GuideMLLMAgent 或 GuideMockAgent
  ↓
SafetyVerifierAgent
```

### RDWStateAgent

输入：

```text
physical_space
user_physical_pose
user_forward
candidate directions
```

输出：

```text
clearance_forward
clearance_left
clearance_right
boundary_risk
reset_risk
safe_directions
unsafe_directions
```

### RDWAffordanceAgent

输入：

```text
RDW state
nearby exhibits
task
current exhibit
```

输出：

```text
intervention_type
preferred_direction
semantic_anchor
physical_goal
```

### GuideMLLMAgent / GuideMockAgent

输出 Unity 可执行的 guidance：

```text
selected_target
route_decision
narration
gaze_cue
gaze_target_id
avatar_action
movement_instruction
physical_intent
```

### SafetyVerifierAgent

检查：

```text
movement_instruction 是否指向 unsafe direction
selected_target 是否在 unsafe direction
是否需要 pause/reset
```

---

## 必须支持的实验条件

系统至少支持以下 6 个 condition：

```text
B1_mllm_only
B2_raw_physical_data
B3_risk_labels
B4_scripted_reset
B5_ours_without_verifier
ours_full
```

其中：

```text
B1 用于暴露 MLLM-only 的物理危险输出
B5 用于证明没有 verifier 仍可能不安全
ours_full 是最终 proposed system
```

---

## 必须支持的任务

```text
T1_overview
T2_main_target
T3_compare
T4_next_step
```

任务含义：

```text
T1：快速概览关键展品
T2：带用户去最重要展品
T3：比较两个相关展品
T4：推荐下一步看什么
```

---

## 必须支持的 physical space

```text
wide_room
narrow_room
obstacle_room
l_shaped_room
```

用途：

```text
wide_room：正常导览
narrow_room：测试前方边界风险
obstacle_room：测试障碍物方向风险
l_shaped_room：测试非矩形空间
```

---

## Guidance JSON 格式

后端返回给 Unity 的 guidance 至少包含：

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

字段名不要随意修改。

---

## 支持的 avatar_action

```text
point_left
point_right
point_forward
look_at_target
wait
```

如果没有安全方向，使用：

```text
wait
```

---

## 支持的 movement_instruction

```text
move_forward_slowly
turn_left_slowly
turn_right_slowly
look_left
look_right
look_forward
pause_and_wait
reset_required
```

不要生成 Unity 无法识别的 movement instruction。

---

## 日志要求

每次导览决策必须写入一行 JSONL。

必须包含：

```text
timestamp
participant_id
condition
physical_space
task_id
current_exhibit
semantic_next_target
selected_target
clearance_forward
clearance_left
clearance_right
boundary_risk
reset_risk
unsafe_directions
safe_directions
intervention_type
narration
movement_instruction
avatar_action
verifier_approved
verifier_rejection_reason
explicit_reset_triggered
```

日志字段名尽量保持稳定，避免后续统计脚本失效。

---

## MVP 验收标准

最小可行版本必须满足：

1. Unity 场景中有 8 个展品。
2. 可以切换 4 种 physical space。
3. 可以切换 6 个 condition。
4. 可以切换 4 个 task。
5. Unity 能发送 state JSON 给后端。
6. 后端能返回 guidance JSON。
7. Unity 能执行 approved guidance。
8. Unity 不执行 rejected guidance。
9. 每次决策都有 JSONL 日志。
10. 在 narrow_room 中，当 forward clearance < 0.8m：
    - B1 可以输出 forward。
    - ours_full 不能输出 forward。
    - ours_full 应选择左侧或右侧语义相关展品。
    - avatar 应指向安全方向。
    - movement hint 应提示安全转向。

---

## Codex 开发要求

Codex 修改代码时需要遵守：

1. 不要随意改 JSON 字段名。
2. 不要删除 baseline。
3. 不要让 LLM 直接判断安全。
4. 不要跳过 SafetyVerifier。
5. 不要只实现 happy path。
6. 新增模块时要给出简单测试方式。
7. 涉及日志字段时，要同步更新 schema 和分析脚本。
8. Unity 和 backend 的接口必须保持一致。
9. 如果 guidance 被 rejected，Unity 不能执行该 guidance。
10. 优先保证系统可运行，而不是写复杂抽象。

---

## 推荐开发顺序

```text
Phase 1：JSON schema 和 mock guidance
Phase 2：Unity 展厅和展品 metadata
Phase 3：physical space 和 risk debug
Phase 4：后端 mock agent pipeline
Phase 5：Unity 执行 guidance
Phase 6：ExperimentDashboard 和日志
Phase 7：baseline 切换
Phase 8：真实 LLM / MLLM 接入
Phase 9：demo 录制和日志统计
```

---

## 最重要的 demo

必须能展示：

```text
同一个任务：Take me to the most important exhibit.
同一个虚拟场景：用户在 E1，主目标 E5 在前方。

B1_mllm_only：
avatar 指向前方，提示用户继续向前。

ours_full + narrow_room：
系统检测 forward unsafe。
avatar 不再指向前方。
系统选择左侧或右侧相关展品。
avatar 指向安全方向。
narration 自然解释为什么先看这个展品。
movement hint 引导用户慢慢转向。
日志记录 semantic_redirection 和 verifier approved。
```

这个 demo 是项目的核心验收目标。

---

## 不要做的事情

当前阶段不要做：

```text
复杂美术资产
复杂 avatar pathfinding
复杂高阶 RDW 算法
让 LLM 直接判断安全
只做单一路线展厅
只记录 Proposed 日志
跳过 baseline
跳过 SafetyVerifier
```

---

## 最终判断标准

如果系统能稳定展示：

```text
同一虚拟任务 + 同一虚拟场景
因为 physical space 不同
导览路线、话术、avatar 动作和 movement hint 发生合理变化
同时减少 unsafe guidance 和显式 reset
```

则说明当前开发目标达成。
