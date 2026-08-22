from __future__ import annotations

from pathlib import Path

import pytest
from fastapi.testclient import TestClient
from pydantic import ValidationError

from backend.app import create_app
from backend.reset_semantic_intervention import (
    DeterministicResetSemanticInterventionGenerator,
    FORBIDDEN_NARRATION_TOKENS,
)
from backend.schemas import (
    ResetSemanticInterventionRequest,
    ResetSemanticInterventionResponse,
)


def reset_payload() -> dict:
    """构造不含方向、角度或物理安全事实的 v2 语义上下文。"""

    return {
        "planning_schema_version": "reset-semantic-intervention-v2",
        "session_id": "P001_ours_T1_session",
        "reset_id": "reset-001",
        "variation_seed": 17,
        "current_exhibit_id": "E3",
        "current_exhibit_name": "青铜纵目面具",
        "next_target_id": "E5",
        "next_target_name": "青铜神树",
        "current_topic": "祭祀与宇宙观",
        "semantic_hint": "青铜纵目面具与青铜神树共享一条主题线索，可以想想其中的联系。",
        "semantic_hint_key": "edge:SE08",
        "recent_narrations": ["请留意面具夸张的眼部造型与巨大尺度。"],
        "recent_content_keys": ["edge:SE08"],
    }


def make_client(tmp_path: Path) -> TestClient:
    return TestClient(
        create_app(
            config_dir=Path(__file__).resolve().parents[2] / "Assets" / "Configs",
            log_dir=tmp_path,
            reset_semantic_generator=DeterministicResetSemanticInterventionGenerator(),
        )
    )


@pytest.mark.parametrize("field", ["reset_angle", "reset_reason", "target_heading"])
def test_request_rejects_physical_fields(field: str) -> None:
    payload = reset_payload()
    payload[field] = 180.0 if field != "reset_reason" else "boundary_risk"
    with pytest.raises(ValidationError):
        ResetSemanticInterventionRequest.model_validate(payload)


def test_response_rejects_physical_control_fields() -> None:
    with pytest.raises(ValidationError):
        ResetSemanticInterventionResponse.model_validate(
            {
                "narration": "青铜神树的层层枝叶连接着自然意象与古蜀宇宙观。",
                "content_key": "edge:SE11",
                "target_heading": 90.0,
            }
        )


def test_endpoint_returns_grounded_two_field_response(tmp_path: Path) -> None:
    response = make_client(tmp_path).post(
        "/api/planning/reset-semantic-intervention",
        json=reset_payload(),
    )

    assert response.status_code == 200
    body = response.json()
    assert set(body) == {"narration", "content_key"}
    assert 20 <= len(body["narration"]) <= 45
    assert body["content_key"].startswith("target-edge:")
    assert "青铜神树" in body["narration"]
    assert any(
        related_name in body["narration"]
        for related_name in ("青铜太阳形器", "古蜀祭祀世界")
    )
    lowered = body["narration"].lower()
    assert not any(token in lowered for token in FORBIDDEN_NARRATION_TOKENS)


def test_endpoint_uses_other_exhibits_related_to_next_target(tmp_path: Path) -> None:
    payload = reset_payload()
    payload["current_exhibit_id"] = "E1"
    payload["current_exhibit_name"] = "古蜀文明序章"
    response = make_client(tmp_path).post(
        "/api/planning/reset-semantic-intervention",
        json=payload,
    )

    assert response.status_code == 200
    body = response.json()
    assert body["content_key"] in {"target-edge:SE11", "target-edge:SE13"}
    assert "青铜神树" in body["narration"]
    assert "古蜀文明序章" not in body["narration"]


def test_variation_seed_changes_deterministic_fallback(tmp_path: Path) -> None:
    client = make_client(tmp_path)
    first_payload = reset_payload()
    first_payload["recent_narrations"] = []
    first_payload["recent_content_keys"] = []
    first_payload["variation_seed"] = 0
    second_payload = dict(first_payload, variation_seed=1, reset_id="reset-002")

    first = client.post(
        "/api/planning/reset-semantic-intervention",
        json=first_payload,
    ).json()
    second = client.post(
        "/api/planning/reset-semantic-intervention",
        json=second_payload,
    ).json()

    assert first["content_key"] != second["content_key"]
    assert first["narration"] != second["narration"]


def test_recent_content_is_not_repeated(tmp_path: Path) -> None:
    client = make_client(tmp_path)
    payload = reset_payload()
    payload["recent_narrations"] = []
    payload["recent_content_keys"] = []
    first = client.post(
        "/api/planning/reset-semantic-intervention",
        json=payload,
    ).json()

    payload["reset_id"] = "reset-002"
    payload["recent_narrations"] = [first["narration"]]
    payload["recent_content_keys"] = [first["content_key"]]
    second = client.post(
        "/api/planning/reset-semantic-intervention",
        json=payload,
    ).json()

    assert second["content_key"] != first["content_key"]
    assert second["narration"] != first["narration"]
