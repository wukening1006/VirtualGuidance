from __future__ import annotations

from pathlib import Path

import pytest
from fastapi.testclient import TestClient
from pydantic import ValidationError

from backend.app import create_app
from backend.reset_semantic_intervention import (
    DeterministicResetSemanticInterventionGenerator,
)
from backend.schemas import (
    ResetSemanticInterventionRequest,
    ResetSemanticInterventionResponse,
)


def reset_payload() -> dict:
    """构造最小 Reset 语义上下文，不含物理安全事实。"""

    return {
        "planning_schema_version": "reset-semantic-intervention-v1",
        "current_exhibit": "青铜面具",
        "current_topic": "祭祀与宇宙观",
        "next_target": "青铜神树",
        "recent_narration": "请留意面具的双眼。",
        "reset_angle": 120.0,
        "reset_reason": "boundary_risk",
    }


def make_client(tmp_path: Path) -> TestClient:
    return TestClient(
        create_app(
            config_dir=Path(__file__).resolve().parents[2] / "Assets" / "Configs",
            log_dir=tmp_path,
            reset_semantic_generator=DeterministicResetSemanticInterventionGenerator(),
        )
    )


def test_request_rejects_physical_fields() -> None:
    payload = reset_payload()
    payload["target_heading"] = 90.0
    with pytest.raises(ValidationError):
        ResetSemanticInterventionRequest.model_validate(payload)


def test_response_rejects_physical_control_fields() -> None:
    with pytest.raises(ValidationError):
        ResetSemanticInterventionResponse.model_validate(
            {
                "intervention_level": "high",
                "narration": "过渡话术",
                "target_heading": 90.0,
            }
        )


@pytest.mark.parametrize(
    ("angle", "level"),
    [(59.9, "low"), (60.0, "medium"), (119.9, "medium"), (120.0, "high")],
)
def test_endpoint_returns_two_field_semantic_response(tmp_path: Path, angle: float, level: str) -> None:
    payload = reset_payload()
    payload["reset_angle"] = angle
    response = make_client(tmp_path).post(
        "/api/planning/reset-semantic-intervention",
        json=payload,
    )

    assert response.status_code == 200
    body = response.json()
    assert set(body) == {"intervention_level", "narration"}
    assert body["intervention_level"] == level
    assert body["narration"]
