"""Deterministic guidance pipeline orchestration."""

from __future__ import annotations

import json
from pathlib import Path

from fastapi import HTTPException

from backend.agents.guide_mock import GuideMockAgent
from backend.agents.rdw_affordance_agent import RDWAffordanceAgent
from backend.agents.safety_verifier_agent import SafetyVerifierAgent
from backend.schemas import GuidanceRequest, GuidanceResponse, PhysicalRiskState, response_from_draft
from backend.services.guidance_logger import GuidanceLogger


class GuidancePipeline:
    """Light orchestration layer for the deterministic MVP agent pipeline."""

    def __init__(self, config_dir: Path, log_dir: Path) -> None:
        self.config_dir = config_dir
        self.affordance = RDWAffordanceAgent()
        self.guide = GuideMockAgent()
        self.verifier = SafetyVerifierAgent()
        self.logger = GuidanceLogger(log_dir)
        self.scene_semantics = load_scene_semantics(config_dir)

    @property
    def physical_spaces(self) -> list[str]:
        return sorted(
            path.stem.removeprefix("physical_")
            for path in self.config_dir.glob("physical_*.json")
        )

    @property
    def exhibit_count(self) -> int:
        return len(self.scene_semantics.get("exhibits", []))

    def handle(self, request: GuidanceRequest) -> GuidanceResponse:
        # 在线风险事实完全来自 Unity；Backend 不根据 boundary/obstacle 重新计算或覆盖结论。
        risk_state = PhysicalRiskState(
            physical_space=request.risk_snapshot.physical_space,
            user_physical_position=request.physical_position_xz,
            user_physical_forward=request.physical_forward_xz,
            clearance_forward=request.risk_snapshot.clearance_forward,
            clearance_left=request.risk_snapshot.clearance_left,
            clearance_right=request.risk_snapshot.clearance_right,
            boundary_risk=request.risk_snapshot.risk_level,
            reset_risk="critical" if request.risk_snapshot.reset_required else request.risk_snapshot.risk_level,
            unsafe_directions=request.risk_snapshot.unsafe_directions,
            safe_directions=request.risk_snapshot.safe_directions,
            reset_required=request.risk_snapshot.reset_required,
            risk_snapshot_id=request.risk_snapshot.risk_snapshot_id,
            risk_revision=request.risk_snapshot.risk_revision,
            calibration_id=request.risk_snapshot.calibration_id,
            sampled_at_utc=request.risk_snapshot.sampled_at_utc,
            risk_policy_version=request.risk_snapshot.risk_policy_version,
            source=request.risk_snapshot.source,
            risk_level=request.risk_snapshot.risk_level,
            critical=request.risk_snapshot.critical,
        )

        semantic_next_target = request.semantic_next_target
        affordance = self.affordance.build(request, risk_state)
        draft = self.guide.build(request, risk_state, affordance)
        if draft.selected_target != semantic_next_target:
            raise HTTPException(
                status_code=500,
                detail=(
                    "Deterministic guidance contract violation: "
                    "selected_target must match semantic_next_target."
                ),
            )
        verifier = self.verifier.verify(request.condition, draft, risk_state)
        self.logger.write(request, risk_state, draft, verifier, semantic_next_target)
        return response_from_draft(request, draft, verifier)


def load_scene_semantics(config_dir: Path) -> dict:
    """Load optional exhibit semantics for health checks and demo self-checks."""

    path = config_dir / "scene_semantics.json"
    if not path.exists():
        return {"exhibits": []}
    with path.open("r", encoding="utf-8") as handle:
        return json.load(handle)
