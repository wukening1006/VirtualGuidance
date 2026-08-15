"""测试 RDW 路线选择接口、身份回显和不可绕过的 route_id 约束。"""

from __future__ import annotations

import asyncio
import json
from pathlib import Path

import pytest
from fastapi.testclient import TestClient

from backend.app import create_app
from backend.route_selection import (
    DeterministicRouteSelectionGenerator,
    RouteSelectionDraft,
)
from backend.schemas import RouteSelectionRequest


CONFIG_DIR = Path(__file__).resolve().parents[2] / "Assets" / "Configs"


def route_selection_payload() -> dict:
    """构造由 Unity 冻结的最小合法 Top-K 路线请求。"""

    return {
        "planning_schema_version": "rdw-route-selection-v1",
        "session_id": "session-route-001",
        "request_id": "request-route-001",
        "plan_id": "plan-001",
        "snapshot_id": "snapshot-001",
        "generation": 7,
        "task_state_version": 12,
        "calibration_id": "calibration-001",
        "physical_space_id": "narrow_room",
        "risk_snapshot_id": "risk-001",
        "risk_revision": 21,
        "current_node_id": "E1",
        "final_target_id": "E5",
        "estimator_id": "s2c",
        "estimator_version": "1",
        "route_candidates": [
            {
                "route_id": "route-a",
                "ordered_node_ids": ["E3", "E5"],
                "first_target_id": "E3",
                "route_utility": 0.65,
                "intervention_level": "medium",
                "estimator_id": "s2c",
                "estimator_version": "1",
                "edges": [
                    {
                        "from_node_id": "E1",
                        "to_node_id": "E3",
                        "relative_yaw_deg": -35.0,
                        "clearance_m": 0.9,
                        "rdw_benefit": 0.7,
                        "gain_cost": 0.2,
                        "reset_pressure": 0.1,
                    },
                    {
                        "from_node_id": "E3",
                        "to_node_id": "E5",
                        "relative_yaw_deg": 20.0,
                        "clearance_m": 0.8,
                        "rdw_benefit": 0.6,
                        "gain_cost": 0.3,
                        "reset_pressure": 0.2,
                    },
                ],
            },
            {
                "route_id": "route-b",
                "ordered_node_ids": ["E4", "E5"],
                "first_target_id": "E4",
                "route_utility": 0.85,
                "intervention_level": "low",
                "estimator_id": "s2c",
                "estimator_version": "1",
                "edges": [
                    {
                        "from_node_id": "E1",
                        "to_node_id": "E4",
                        "relative_yaw_deg": 25.0,
                        "clearance_m": 1.1,
                        "rdw_benefit": 0.8,
                        "gain_cost": 0.1,
                        "reset_pressure": 0.05,
                    },
                    {
                        "from_node_id": "E4",
                        "to_node_id": "E5",
                        "relative_yaw_deg": -15.0,
                        "clearance_m": 1.0,
                        "rdw_benefit": 0.75,
                        "gain_cost": 0.15,
                        "reset_pressure": 0.1,
                    },
                ],
            },
        ],
    }


class FixedRouteSelector:
    """测试用路线选择器，只返回允许的协议字段。"""

    def __init__(self, route_id: str) -> None:
        self.route_id = route_id

    async def generate(self, request: RouteSelectionRequest) -> RouteSelectionDraft:
        return RouteSelectionDraft(
            route_id=self.route_id,
            narration="请沿当前路线继续参观。",
        )


def make_client(tmp_path: Path, selector: FixedRouteSelector) -> TestClient:
    app = create_app(
        config_dir=CONFIG_DIR,
        log_dir=tmp_path,
        route_selector=selector,
    )
    return TestClient(app)


def test_endpoint_returns_only_existing_route_and_echoes_identity(tmp_path: Path) -> None:
    payload = route_selection_payload()

    response = make_client(tmp_path, FixedRouteSelector("route-a")).post(
        "/api/planning/route-selection",
        json=payload,
    )

    assert response.status_code == 200
    body = response.json()
    assert body == {
        "planning_schema_version": payload["planning_schema_version"],
        "session_id": payload["session_id"],
        "request_id": payload["request_id"],
        "plan_id": payload["plan_id"],
        "snapshot_id": payload["snapshot_id"],
        "generation": payload["generation"],
        "route_id": "route-a",
        "narration": "请沿当前路线继续参观。",
        "verifier": {
            "approved": True,
            "reason": "route_id belongs to frozen route_candidates; physical safety remains Unity-owned.",
        },
    }


def test_endpoint_rejects_selector_route_outside_frozen_candidates(tmp_path: Path) -> None:
    response = make_client(tmp_path, FixedRouteSelector("route-outside")).post(
        "/api/planning/route-selection",
        json=route_selection_payload(),
    )

    assert response.status_code == 502
    assert "outside candidates" in response.json()["detail"]


@pytest.mark.parametrize("forbidden_field", ["gain", "reset", "safety"])
def test_model_output_rejects_forbidden_control_fields(forbidden_field: str) -> None:
    request = RouteSelectionRequest.model_validate(route_selection_payload())
    output = {
        "route_id": "route-a",
        "narration": "请继续前往下一站。",
        forbidden_field: True,
    }

    with pytest.raises(ValueError, match="schema validation failed"):
        RouteSelectionDraft.parse_model_json(json.dumps(output), request)


def test_model_output_rejects_unknown_route_before_pipeline() -> None:
    request = RouteSelectionRequest.model_validate(route_selection_payload())

    with pytest.raises(ValueError, match="frozen route_candidates"):
        RouteSelectionDraft.parse_model_json(
            json.dumps(
                {
                    "route_id": "route-outside",
                    "narration": "请继续前往下一站。",
                }
            ),
            request,
        )


def test_offline_selector_is_deterministic_for_utility_then_route_id() -> None:
    payload = route_selection_payload()
    payload["route_candidates"][0]["route_utility"] = 0.85
    request = RouteSelectionRequest.model_validate(payload)
    selector = DeterministicRouteSelectionGenerator()

    first = asyncio.run(selector.generate(request))
    second = asyncio.run(selector.generate(request))

    assert first == second
    assert first.route_id == "route-a"

