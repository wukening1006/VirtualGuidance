"""测试 FastAPI guidance 接口、健康检查和 JSONL 日志记录行为。"""

import json
from pathlib import Path

from fastapi.testclient import TestClient

from backend.app import create_app


def make_client(tmp_path: Path) -> TestClient:
    config_dir = Path(__file__).resolve().parents[2] / "Assets" / "Configs"
    app = create_app(config_dir=config_dir, log_dir=tmp_path)
    return TestClient(app)


def base_payload(condition: str) -> dict:
    return {
        "request_id": "request-test-001",
        "decision_id": "decision-test-001",
        "state_version": 3,
        "condition": condition,
        "task_id": "T2_main_target",
        "physical_space_id": "narrow_room",
        "current_exhibit": "E1",
        "user_physical_position": [1.0, 4.3],
        "user_physical_forward": [0.0, 1.0],
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
        "request_id",
        "decision_id",
        "state_version",
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
    assert body["decision_id"] == "decision-test-001"
    assert body["state_version"] == 3
    assert_chinese_narration(body)

    record = read_log_records(tmp_path)[0]
    assert record["request_id"] == "request-test-001"
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
