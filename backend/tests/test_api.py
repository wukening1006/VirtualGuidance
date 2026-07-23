"""测试 FastAPI guidance 接口、健康检查和 JSONL 日志记录行为。"""

import json
from pathlib import Path

import pytest
from fastapi.testclient import TestClient

from backend.app import create_app
from backend.schemas import GuidanceRequest, GuidanceResponse


def make_client(tmp_path: Path) -> TestClient:
    config_dir = Path(__file__).resolve().parents[2] / "Assets" / "Configs"
    app = create_app(config_dir=config_dir, log_dir=tmp_path)
    return TestClient(app)


def base_payload(condition: str) -> dict:
    return {
        "task_definition_version": "t2-main-target-v2",
        "guidance_schema_version": "guidance-schema-v2",
        "candidate_policy_version": "t2-candidate-policy-v1",
        "risk_policy_version": "unity-risk-policy-v1",
        "session_id": "session-test-001",
        "calibration_id": "calibration-test-001",
        "request_id": "request-test-001",
        "decision_id": "decision-test-001",
        "state_version": 3,
        "risk_snapshot_id": "risk-test-001",
        "target_role": "route_subgoal",
        "condition": condition,
        "task_id": "T2_main_target",
        "physical_space_id": "narrow_room",
        "current_exhibit": "E1",
        "user_physical_position": [1.0, 4.3],
        "user_physical_forward": [0.0, 1.0],
        "semantic_next_target": "E5",
    }


def read_log_records(tmp_path: Path) -> list[dict]:
    log_files = list(tmp_path.glob("guidance_*.jsonl"))
    assert len(log_files) == 1
    return [json.loads(line) for line in log_files[0].read_text(encoding="utf-8").splitlines()]


def assert_chinese_narration(body: dict) -> None:
    assert any("\u4e00" <= char <= "\u9fff" for char in body["narration"])


def test_health_reports_loaded_configs(tmp_path: Path) -> None:
    response = make_client(tmp_path).get("/health")

    assert response.status_code == 200
    body = response.json()
    assert body["status"] == "ok"
    assert set(body["physical_spaces"]) == {
        "wide_room",
        "narrow_room",
        "obstacle_room",
        "l_shaped_room",
    }
    assert body["exhibit_count"] == 8


def test_b1_narrow_room_returns_forward_and_logs_risk(tmp_path: Path) -> None:
    client = make_client(tmp_path)

    response = client.post("/api/guidance", json=base_payload("B1_mllm_only"))

    assert response.status_code == 200
    body = response.json()
    assert body["selected_target"] == "E5"
    assert body["movement_instruction"] == "move_forward_slowly"
    assert body["avatar_action"] == "point_forward"
    assert body["verifier"]["approved"] is True
    assert_chinese_narration(body)

    record = read_log_records(tmp_path)[0]
    assert record["selected_target"] == "E5"
    assert record["clearance_forward"] == 0.7
    assert "forward" in record["unsafe_directions"]


def test_guidance_response_contract_keys_are_stable(tmp_path: Path) -> None:
    client = make_client(tmp_path)

    response = client.post("/api/guidance", json=base_payload("ours_full"))

    assert response.status_code == 200
    body = response.json()
    assert set(body) == {
        "task_definition_version",
        "guidance_schema_version",
        "candidate_policy_version",
        "risk_policy_version",
        "session_id",
        "calibration_id",
        "request_id",
        "decision_id",
        "state_version",
        "risk_snapshot_id",
        "target_role",
        "selected_target",
        "route_decision",
        "narration",
        "gaze_cue",
        "gaze_target_id",
        "avatar_action",
        "movement_instruction",
        "physical_intent",
        "verifier",
    }
    assert set(body["verifier"]) == {"approved", "reason"}
    assert body["request_id"] == "request-test-001"
    assert body["session_id"] == "session-test-001"
    assert body["calibration_id"] == "calibration-test-001"
    assert body["risk_snapshot_id"] == "risk-test-001"
    assert body["decision_id"] == "decision-test-001"
    assert body["state_version"] == 3
    assert body["target_role"] == "route_subgoal"
    assert_chinese_narration(body)

    record = read_log_records(tmp_path)[0]
    assert record["request_id"] == "request-test-001"
    assert record["session_id"] == "session-test-001"
    assert record["calibration_id"] == "calibration-test-001"
    assert record["risk_snapshot_id"] == "risk-test-001"
    assert record["decision_id"] == "decision-test-001"
    assert record["state_version"] == 3
    assert record["policy_version"] == "condition-policy-v1"
    assert record["raw_guidance"]["selected_target"] == body["selected_target"]
    assert record["condition_verifier_result"]["applied"] is True
    assert record["runtime_validation_result"] == "pending_unity"
    assert record["runtime_intervention"] is None
    assert record["executed_guidance"] is None


def test_ours_full_narrow_room_redirects_away_from_forward(tmp_path: Path) -> None:
    client = make_client(tmp_path)
    payload = base_payload("ours_full")
    payload["user_physical_position"] = [1.8, 4.3]
    payload["semantic_next_target"] = "E3"

    response = client.post("/api/guidance", json=payload)

    assert response.status_code == 200
    body = response.json()
    assert body["selected_target"] == "E3"
    assert body["movement_instruction"] == "turn_left_slowly"
    assert body["movement_instruction"] != "move_forward_slowly"
    assert body["verifier"]["approved"] is True
    assert_chinese_narration(body)


