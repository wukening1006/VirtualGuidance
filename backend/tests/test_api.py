"""测试 FastAPI guidance 接口、健康检查和 JSONL 日志记录行为。"""

import json
from pathlib import Path

import pytest
from fastapi.testclient import TestClient

from backend.agents.guide_mock import GuideMockAgent
from backend.app import create_app
from backend.pipeline import load_scene_semantics
from backend.schemas import GuidanceRequest, GuidanceResponse


def make_client(tmp_path: Path) -> TestClient:
    config_dir = Path(__file__).resolve().parents[2] / "Assets" / "Configs"
    app = create_app(
        config_dir=config_dir,
        log_dir=tmp_path,
        draft_generator=GuideMockAgent(),
    )
    return TestClient(app)


def base_payload(condition: str) -> dict:
    candidate_ids = ["E5", "E3", "E4"]
    safe_filter = condition in {"B5_ours_without_verifier", "ours_full"}
    model_candidate_ids = ["E3", "E4"] if safe_filter else candidate_ids
    payload = {
        "task_definition_version": "t2-main-target-v2",
        "guidance_schema_version": "guidance-schema-v2",
        "candidate_policy_version": "t2-candidate-policy-v1",
        "risk_policy_version": "unity-risk-policy-v1",
        "session_id": "session-test-001",
        "calibration_id": "calibration-test-001",
        "request_id": "request-test-001",
        "decision_id": "decision-test-001",
        "candidate_set_id": "candidates-test-001",
        "state_version": 3,
        "risk_snapshot_id": "risk-test-001",
        "condition": condition,
        "task_id": "T2_main_target",
        "physical_space_id": "narrow_room",
        "current_exhibit": "E1",
        "user_physical_position": [1.0, 4.3],
        "user_physical_forward": [0.0, 1.0],
        "final_target": "E5",
        "preferred_task_target": "E5",
        "candidate_ids": candidate_ids,
        "model_candidate_ids": model_candidate_ids,
        "allowed_candidate_ids": ["E3", "E4"],
        "semantic_candidates": [
            {
                "id": target_id,
                "target_role": "final_target" if target_id == "E5" else "navigable_detour",
                "relative_direction": {"E5": "forward", "E3": "left", "E4": "right"}[target_id],
                "relative_yaw_deg": {"E5": 0.0, "E3": -90.0, "E4": 90.0}[target_id],
                "suggested_movement_instruction": {
                    "E5": "move_forward_slowly",
                    "E3": "turn_left_slowly",
                    "E4": "turn_right_slowly",
                }[target_id],
            }
            for target_id in model_candidate_ids
        ],
    }
    payload["risk_snapshot"] = {
        "source": "unity",
        "risk_snapshot_id": payload["risk_snapshot_id"],
        "risk_revision": 7,
        "calibration_id": payload["calibration_id"],
        "sampled_at_utc": "2026-07-23T08:00:00.0000000Z",
        "risk_policy_version": payload["risk_policy_version"],
        "physical_space": payload["physical_space_id"],
        "physical_position": payload["user_physical_position"],
        "physical_forward": payload["user_physical_forward"],
        "boundary_context": {"points": [[0.0, 0.0], [3.6, 0.0], [3.6, 5.0], [0.0, 5.0]]},
        "obstacle_context": [],
        "clearance_forward": 0.7,
        "clearance_left": 1.0,
        "clearance_right": 1.0,
        "risk_level": "high",
        "safe_directions": [],
        "unsafe_directions": ["forward"],
        "critical": False,
        "reset_required": False,
    }
    return payload


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


@pytest.mark.parametrize("encoding", ["utf-8", "utf-8-sig"])
def test_scene_semantics_accepts_utf8_with_or_without_bom(
    tmp_path: Path,
    encoding: str,
) -> None:
    payload = {"exhibits": [{"id": "E1"}]}
    (tmp_path / "scene_semantics.json").write_text(
        json.dumps(payload),
        encoding=encoding,
    )

    assert load_scene_semantics(tmp_path) == payload


