"""GuideMockAgent：为各实验 condition 生成确定性的 mock guidance。"""

from __future__ import annotations

from backend.agents.rdw_affordance import infer_semantic_target, target_direction
from backend.schemas import (
    Affordance,
    AvatarAction,
    Condition,
    GuidanceRequest,
    GuideDraft,
    MovementInstruction,
    PhysicalRiskState,
    TaskId,
)


class GuideMockAgent:
    """为每个 condition 生成确定性的、Unity 可执行的 mock guidance。"""

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
        target = infer_semantic_target(request)
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
        target = infer_semantic_target(request)
        direction = target_direction(target)
        return draft_for_direction(
            selected_target=target,
            direction=direction,
            route_decision=route_decision_for_target(target),
            narration=(
                f"The main route continues ahead. Current forward clearance is "
                f"{risk_state.clearance_forward:.2f} meters, so keep moving slowly."
            ),
            physical_intent="raw_clearance_exposed_without_semantic_intervention",
            intervention_type="raw_physical_data",
        )

    def _risk_label_guidance(
        self,
        request: GuidanceRequest,
        risk_state: PhysicalRiskState,
    ) -> GuideDraft:
        target = infer_semantic_target(request)
        if risk_state.boundary_risk in {"high", "critical"}:
            return GuideDraft(
                selected_target=target,
                route_decision="pause_due_to_risk_label",
                narration=(
                    f"The next important exhibit is {target}, but the current "
                    f"tracking-space risk is {risk_state.boundary_risk}. Please pause briefly."
                ),
                gaze_cue="look_forward",
                gaze_target_id=f"{target}_GazeTarget",
                avatar_action=AvatarAction.WAIT,
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
        target = infer_semantic_target(request)
        if risk_state.boundary_risk in {"high", "critical"}:
            return GuideDraft(
                selected_target=target,
                route_decision="scripted_reset_due_to_boundary_risk",
                narration="Please stop here. A reset is required before continuing the tour.",
                gaze_cue="look_forward",
                gaze_target_id=f"{target}_GazeTarget",
                avatar_action=AvatarAction.WAIT,
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
                selected_target="E5",
                direction="forward",
                route_decision="unsafe_forward_without_final_verifier",
                narration=(
                    "Let's continue straight toward the key exhibit first, then we can "
                    "use the side exhibits for context."
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
        if affordance.intervention_type == "pause_reset":
            return GuideDraft(
                selected_target=request.current_exhibit,
                route_decision="pause_until_safe_direction_available",
                narration="Let's pause here for a moment before continuing the tour.",
                gaze_cue="look_forward",
                gaze_target_id=f"{request.current_exhibit}_GazeTarget",
                avatar_action=AvatarAction.WAIT,
                movement_instruction=MovementInstruction.PAUSE_AND_WAIT,
                physical_intent=affordance.physical_goal,
                intervention_type=affordance.intervention_type,
            )

        direction = affordance.preferred_direction or "forward"
        target = affordance.semantic_anchor
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


def route_decision_for_target(target: str) -> str:
    if target == "E5":
        return "direct_to_main_target"
    if target == "E7":
        return "direct_to_comparison_object"
    return "direct_to_next_semantic_target"


def route_decision_for_affordance(affordance: Affordance, target: str) -> str:
    if affordance.intervention_type == "semantic_redirection":
        return f"visit_{affordance.preferred_direction}_related_exhibit_before_forward_target"
    return route_decision_for_target(target)


def narration_for_direct_target(request: GuidanceRequest, target: str) -> str:
    if request.task_id == TaskId.T2_MAIN_TARGET.value and target == "E5":
        return "Let's continue forward to the key exhibit."
    if request.task_id == TaskId.T3_COMPARE.value:
        return "Let's move to the comparison exhibit and use it to contrast the ideas."
    if request.task_id == TaskId.T4_NEXT_STEP.value:
        return "A good next step is the exhibit ahead that continues the main storyline."
    return "Let's continue to the next exhibit for a quick overview."


def narration_for_redirection(target: str, direction: str) -> str:
    side = "left" if direction == "left" else "right"
    return (
        f"Before we move to the main exhibit, let's first look at the related "
        f"object on your {side}."
    )


def draft_for_direction(
    selected_target: str,
    direction: str,
    route_decision: str,
    narration: str,
    physical_intent: str,
    intervention_type: str,
) -> GuideDraft:
    """把语义方向映射为 Unity 端可识别的 action enum 值。"""

    movement_by_direction = {
        "forward": MovementInstruction.MOVE_FORWARD_SLOWLY,
        "left": MovementInstruction.TURN_LEFT_SLOWLY,
        "right": MovementInstruction.TURN_RIGHT_SLOWLY,
    }
    avatar_by_direction = {
        "forward": AvatarAction.POINT_FORWARD,
        "left": AvatarAction.POINT_LEFT,
        "right": AvatarAction.POINT_RIGHT,
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
        avatar_action=avatar_by_direction[direction],
        movement_instruction=movement_by_direction[direction],
        physical_intent=physical_intent,
        intervention_type=intervention_type,
    )
