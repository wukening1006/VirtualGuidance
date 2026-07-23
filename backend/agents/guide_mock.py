"""GuideMockAgent：为各实验 condition 生成确定性的 mock guidance。"""

from __future__ import annotations

from backend.agents.rdw_affordance_agent import preferred_model_target, target_direction
from backend.schemas import (
    Affordance,
    Condition,
    GuidanceRequest,
    GuideDraft,
    MovementInstruction,
    PhysicalRiskState,
    RouteDecision,
    TaskId,
)


class GuideMockAgent:
    """为每个 condition 生成确定性的、Unity 可执行的 mock guidance。"""

    async def generate(
        self,
        request: GuidanceRequest,
        risk_state: PhysicalRiskState,
        affordance: Affordance,
    ) -> GuideDraft:
        """为自动化测试和显式离线复现提供异步 Generator 接口。"""

        return self.build(request, risk_state, affordance)

    def build(
        self,
        request: GuidanceRequest,
        risk_state: PhysicalRiskState,
        affordance: Affordance,
    ) -> GuideDraft:
        # 各 baseline 故意使用不同程度的 RDW 信息，让 unsafe guidance 能在测试和日志中暴露出来。
        condition = request.condition
        if condition == Condition.B1_MLLM_ONLY.value:
            return self._direct_semantic_guidance(request, "semantic_only_no_physical_grounding")
        if condition == Condition.B2_RAW_PHYSICAL_DATA.value:
            return self._raw_physical_guidance(request, risk_state)
        if condition == Condition.B3_RISK_LABELS.value:
            return self._risk_label_guidance(request, risk_state)
        if condition == Condition.B4_SCRIPTED_RESET.value:
            return self._scripted_reset_guidance(request, risk_state)
        if condition == Condition.B5_OURS_WITHOUT_VERIFIER.value:
            return self._ours_without_verifier_guidance(request, affordance)
        return self._affordance_guidance(request, affordance)

    def _direct_semantic_guidance(self, request: GuidanceRequest, intent: str) -> GuideDraft:
        target = preferred_model_target(request)
        direction = target_direction(target)
        return draft_for_direction(
            selected_target=target,
            direction=direction,
            route_decision=route_decision_for_target(target),
            narration=narration_for_direct_target(request, target),
            physical_intent=intent,
            intervention_type="none",
        )

    def _raw_physical_guidance(
        self,
        request: GuidanceRequest,
        risk_state: PhysicalRiskState,
    ) -> GuideDraft:
        target = preferred_model_target(request)
        direction = target_direction(target)
        return draft_for_direction(
            selected_target=target,
            direction=direction,
            route_decision=route_decision_for_target(target),
            narration=(
                f"主路线在前方，当前前方可用距离为 {risk_state.clearance_forward:.2f} 米，"
                "请缓慢前进。"
            ),
            physical_intent="raw_clearance_exposed_without_semantic_intervention",
            intervention_type="raw_physical_data",
        )

    def _risk_label_guidance(
        self,
        request: GuidanceRequest,
        risk_state: PhysicalRiskState,
    ) -> GuideDraft:
        target = preferred_model_target(request)
        if risk_state.boundary_risk in {"high", "critical"}:
            return GuideDraft(
                selected_target=target,
                route_decision=RouteDecision.REGUIDANCE_SAME_TARGET,
                narration=(
                    "下一个重要展品暂时不适合直接前往，请在原地暂停等待。"
                ),
                gaze_cue="look_forward",
                gaze_target_id=f"{target}_GazeTarget",
                movement_instruction=MovementInstruction.PAUSE_AND_WAIT,
                physical_intent="react_to_risk_label_without_semantic_redirection",
                intervention_type="risk_label_pause",
            )
        return self._direct_semantic_guidance(request, "risk_label_allows_normal_route")

    def _scripted_reset_guidance(
        self,
        request: GuidanceRequest,
        risk_state: PhysicalRiskState,
    ) -> GuideDraft:
        target = preferred_model_target(request)
        if risk_state.boundary_risk in {"high", "critical"}:
            return GuideDraft(
                selected_target=target,
                route_decision=RouteDecision.REGUIDANCE_SAME_TARGET,
                narration="请在此停下，继续参观前需要重新校准活动空间。",
                gaze_cue="look_forward",
                gaze_target_id=f"{target}_GazeTarget",
                movement_instruction=MovementInstruction.RESET_REQUIRED,
                physical_intent="scripted_reset_on_high_or_critical_risk",
                intervention_type="scripted_reset",
                explicit_reset_triggered=True,
            )
        return self._direct_semantic_guidance(request, "scripted_reset_not_needed")

    def _ours_without_verifier_guidance(
        self,
        request: GuidanceRequest,
        affordance: Affordance,
    ) -> GuideDraft:
        # 为 B5 保留一个稳定失败案例：即使上游 pipeline 大体 grounded，没有最终 verifier 仍可能危险。
        if (
            request.task_id == TaskId.T2_MAIN_TARGET.value
            and request.physical_space_id == "narrow_room"
            and request.current_exhibit == "E1"
        ):
            return draft_for_direction(
                selected_target=preferred_model_target(request),
                direction="forward",
                route_decision=RouteDecision.DIRECT_TO_TARGET,
                narration=(
                    "我们先直行前往重点展品，之后再通过两侧展品补充背景。"
                ),
                physical_intent="affordance_pipeline_without_final_safety_check",
                intervention_type="semantic_redirection_without_verifier_failure",
            )
        return self._affordance_guidance(request, affordance)

    def _affordance_guidance(
        self,
        request: GuidanceRequest,
        affordance: Affordance,
    ) -> GuideDraft:
        target = (
            affordance.semantic_anchor
            if affordance.semantic_anchor in request.model_candidate_ids
            else preferred_model_target(request)
        )
        if affordance.intervention_type == "pause_reset":
            return GuideDraft(
                selected_target=target,
                route_decision=RouteDecision.REGUIDANCE_SAME_TARGET,
                narration="请在此暂停片刻，等待可以安全继续参观。",
                gaze_cue="look_forward",
                gaze_target_id=f"{target}_GazeTarget",
                movement_instruction=MovementInstruction.PAUSE_AND_WAIT,
                physical_intent=affordance.physical_goal,
                intervention_type=affordance.intervention_type,
            )

        direction = target_direction(target)
        narration = narration_for_redirection(target, direction)
        if affordance.intervention_type == "direct_visit":
            narration = narration_for_direct_target(request, target)

        return draft_for_direction(
            selected_target=target,
            direction=direction,
            route_decision=route_decision_for_affordance(affordance, target),
            narration=narration,
            physical_intent=affordance.physical_goal,
            intervention_type=affordance.intervention_type,
        )


