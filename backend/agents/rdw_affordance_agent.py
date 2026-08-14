"""RDWAffordanceAgent：把 RDW 风险状态转换为可执行的语义导览策略。"""

from __future__ import annotations

from backend.schemas import (
    Affordance,
    GuidanceRequest,
    NavigationTargetRole,
    PhysicalRiskState,
)


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

        semantic_target = preferred_model_target(request)
        preferred_direction = get_unity_direction(request, semantic_target)
        semantic_candidate = request.candidate_for(semantic_target)
        if (
            semantic_candidate is not None
            and semantic_candidate.target_role == NavigationTargetRole.NAVIGABLE_DETOUR
        ):
            return Affordance(
                intervention_type="semantic_redirection",
                preferred_direction=preferred_direction,
                semantic_anchor=semantic_target,
                physical_goal="use_unity_allowed_semantic_candidate",
                avoided_direction=None,
            )

        return Affordance(
            intervention_type="direct_visit",
            preferred_direction=preferred_direction,
            semantic_anchor=semantic_target,
            physical_goal="continue_semantic_route",
            avoided_direction=None,
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
        raise ValueError(f"Unity semantic candidate is missing: {target_id}")
    return candidate.relative_direction


