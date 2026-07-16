"""GuidanceLogger：把每次导览决策写入稳定 JSONL 日志，供后续分析使用。"""

from __future__ import annotations

import json
from datetime import datetime, timezone
from pathlib import Path

from backend.schemas import (
    GuidanceLogRecord,
    GuidanceRequest,
    GuideDraft,
    PhysicalRiskState,
    VerifierResult,
    ConditionCapabilityMode,
    condition_policy_for,
)


class GuidanceLogger:
    """为每次 guidance decision 写入一条稳定 JSONL 记录。"""

    def __init__(self, log_dir: Path) -> None:
        self.log_dir = log_dir
        self.log_dir.mkdir(parents=True, exist_ok=True)

    def write(
        self,
        request: GuidanceRequest,
        risk_state: PhysicalRiskState,
        draft: GuideDraft,
        verifier: VerifierResult,
        semantic_next_target: str,
    ) -> Path:
        timestamp = datetime.now(timezone.utc).isoformat()
        policy = condition_policy_for(request.condition)
        # 同一行同时记录 proposed guidance 和 verifier 结果，分析时无需重放状态即可比较 baseline 和 ours_full。
        record = GuidanceLogRecord(
            timestamp=timestamp,
            request_id=request.request_id,
            decision_id=request.decision_id,
            state_version=request.state_version,
            policy_version=policy.policy_version,
            raw_guidance=draft.model_dump(mode="json"),
            condition_verifier_result={
                "applied": policy.use_model_verifier == ConditionCapabilityMode.ENABLED,
                "approved": verifier.approved,
                "reason": verifier.reason,
            },
            runtime_validation_result="pending_unity",
            runtime_intervention=None,
            executed_guidance=None,
            participant_id=request.participant_id,
            condition=request.condition,
            physical_space=risk_state.physical_space,
            task_id=request.task_id,
            current_exhibit=request.current_exhibit,
            semantic_next_target=semantic_next_target,
            selected_target=draft.selected_target,
            clearance_forward=risk_state.clearance_forward,
            clearance_left=risk_state.clearance_left,
            clearance_right=risk_state.clearance_right,
            boundary_risk=risk_state.boundary_risk,
            reset_risk=risk_state.reset_risk,
            unsafe_directions=risk_state.unsafe_directions,
            safe_directions=risk_state.safe_directions,
            intervention_type=draft.intervention_type,
            narration=draft.narration,
            movement_instruction=draft.movement_instruction.value,
            avatar_action=draft.avatar_action.value,
            verifier_approved=verifier.approved,
            verifier_rejection_reason="" if verifier.approved else verifier.reason,
            explicit_reset_triggered=draft.explicit_reset_triggered,
        )
        # 行内时间使用 UTC 保证事件时间无歧义；文件名使用本地日期，方便 demo 时浏览。
        path = self.log_dir / f"guidance_{datetime.now().strftime('%Y%m%d')}.jsonl"
        with path.open("a", encoding="utf-8") as handle:
            handle.write(json.dumps(record.model_dump(), ensure_ascii=False) + "\n")
        return path
