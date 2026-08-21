"""验证四任务动态起点的跨端启动快照契约。"""

import pytest

from backend.schemas import (
    NaturalLanguageTaskState,
    RouteSelectionRequest,
    SemanticPlanRequest,
    TaskId,
)
from backend.tests.test_route_selection import route_selection_payload
from backend.tests.test_semantic_planning import semantic_payload


def test_all_formal_task_ids_are_accepted() -> None:
    assert {item.value for item in TaskId} == {
        "T1_overview",
        "T2_main_target",
        "T3_compare",
        "T4_next_step",
    }


def test_semantic_startup_snapshot_allows_empty_current_exhibit() -> None:
    payload = semantic_payload()
    payload["task_state"].update(
        current_exhibit_id="",
        visited_exhibit_ids=[],
        intermediate_visit_count=0,
    )

    request = SemanticPlanRequest.model_validate(payload)

    assert request.task_state.current_exhibit_id == ""
    assert request.task_state.visited_exhibit_ids == []


def test_rdw_startup_snapshot_uses_runtime_root_node() -> None:
    payload = route_selection_payload()
    payload["current_node_id"] = "__runtime_start__"
    payload["task_state"].update(
        current_exhibit_id="",
        visited_exhibit_ids=[],
        intermediate_visit_count=0,
    )

    request = RouteSelectionRequest.model_validate(payload)

    assert request.current_node_id == "__runtime_start__"
    assert request.task_state.current_exhibit_id == ""


def test_empty_current_exhibit_is_restricted_to_startup_snapshot() -> None:
    with pytest.raises(ValueError):
        NaturalLanguageTaskState(
            phase="exploring",
            current_exhibit_id="",
            visited_exhibit_ids=["E1"],
            intermediate_visit_count=0,
        )
