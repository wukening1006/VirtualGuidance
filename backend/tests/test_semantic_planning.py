"""自然语言局部规划协议和确定性 Verifier 测试。"""

from pathlib import Path

import pytest
from fastapi.testclient import TestClient
from pydantic import ValidationError

from backend.app import create_app
from backend.semantic_planning import DeterministicSemanticPlanGenerator
from backend.services.mllm_provider import MLLMTimeoutError
from backend.schemas import (
    SemanticPlanDraft,
    SemanticPlanRequest,
    formal_experiment_profile,
    verify_semantic_plan,
)


def semantic_payload() -> dict:
    def node(exhibit_id: str) -> dict:
        return {
            "id": exhibit_id,
            "name": exhibit_id,
            "category": "青铜器",
            "culture": "古蜀文明",
            "chronology": "青铜时代",
            "description": "静态语义节点",
            "visual_features": ["造型"],
            "cultural_significance": ["祭祀"],
            "key_concepts": ["文化"],
            "interpretation_notes": [],
            "semantic_edges": [],
        }

    def semantic(source: str, target: str, edge_id: str) -> dict:
        return {
            "id": edge_id,
            "source": source,
            "target": target,
            "relation_type": "thematic_association",
            "directed": False,
            "description": "有助于理解青铜神树",
            "evidence_status": "supported",
        }

    return {
        "planning_schema_version": "semantic-local-planning-v2",
        "session_id": "session-semantic-001",
        "request_id": "request-semantic-001",
        "generation": 1,
        "task_state_version": 4,
        "task": {
            "task_id": "T2_main_target",
            "user_task": "参观相关展品后，最后抵达青铜神树。",
            "final_goal": {"exhibit_id": "E5", "name": "青铜神树", "required": True},
            "intermediate_policy": {
                "minimum_visits": 3,
                "maximum_visits": 4,
                "exclude_final_until_ready": True,
                "require_semantic_connection": True,
            },
        },
        "task_state": {
            "phase": "exploring",
            "current_exhibit_id": "E1",
            "visited_exhibit_ids": ["E1"],
            "intermediate_visit_count": 0,
            "required_target_id": "",
        },
        "local_semantic_nodes": [
            node("E1"), node("E2"), node("E3"), node("E4"), node("E5"), node("E8")
        ],
        "local_semantic_edges": [
            semantic("E2", "E5", "SE_25"),
            semantic("E3", "E5", "SE_35"),
            semantic("E4", "E5", "SE_45"),
        ],
    }


def verify(payload: dict, node_ids: list[str]):
    request = SemanticPlanRequest.model_validate(payload)
    return verify_semantic_plan(
        request,
        SemanticPlanDraft(ordered_node_ids=node_ids, narration="测试讲解"),
    )


def test_semantic_request_rejects_physical_fields() -> None:
    payload = semantic_payload()
    payload["risk_snapshot"] = {"risk_level": "low"}
    with pytest.raises(ValidationError):
        SemanticPlanRequest.model_validate(payload)


def test_semantic_request_rejects_unknown_node() -> None:
    payload = semantic_payload()
    payload["local_semantic_edges"][0]["target"] = "E_UNKNOWN"
    with pytest.raises(ValidationError):
        SemanticPlanRequest.model_validate(payload)


def test_semantic_plan_rejects_multiple_targets() -> None:
    payload = semantic_payload()
    with pytest.raises(ValidationError):
        SemanticPlanDraft(
            ordered_node_ids=["E2", "E3"],
            narration="不应一次提交多个子目标",
        )


def test_semantic_plan_endpoint_generates_related_intermediate_route() -> None:
    config_dir = Path(__file__).resolve().parents[2] / "Assets" / "Configs"
    app = create_app(config_dir=config_dir, semantic_planner=DeterministicSemanticPlanGenerator())

    response = TestClient(app).post("/api/planning/semantic-local", json=semantic_payload())

    assert response.status_code == 200
    body = response.json()
    assert body["planning_schema_version"] == "semantic-local-planning-v2"
    assert body["ordered_node_ids"] == ["E2"]
    assert body["verifier"]["approved"] is True