def test_b5_narrow_room_returns_unsafe_forward_without_verifier(tmp_path: Path) -> None:
    client = make_client(tmp_path)

    response = client.post("/api/guidance", json=base_payload("B5_ours_without_verifier"))

    assert response.status_code == 200
    body = response.json()
    assert body["selected_target"] == "E5"
    assert body["movement_instruction"] == "move_forward_slowly"
    assert body["verifier"]["approved"] is True
    assert "not applied" in body["verifier"]["reason"]
    assert_chinese_narration(body)


def test_b4_scripted_reset_logs_explicit_reset(tmp_path: Path) -> None:
    client = make_client(tmp_path)

    response = client.post("/api/guidance", json=base_payload("B4_scripted_reset"))

    assert response.status_code == 200
    body = response.json()
    assert body["movement_instruction"] == "reset_required"
    assert_chinese_narration(body)

    record = read_log_records(tmp_path)[0]
    assert record["explicit_reset_triggered"] is True
    assert record["movement_instruction"] == "reset_required"


@pytest.mark.parametrize(
    "condition",
    [
        "B1_mllm_only",
        "B2_raw_physical_data",
        "B3_risk_labels",
        "B4_scripted_reset",
        "B5_ours_without_verifier",
        "ours_full",
    ],
)
def test_all_conditions_preserve_unity_selected_target(condition: str, tmp_path: Path) -> None:
    client = make_client(tmp_path)
    payload = base_payload(condition)
    payload["physical_space_id"] = "wide_room"
    payload["user_physical_position"] = [2.5, 2.5]
    payload["semantic_next_target"] = "E5"

    response = client.post("/api/guidance", json=payload)

    assert response.status_code == 200
    body = response.json()
    assert body["selected_target"] == payload["semantic_next_target"]
    record = read_log_records(tmp_path)[0]
    assert record["semantic_next_target"] == payload["semantic_next_target"]
    assert record["raw_guidance"]["selected_target"] == payload["semantic_next_target"]
    assert record["executed_guidance"] is None


def test_request_schema_requires_unity_target_and_rejects_unknown_condition(tmp_path: Path) -> None:
    client = make_client(tmp_path)
    missing_target = base_payload("ours_full")
    missing_target.pop("semantic_next_target")

    missing_response = client.post("/api/guidance", json=missing_target)
    unknown_response = client.post(
        "/api/guidance",
        json={**base_payload("ours_full"), "condition": "unknown_condition"},
    )

    assert missing_response.status_code == 422
    assert any(item["loc"][-1] == "semantic_next_target" for item in missing_response.json()["detail"])
    assert unknown_response.status_code == 422
    assert any(item["loc"][-1] == "condition" for item in unknown_response.json()["detail"])


@pytest.mark.parametrize("task_id", ["T1_overview", "T3_compare", "T4_next_step"])
def test_formal_api_rejects_frozen_tasks(task_id: str, tmp_path: Path) -> None:
    response = make_client(tmp_path).post(
        "/api/guidance",
        json={**base_payload("ours_full"), "task_id": task_id},
    )

    assert response.status_code == 422
    assert any(item["loc"][-1] == "task_id" for item in response.json()["detail"])


def test_api_rejects_legacy_guidance_schema_version(tmp_path: Path) -> None:
    response = make_client(tmp_path).post(
        "/api/guidance",
        json={**base_payload("ours_full"), "guidance_schema_version": "guidance-schema-v1"},
    )

    assert response.status_code == 422
    assert any(item["loc"][-1] == "guidance_schema_version" for item in response.json()["detail"])


def test_pydantic_contract_matches_unity_dto_fields() -> None:
    request_schema = GuidanceRequest.model_json_schema()
    response_schema = GuidanceResponse.model_json_schema()
    assert set(request_schema["properties"]) == {
        "task_definition_version",
        "guidance_schema_version",
        "candidate_policy_version",
        "risk_policy_version",
        "session_id",
        "calibration_id",
        "request_id",
        "decision_id",
        "state_version",
        "risk_snapshot_id",
        "target_role",
        "condition",
        "task_id",
        "physical_space_id",
        "current_exhibit",
        "user_physical_position",
        "user_physical_forward",
        "participant_id",
        "user_task",
        "semantic_next_target",
        "user_virtual_position",
        "user_virtual_forward",
    }
    assert "semantic_next_target" in request_schema["required"]
    assert request_schema["additionalProperties"] is False
    assert set(response_schema["required"]) == set(response_schema["properties"])
    assert response_schema["additionalProperties"] is False


def test_pipeline_rejects_internal_target_mismatch(tmp_path: Path) -> None:
    client = make_client(tmp_path)
    pipeline = client.app.state.pipeline
    original_build = pipeline.guide.build

    def mismatched_build(request, risk_state, affordance):
        draft = original_build(request, risk_state, affordance)
        return draft.model_copy(update={"selected_target": "E4"})

    pipeline.guide.build = mismatched_build

    response = client.post("/api/guidance", json=base_payload("ours_full"))

    assert response.status_code == 500
    assert response.json()["detail"] == (
        "Deterministic guidance contract violation: "
        "selected_target must match semantic_next_target."
    )