def route_decision_for_target(target: str) -> RouteDecision:
    return RouteDecision.DIRECT_TO_TARGET if target == "E5" else RouteDecision.VISIT_ROUTE_SUBGOAL


def route_decision_for_affordance(affordance: Affordance, target: str) -> RouteDecision:
    if affordance.intervention_type == "semantic_redirection":
        return RouteDecision.SEMANTIC_REDIRECT
    return route_decision_for_target(target)


def narration_for_direct_target(request: GuidanceRequest, target: str) -> str:
    if request.task_id == TaskId.T2_MAIN_TARGET.value and target == "E5":
        return "请继续向前，前往重点展品。"
    if request.task_id == TaskId.T3_COMPARE.value:
        return "请前往对比展品，观察它与当前内容的差异。"
    if request.task_id == TaskId.T4_NEXT_STEP.value:
        return "请前往前方展品，继续了解主要参观内容。"
    return "请前往下一个展品，快速了解整体内容。"


def narration_for_redirection(target: str, direction: str) -> str:
    side = "左侧" if direction == "left" else "右侧"
    return f"前往主要展品前，请先看向您{side}的相关展品。"


def draft_for_direction(
    selected_target: str,
    direction: str,
    route_decision: RouteDecision,
    narration: str,
    physical_intent: str,
    intervention_type: str,
) -> GuideDraft:
    """把语义方向映射为 Unity 端可识别的 movement/gaze enum 值。"""

    movement_by_direction = {
        "forward": MovementInstruction.MOVE_FORWARD_SLOWLY,
        "left": MovementInstruction.TURN_LEFT_SLOWLY,
        "right": MovementInstruction.TURN_RIGHT_SLOWLY,
    }
    gaze_by_direction = {
        "forward": "look_forward",
        "left": "look_left",
        "right": "look_right",
    }
    return GuideDraft(
        selected_target=selected_target,
        route_decision=route_decision,
        narration=narration,
        gaze_cue=gaze_by_direction[direction],
        gaze_target_id=f"{selected_target}_GazeTarget",
        movement_instruction=movement_by_direction[direction],
        physical_intent=physical_intent,
        intervention_type=intervention_type,
    )
