"""RDWAffordanceAgent：把 RDW 风险状态转换为可执行的语义导览策略。"""

from __future__ import annotations

from backend.schemas import Affordance, GuidanceRequest, PhysicalRiskState


# Direction authority moved to Unity: use semantic_candidates[].relative_direction


class RDWAffordanceAgent:
    """把确定性 RDW 状态转换成可用的语义导览策略。"""

    def build(self, request: GuidanceRequest, risk_state: PhysicalRiskState) -> Affordance:
        if risk_state.reset_required:
            return Affordance(
                intervention_type="pause_reset",
                preferred_direction=None,
                semantic_anchor=request.current_exhibit,
                physical_goal="pause_or_reset_required",
                avoided_direction=None,
            )

        if "forward" in risk_state.unsafe_directions:
            # MVP demo 的关键是语义重定向：保留导览意图，同时避开不安全的前进方向。
            return self._build_redirection(request, risk_state)

        semantic_target = preferred_model_target(request)
        preferred_direction = get_unity_direction(request, semantic_target)
        return Affordance(
            intervention_type="direct_visit",
            preferred_direction=preferred_direction,
            semantic_anchor=semantic_target,
            physical_goal="continue_semantic_route",
            avoided_direction=None,
        )

    def _build_redirection(
        self,
        request: GuidanceRequest,
        risk_state: PhysicalRiskState,
    ) -> Affordance:
        candidates = [
            (get_unity_direction(request, candidate_id), candidate_id)
            for candidate_id in request.model_candidate_ids
            if get_unity_direction(request, candidate_id) in {"left", "right"}
        ]

        selected = first_matching_direction(candidates, risk_state.safe_directions)
        if selected is None:
            # 中等风险方向仍可能优于明确 unsafe 的前进指令；最终仍由 SafetyVerifier 把关。
            selected = first_not_unsafe(candidates, risk_state.unsafe_directions)

        if selected is None:
            return Affordance(
                intervention_type="pause_reset",
                preferred_direction=None,
                semantic_anchor=request.current_exhibit,
                physical_goal="no_safe_redirection_available",
                avoided_direction="forward",
            )

        direction, exhibit_id = selected
        return Affordance(
            intervention_type="semantic_redirection",
            preferred_direction=direction,
            semantic_anchor=exhibit_id,
            physical_goal="avoid_forward_boundary_and_reduce_reset",
            avoided_direction="forward",
        )


def preferred_model_target(request: GuidanceRequest) -> str:
    """返回模型候选中优先级最高的任务目标。"""

    if request.preferred_task_target in request.model_candidate_ids:
        return request.preferred_task_target
    return request.model_candidate_ids[0]


def get_unity_direction(request: GuidanceRequest, target_id: str) -> str:
    """从Unity权威的semantic_candidates中获取目标的相对方向。"""
    candidate = request.candidate_for(target_id)
    if candidate is None:
        return "forward"  # fallback for safety
    return candidate.relative_direction


def first_matching_direction(
    candidates: list[tuple[str, str]],
    allowed_directions: list[str],
) -> tuple[str, str] | None:
    for candidate in candidates:
        if candidate[0] in allowed_directions:
            return candidate
    return None


def first_not_unsafe(
    candidates: list[tuple[str, str]],
    unsafe_directions: list[str],
) -> tuple[str, str] | None:
    for candidate in candidates:
        if candidate[0] not in unsafe_directions:
            return candidate
    return None
