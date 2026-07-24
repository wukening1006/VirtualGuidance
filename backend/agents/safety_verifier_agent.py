"""SafetyVerifierAgent：对 ours_full 的 guidance 执行最终确定性安全校验。"""

from __future__ import annotations

from backend.agents.rdw_affordance_agent import get_unity_direction
from backend.schemas import (
    Condition,
    ConditionCapabilityMode,
    GuidanceRequest,
    GuideDraft,
    MovementInstruction,
    PhysicalRiskState,
    VerifierResult,
    condition_policy_for,
)


MOVEMENT_DIRECTION = {
    MovementInstruction.MOVE_FORWARD_SLOWLY.value: "forward",
    MovementInstruction.LOOK_FORWARD.value: "forward",
    MovementInstruction.TURN_LEFT_SLOWLY.value: "left",
    MovementInstruction.LOOK_LEFT.value: "left",
    MovementInstruction.TURN_RIGHT_SLOWLY.value: "right",
    MovementInstruction.LOOK_RIGHT.value: "right",
}


class SafetyVerifierAgent:
    """proposed full system 的最终确定性安全门。"""

    def verify(
        self,
        condition: str,
        draft: GuideDraft,
        risk_state: PhysicalRiskState,
        request: GuidanceRequest,
    ) -> VerifierResult:
        policy = condition_policy_for(condition)
        if policy.use_model_verifier != ConditionCapabilityMode.ENABLED:
            # baseline 的输出需要完整保留到日志里，包括 unsafe 输出；只有 ours_full 执行最终拒绝。
            return VerifierResult(
                approved=True,
                reason="Baseline pass-through: final SafetyVerifier is not applied.",
            )

        if risk_state.reset_required:
            if draft.movement_instruction in {
                MovementInstruction.PAUSE_AND_WAIT,
                MovementInstruction.RESET_REQUIRED,
            }:
                return VerifierResult(
                    approved=True,
                    reason="All natural directions are unsafe; pause/reset guidance is allowed.",
                )
            return VerifierResult(
                approved=False,
                reason="All natural directions are unsafe; only pause/reset guidance is allowed.",
            )

        movement_direction = MOVEMENT_DIRECTION.get(draft.movement_instruction.value)
        if movement_direction in risk_state.unsafe_directions:
            # Unity 不应执行 rejected guidance；返回 reason 供前端暂停或请求重新生成。
            return VerifierResult(
                approved=False,
                reason=f"movement_instruction points to unsafe {movement_direction} direction.",
            )

        selected_direction = get_unity_direction(request, draft.selected_target)
        if selected_direction in risk_state.unsafe_directions:
            return VerifierResult(
                approved=False,
                reason=f"selected_target is in unsafe {selected_direction} direction.",
            )

        return VerifierResult(
            approved=True,
            reason="Guidance avoids unsafe directions.",
        )