def test_b1_narrow_room_returns_forward_and_logs_risk(tmp_path: Path) -> None:
    client = make_client(tmp_path)

    response = client.post("/api/guidance", json=base_payload("B1_mllm_only"))

    assert response.status_code == 200
    body = response.json()
    assert body["selected_target"] == "E5"
    assert body["movement_instruction"] == "move_forward_slowly"
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
        "candidate_set_id",
        "state_version",
        "risk_snapshot_id",
        "risk_revision",
        "target_role",
        "selected_target",
        "route_decision",
        "narration",
        "gaze_cue",
        "gaze_target_id",
        "movement_instruction",
        "physical_intent",
        "verifier",
    }
    assert set(body["verifier"]) == {"approved", "reason"}
    assert body["request_id"] == "request-test-001"
    assert body["session_id"] == "session-test-001"
    assert body["calibration_id"] == "calibration-test-001"
    assert body["risk_snapshot_id"] == "risk-test-001"
    assert body["risk_revision"] == 7
    assert body["decision_id"] == "decision-test-001"
    assert body["state_version"] == 3
    assert body["candidate_set_id"] == "candidates-test-001"
    assert body["target_role"] == "navigable_detour"
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
    assert "avatar_action" not in body
    assert "avatar_action" not in record
    assert "avatar_action" not in record["raw_guidance"]


def test_ours_full_narrow_room_redirects_away_from_forward(tmp_path: Path) -> None:
    client = make_client(tmp_path)
    payload = base_payload("ours_full")
    payload["user_physical_position"] = [1.8, 4.3]
    payload["risk_snapshot"]["physical_position"] = [1.8, 4.3]
    payload["risk_snapshot"]["safe_directions"] = ["left"]

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
    assert body["selected_target"] == "E3"
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
def test_all_conditions_select_only_from_frozen_model_candidates(condition: str, tmp_path: Path) -> None:
    client = make_client(tmp_path)
    payload = base_payload(condition)
    payload["physical_space_id"] = "wide_room"
    payload["user_physical_position"] = [2.5, 2.5]
    payload["risk_snapshot"]["physical_space"] = "wide_room"
    payload["risk_snapshot"]["physical_position"] = [2.5, 2.5]
    payload["risk_snapshot"]["clearance_forward"] = 2.5
    payload["risk_snapshot"]["clearance_left"] = 2.5
    payload["risk_snapshot"]["clearance_right"] = 2.5
    payload["risk_snapshot"]["risk_level"] = "low"
    payload["risk_snapshot"]["safe_directions"] = ["forward", "left", "right"]
    payload["risk_snapshot"]["unsafe_directions"] = []
    response = client.post("/api/guidance", json=payload)

    assert response.status_code == 200
    body = response.json()
    assert body["selected_target"] in payload["model_candidate_ids"]
    record = read_log_records(tmp_path)[0]
    assert record["candidate_set_id"] == payload["candidate_set_id"]
    assert record["model_candidate_ids"] == payload["model_candidate_ids"]
    assert record["raw_guidance"]["selected_target"] in payload["model_candidate_ids"]
    assert record["executed_guidance"] is None


def test_request_schema_requires_candidate_set_and_rejects_unknown_condition(tmp_path: Path) -> None:
    client = make_client(tmp_path)
    missing_target = base_payload("ours_full")
    missing_target.pop("candidate_set_id")

    missing_response = client.post("/api/guidance", json=missing_target)
    unknown_response = client.post(
        "/api/guidance",
        json={**base_payload("ours_full"), "condition": "unknown_condition"},
    )

    assert missing_response.status_code == 422
    assert any(item["loc"][-1] == "candidate_set_id" for item in missing_response.json()["detail"])
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


def test_api_rejects_missing_or_inconsistent_unity_risk_snapshot(tmp_path: Path) -> None:
    missing = base_payload("ours_full")
    missing.pop("risk_snapshot")
    inconsistent = base_payload("ours_full")
    inconsistent["risk_snapshot"]["risk_snapshot_id"] = "risk-other"

    missing_response = make_client(tmp_path).post("/api/guidance", json=missing)
    inconsistent_response = make_client(tmp_path).post("/api/guidance", json=inconsistent)

    assert missing_response.status_code == 422
    assert inconsistent_response.status_code == 422


@pytest.mark.parametrize(
    ("mutate", "expected_location"),
    [
        (lambda snapshot: snapshot.update({"physical_position": ["NaN", 1.0]}), "physical_position"),
        (lambda snapshot: snapshot["boundary_context"].update({"unexpected": True}), "unexpected"),
        (
            lambda snapshot: snapshot.update(
                {"obstacle_context": [{"id": "wall", "center": [0.0], "radius": 0.2}]}
            ),
            "center",
        ),
        (lambda snapshot: snapshot.update({"sampled_at_utc": "2026-07-23T08:00:00"}), "sampled_at_utc"),
    ],
)
def test_api_rejects_malformed_unity_risk_snapshot(
    mutate,
    expected_location: str,
    tmp_path: Path,
) -> None:
    payload = base_payload("ours_full")
    mutate(payload["risk_snapshot"])

    response = make_client(tmp_path).post("/api/guidance", json=payload)

    assert response.status_code == 422
    assert any(expected_location in item["loc"] for item in response.json()["detail"])


