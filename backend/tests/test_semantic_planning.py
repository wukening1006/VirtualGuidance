"""Semantic-only 局部规划协议和确定性 Verifier 测试。"""

from pathlib import Path

import pytest
from fastapi.testclient import TestClient
from pydantic import ValidationError

from backend.app import create_app
from backend.semantic_planning import DeterministicSemanticPlanGenerator
from backend.schemas import (
    SemanticPlanDraft,
    SemanticPlanRequest,
    formal_experiment_profile,
)


def semantic_payload() -> dict:
    node = lambda exhibit_id: {
        "id": exhibit_id,
        "name": exhibit_id,
        "category": "青铜器",
        "culture": "古蜀文明",
        "chronology": "青铜时代",
        "description": "静态语义节点",
        "visual_features": ["造型"],
        "cultural_significance": ["祭祀"],
        "key_concepts": ["主题"],
        "themes": ["TH_RITUAL"],
        "interpretation_notes": [],
        "semantic_edges": [],
        "spatial_edges": [],
    }

    spatial = lambda source, target, edge_id: {
        "id": edge_id,
        "source": source,
        "target": target,
        "relation_type": "adjacent_diagonal",
        "bidirectional": True,
        "virtual_distance_m": 2.0,
        "relative_direction_from_source": "diagonal",
    }
    return {
        "planning_schema_version": "semantic-local-planning-v1",
        "session_id": "session-semantic-001",
        "request_id": "request-semantic-001",
        "generation": 1,
        "task_state_version": 4,
        "theme_goal": {
            "theme_id": "TH_RITUAL",
            "name": "祭祀",
            "description": "主题描述",
        },
        "current_exhibit_id": "E1",
        "visited_exhibit_ids": ["E1"],
        "theme_progress": {"TH_RITUAL": 0.0},
        "local_semantic_nodes": [node("E1"), node("E3"), node("E5"), node("E8")],
        "local_spatial_edges": [
            spatial("E1", "E3", "SP_13"),
            spatial("E3", "E5", "SP_35"),
            spatial("E5", "E8", "SP_58"),
        ],
        "local_semantic_edges": [],
    }


def test_semantic_request_rejects_physical_fields() -> None:
    payload = semantic_payload()
    payload["risk_snapshot"] = {"risk_level": "low"}
    with pytest.raises(ValidationError):
        SemanticPlanRequest.model_validate(payload)


def test_semantic_request_rejects_unknown_node() -> None:
    payload = semantic_payload()
    payload["local_spatial_edges"][0]["target"] = "E_UNKNOWN"
    with pytest.raises(ValidationError):
        SemanticPlanRequest.model_validate(payload)


def test_semantic_plan_endpoint_verifies_graph_membership() -> None:
    config_dir = Path(__file__).resolve().parents[2] / "Assets" / "Configs"
    app = create_app(
        config_dir=config_dir,
        semantic_planner=DeterministicSemanticPlanGenerator(),
    )
    response = TestClient(app).post("/api/planning/semantic-local", json=semantic_payload())
    assert response.status_code == 200
    body = response.json()
    assert body["planning_schema_version"] == "semantic-local-planning-v1"
    assert body["request_id"] == "request-semantic-001"
    assert body["verifier"]["approved"] is True
    assert body["ordered_node_ids"] == ["E3", "E5", "E8"]


class InvalidSpatialPlanGenerator:
    """模拟模型把语义边误当成空间边。"""

    async def generate(self, request: SemanticPlanRequest) -> SemanticPlanDraft:
        return SemanticPlanDraft(
            ordered_node_ids=["E3", "E4"],
            narration="先比较两件展品。",
        )


def test_semantic_plan_falls_back_when_model_uses_semantic_edge_as_route() -> None:
    payload = semantic_payload()
    payload["local_semantic_nodes"].append(payload["local_semantic_nodes"][1].copy())
    payload["local_semantic_nodes"][-1]["id"] = "E4"
    payload["local_semantic_edges"].append(
        {
            "id": "SE34",
            "source": "E3",
            "target": "E4",
            "relation_type": "compare_with",
            "directed": False,
            "description": "语义比较",
            "evidence_status": "supported",
        }
    )
    config_dir = Path(__file__).resolve().parents[2] / "Assets" / "Configs"
    app = create_app(config_dir=config_dir, semantic_planner=InvalidSpatialPlanGenerator())

    response = TestClient(app).post("/api/planning/semantic-local", json=payload)

    assert response.status_code == 200
    body = response.json()
    assert body["ordered_node_ids"] == ["E3", "E5", "E8"]
    assert body["verifier"] == {
        "approved": True,
        "reason": "semantic_plan_is_graph_valid",
    }


def test_semantic_plan_generator_context_has_no_physical_contract() -> None:
    request = SemanticPlanRequest.model_validate(semantic_payload())
    assert "risk_snapshot" not in request.model_dump()
    assert "clearance" not in request.model_dump()
    assert "rdw_benefit" not in request.model_dump()
    assert "reset_pressure" not in request.model_dump()


def test_semantic_plan_rejects_return_to_current_exhibit() -> None:
    payload = semantic_payload()
    payload["local_semantic_nodes"].append(payload["local_semantic_nodes"][0].copy())
    payload["local_semantic_nodes"][-1]["id"] = "E9"
    payload["local_spatial_edges"].append(
        {
            "id": "SP_39",
            "source": "E3",
            "target": "E1",
            "relation_type": "adjacent_diagonal",
            "bidirectional": True,
            "virtual_distance_m": 2.0,
            "relative_direction_from_source": "diagonal",
        }
    )
    request = SemanticPlanRequest.model_validate(payload)
    from backend.schemas import SemanticPlanDraft, verify_semantic_plan

    result = verify_semantic_plan(
        request,
        SemanticPlanDraft(ordered_node_ids=["E3", "E1"], narration="循环"),
    )
    assert result.approved is False
    assert result.reason == "plan_starts_at_current_exhibit"


def test_semantic_plan_honors_directed_spatial_edges() -> None:
    payload = semantic_payload()
    payload["local_spatial_edges"][0]["bidirectional"] = False
    payload["current_exhibit_id"] = "E3"
    payload["visited_exhibit_ids"] = ["E3"]
    request = SemanticPlanRequest.model_validate(payload)
    from backend.schemas import SemanticPlanDraft, verify_semantic_plan

    result = verify_semantic_plan(
        request,
        SemanticPlanDraft(ordered_node_ids=["E1"], narration="反向"),
    )
    assert result.approved is False
    assert result.reason == "missing_spatial_edge:E3->E1"


def test_formal_experiment_profiles_match_three_condition_matrix() -> None:
    assert formal_experiment_profile("B1_semantic_only").model_dump() == {
        "condition_id": "B1_semantic_only",
        "planner_mode": "SemanticOnly",
        "continuous_rdw_enabled": False,
        "rdw_planning_mode": "Legacy",
        "reset_mode": "Normal",
    }
    assert formal_experiment_profile("B2_semantic_only_continuous").continuous_rdw_enabled
    assert formal_experiment_profile("ours_rdw_grounded_topk").rdw_planning_mode == "RdwConstrained"
    with pytest.raises(ValueError):
        formal_experiment_profile("ours_full")
