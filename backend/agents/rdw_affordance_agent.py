"""RDWAffordanceAgent：把 RDW 风险状态转换为可执行的语义导览策略。"""

from __future__ import annotations

from backend.schemas import Affordance, GuidanceRequest, PhysicalRiskState, TaskId


TARGET_DIRECTION = {
    "E1": "forward",
    "E2": "forward",
    "E3": "left",
    "E4": "right",
    "E5": "forward",
    "E6": "left",
    "E7": "right",
    "E8": "forward",
}


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

        semantic_target = infer_semantic_target(request)
        preferred_direction = target_direction(semantic_target)
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
        # 候选展品编码当前展厅布局：E3 在 demo 起点左侧，E4/E7 是右侧替代目标。
        candidates = [("left", "E3"), ("right", "E4")]
        if request.task_id == TaskId.T3_COMPARE.value:
            candidates = [("right", "E7"), ("left", "E3")]

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


def infer_semantic_target(request: GuidanceRequest) -> str:
    """返回 Unity 已选择并写入请求契约的唯一语义目标。"""

    return request.semantic_next_target


def target_direction(exhibit_id: str) -> str:
    return TARGET_DIRECTION.get(exhibit_id, "forward")


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