def test_pipeline_consumes_unity_risk_without_online_recomputation(tmp_path: Path) -> None:
    client = make_client(tmp_path)
    payload = base_payload("ours_full")
    payload["user_physical_position"] = [2.5, 2.5]
    payload["risk_snapshot"]["physical_position"] = [2.5, 2.5]
    payload["risk_snapshot"]["clearance_forward"] = 0.2
    payload["risk_snapshot"]["risk_level"] = "critical"
    payload["risk_snapshot"]["unsafe_directions"] = ["forward"]
    payload["risk_snapshot"]["critical"] = True

    response = client.post("/api/guidance", json=payload)

    assert response.status_code == 200
    record = read_log_records(tmp_path)[0]
    assert record["clearance_forward"] == 0.2
    assert record["boundary_risk"] == "critical"


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
        "candidate_set_id",
        "state_version",
        "risk_snapshot_id",
        "condition",
        "task_id",
        "physical_space_id",
        "current_exhibit",
        "risk_snapshot",
        "user_physical_position",
        "user_physical_forward",
        "participant_id",
        "user_task",
        "final_target",
        "preferred_task_target",
        "candidate_ids",
        "model_candidate_ids",
        "allowed_candidate_ids",
        "semantic_candidates",
        "user_virtual_position",
        "user_virtual_forward",
    }
    assert "candidate_set_id" in request_schema["required"]
    assert "model_candidate_ids" in request_schema["required"]
    assert request_schema["additionalProperties"] is False
    assert set(response_schema["required"]) == set(response_schema["properties"])
    assert response_schema["additionalProperties"] is False


def test_guidance_response_schema_rejects_legacy_avatar_action() -> None:
    payload = {
        "task_definition_version": "t2-main-target-v2",
        "guidance_schema_version": "guidance-schema-v2",
        "candidate_policy_version": "t2-candidate-policy-v1",
        "risk_policy_version": "unity-risk-policy-v1",
        "session_id": "session-test-001",
        "calibration_id": "calibration-test-001",
        "request_id": "request-test-001",
        "decision_id": "decision-test-001",
        "candidate_set_id": "candidates-test-001",
        "state_version": 3,
        "risk_snapshot_id": "risk-test-001",
        "risk_revision": 7,
        "target_role": "final_target",
        "selected_target": "E5",
        "route_decision": "direct_to_target",
        "narration": "请继续向前，前往重点展品。",
        "gaze_cue": "look_forward",
        "gaze_target_id": "E5_GazeTarget",
        "movement_instruction": "move_forward_slowly",
        "physical_intent": "semantic_only_no_physical_grounding",
        "verifier": {"approved": True, "reason": "approved"},
        "avatar_action": "point_forward",
    }

    with pytest.raises(ValueError, match="avatar_action"):
        GuidanceResponse.model_validate(payload)


def test_pipeline_rejects_internal_target_mismatch(tmp_path: Path) -> None:
    client = make_client(tmp_path)
    pipeline = client.app.state.pipeline
    original_generate = pipeline.guide.generate

    async def mismatched_generate(request, risk_state, affordance):
        draft = await original_generate(request, risk_state, affordance)
        return draft.model_copy(update={"selected_target": "E2"})

    pipeline.guide.generate = mismatched_generate

    response = client.post("/api/guidance", json=base_payload("ours_full"))

    assert response.status_code == 502
    assert response.json()["detail"] == (
        "Guidance draft contract violation: "
        "selected_target must belong to model_candidate_ids."
    )


@pytest.mark.parametrize(
    "mutation",
    [
        lambda payload: payload.update({"candidate_ids": ["E5", "E3", "E3"]}),
        lambda payload: payload.update({"model_candidate_ids": ["E2"]}),
        lambda payload: payload.update({"allowed_candidate_ids": ["E2"]}),
        lambda payload: payload.update({"semantic_candidates": []}),
    ],
)
def test_api_rejects_malformed_candidate_membership(mutation, tmp_path: Path) -> None:
    payload = base_payload("ours_full")
    mutation(payload)

    response = make_client(tmp_path).post("/api/guidance", json=payload)

    assert response.status_code == 422


def test_b5_structural_validation_remains_active_without_model_verifier(tmp_path: Path) -> None:
    payload = base_payload("B5_ours_without_verifier")
    payload["model_candidate_ids"] = ["E2"]

    response = make_client(tmp_path).post("/api/guidance", json=payload)

    assert response.status_code == 422