class TimeoutSemanticPlanGenerator:
    """模拟 MLLM 超时，验证静态语义图兜底。"""

    async def generate(self, request: SemanticPlanRequest) -> SemanticPlanDraft:
        raise MLLMTimeoutError("model timed out")


class InvalidSemanticPlanGenerator:
    """模拟模型提前选择最终目标，验证 Verifier 拒绝后的兜底。"""

    async def generate(self, request: SemanticPlanRequest) -> SemanticPlanDraft:
        return SemanticPlanDraft(ordered_node_ids=["E5"], narration="错误地提前前往最终目标")


@pytest.mark.parametrize(
    "planner",
    [TimeoutSemanticPlanGenerator(), InvalidSemanticPlanGenerator()],
)
def test_semantic_plan_endpoint_falls_back_when_model_fails_or_is_rejected(planner) -> None:
    config_dir = Path(__file__).resolve().parents[2] / "Assets" / "Configs"
    app = create_app(config_dir=config_dir, semantic_planner=planner)

    response = TestClient(app).post("/api/planning/semantic-local", json=semantic_payload())

    assert response.status_code == 200
    body = response.json()
    assert body["ordered_node_ids"] == ["E2"]
    assert body["verifier"]["approved"] is True


def test_final_goal_is_locked_before_three_intermediate_visits() -> None:
    payload = semantic_payload()
    result = verify(payload, ["E5"])

    assert result.approved is False
    assert result.reason == "semantic_plan_final_goal_locked"


def test_final_goal_is_allowed_as_single_target_after_three_visits() -> None:
    payload = semantic_payload()
    payload["task_state"] = {
        "phase": "exploring",
        "current_exhibit_id": "E4",
        "visited_exhibit_ids": ["E1", "E2", "E3", "E4"],
        "intermediate_visit_count": 3,
        "required_target_id": "",
    }

    assert verify(payload, ["E5"]).approved is True


def test_final_goal_is_required_after_four_intermediate_visits() -> None:
    payload = semantic_payload()
    payload["task_state"] = {
        "phase": "final_approach",
        "current_exhibit_id": "E4",
        "visited_exhibit_ids": ["E1", "E2", "E8", "E4"],
        "intermediate_visit_count": 4,
        "required_target_id": "E5",
    }

    result = verify(payload, ["E3"])

    assert result.approved is False
    assert result.reason == "semantic_plan_final_goal_required"


def test_spatially_reachable_candidate_without_semantic_edge_is_allowed() -> None:
    payload = semantic_payload()
    payload["task_state"] = {
        "phase": "exploring",
        "current_exhibit_id": "E2",
        "visited_exhibit_ids": ["E1", "E2"],
        "intermediate_visit_count": 1,
        "required_target_id": "",
    }
    candidate = dict(payload["local_semantic_nodes"][-1])
    candidate["id"] = "E7"
    candidate["name"] = "E7"
    payload["local_semantic_nodes"].append(candidate)
    result = verify(payload, ["E7"])

    assert result.approved is True



def test_semantic_plan_rejects_return_to_current_exhibit() -> None:
    result = verify(semantic_payload(), ["E2", "E1"])

    assert result.approved is False
    assert result.reason == "semantic_plan_starts_at_current_exhibit"


def test_candidate_without_direct_spatial_edge_is_allowed() -> None:
    payload = semantic_payload()
    payload["task_state"] = {
        "phase": "exploring",
        "current_exhibit_id": "E2",
        "visited_exhibit_ids": ["E2"],
        "intermediate_visit_count": 0,
        "required_target_id": "",
    }
    payload["local_semantic_edges"].append({
        **payload["local_semantic_edges"][0],
        "id": "SE_21",
        "source": "E2",
        "target": "E1",
    })

    result = verify(payload, ["E1"])

    assert result.approved is True


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
