"""Deterministic guidance pipeline orchestration."""

from __future__ import annotations

import json
from pathlib import Path

from fastapi import HTTPException

from backend.agents.guide_mock import GuideMockAgent
from backend.agents.rdw_affordance_agent import RDWAffordanceAgent, infer_semantic_target
from backend.agents.rdw_state_agent import RDWStateAgent
from backend.agents.safety_verifier_agent import SafetyVerifierAgent
from backend.schemas import GuidanceRequest, GuidanceResponse, response_from_draft
from backend.services.guidance_logger import GuidanceLogger


class GuidancePipeline:
    """Light orchestration layer for the deterministic MVP agent pipeline."""

    def __init__(self, config_dir: Path, log_dir: Path) -> None:
        self.config_dir = config_dir
        self.rdw_state = RDWStateAgent.from_config_dir(config_dir)
        self.affordance = RDWAffordanceAgent()
        self.guide = GuideMockAgent()
        self.verifier = SafetyVerifierAgent()
        self.logger = GuidanceLogger(log_dir)
        self.scene_semantics = load_scene_semantics(config_dir)

    @property
    def physical_spaces(self) -> list[str]:
        return sorted(self.rdw_state.spaces.keys())

    @property
    def exhibit_count(self) -> int:
        return len(self.scene_semantics.get("exhibits", []))

    def handle(self, request: GuidanceRequest) -> GuidanceResponse:
        try:
            risk_state = self.rdw_state.evaluate(
                request.physical_space_id,
                request.physical_position_xz,
                request.physical_forward_xz,
            )
        except ValueError as exc:
            raise HTTPException(status_code=400, detail=str(exc)) from exc

        semantic_next_target = infer_semantic_target(request)
        affordance = self.affordance.build(request, risk_state)
        draft = self.guide.build(request, risk_state, affordance)
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
