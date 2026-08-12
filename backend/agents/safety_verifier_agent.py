"""SafetyVerifierAgent：校验 ours_full guidance 的协议与动作语义一致性。"""

from __future__ import annotations

from backend.schemas import (
    Condition,
    ConditionCapabilityMode,
    GuidanceRequest,
    GuideDraft,
    MovementInstruction,
    PhysicalRiskState,
    VerifierResult,
    condition_policy_for,
    movement_for_candidate_bearing,
)


class SafetyVerifierAgent:
    """验证候选成员、协议状态与 Unity 候选动作语义。"""

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

        candidate = request.candidate_for(draft.selected_target)
        if candidate is None:
            return VerifierResult(
                approved=False,
                reason="selected_target must belong to the frozen model candidate set.",
            )

        if draft.movement_instruction in {
            MovementInstruction.PAUSE_AND_WAIT,
            MovementInstruction.RESET_REQUIRED,
        }:
            return VerifierResult(
                approved=True,
                reason="Non-movement guidance satisfies the verifier contract.",
            )

        expected_movement = movement_for_candidate_bearing(
            candidate.suggested_movement_instruction,
            candidate.relative_yaw_deg,
        )
        if draft.movement_instruction != expected_movement:
            return VerifierResult(
                approved=False,
                reason=(
                    "movement_instruction is inconsistent with the selected target bearing: "
                    f"expected {expected_movement.value}."
                ),
            )

        return VerifierResult(
            approved=True,
            reason=(
                "Guidance action matches the selected target bearing; "
                "physical safety is delegated to Unity."
            ),
        )
