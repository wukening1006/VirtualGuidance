import csv
import json
from pathlib import Path

import pytest

from backend.session_metrics import build_session_result, write_results_csv


def _write_jsonl(path: Path, rows: list[dict]) -> None:
    path.write_text("".join(json.dumps(row) + "\n" for row in rows), encoding="utf-8")


def _session(root: Path, name: str, reset_count: int = 2) -> Path:
    directory = root / name
    directory.mkdir()
    (directory / "session_meta.json").write_text(
        json.dumps({
            "session_id": name,
            "participant_id": name[:2],
            "condition": "ours_rdw_grounded_topk",
            "route_id": "E1>E5",
            "near_boundary_threshold_m": 0.5,
        }),
        encoding="utf-8",
    )
    events = [
        {"timestamp": "2026-01-01T00:00:00Z", "elapsed_s": 0, "event_name": "session.started", "data": {"event_type": "session_started"}},
        {"elapsed_s": 1, "event_name": "guidance.requested", "trace": {"request_id": "c1"}, "data": {"event_type": "guidance_requested", "k_before": 5, "k_after": 2}},
        {"elapsed_s": 2, "event_name": "guidance.received", "trace": {"request_id": "c1"}, "data": {"event_type": "guidance_received", "latency_s": 1}},
        {"elapsed_s": 1, "data": {"event_type": "planner_waiting_start", "interval_id": "w1"}},
        {"elapsed_s": 5, "data": {"event_type": "planner_waiting_end", "interval_id": "w1"}},
        {"elapsed_s": 6, "data": {"event_type": "planner_waiting_start", "interval_id": "w2"}},
        {"elapsed_s": 9, "data": {"event_type": "planner_waiting_end", "interval_id": "w2"}},
        {"elapsed_s": 4, "data": {"event_type": "near_boundary_enter"}},
        {"elapsed_s": 5, "data": {"event_type": "collision"}},
        {"elapsed_s": 6, "data": {"event_type": "exhibit_dwell_start"}},
        {"elapsed_s": 8, "data": {"event_type": "exhibit_dwell_end"}},
    ]
    for index in range(reset_count):
        at = 3 + index * 4
        events.extend([
            {
                "elapsed_s": at,
                "data": {
                    "event_type": "reset_started",
                    "reset_id": f"r{index}",
                    "metric_snapshot": {"virtual_distance_m": 1 + index * 8},
                },
            },
            {
                "elapsed_s": at + 1,
                "data": {
                    "event_type": "reset_completed",
                    "reset_id": f"r{index}",
                    "metric_snapshot": {"virtual_distance_m": 2 + index * 8},
                },
            },
        ])
    events.append({"elapsed_s": 10, "data": {"event_type": "session_ended"}})
    _write_jsonl(directory / "events.jsonl", events)
    _write_jsonl(
        directory / "telemetry.jsonl",
        [
            {"elapsed_s": 0, "data": {"state": "idle", "sample_dt_s": 0, "virtual_position": {"x": 0, "y": 0, "z": 0}, "virtual_distance_m": 0, "minimum_clearance_m": 2}},
            {"elapsed_s": 3, "data": {"state": "active_walking", "sample_dt_s": 3, "virtual_position": {"x": 3, "y": 0, "z": 0}, "virtual_distance_m": 3, "minimum_clearance_m": 0.4}},
            {"elapsed_s": 4, "data": {"state": "reset", "sample_dt_s": 1, "virtual_position": {"x": 3, "y": 0, "z": 0}, "virtual_distance_m": 3, "minimum_clearance_m": 0.4}},
            {"elapsed_s": 6, "data": {"state": "active_walking", "sample_dt_s": 2, "virtual_position": {"x": 5, "y": 0, "z": 0}, "virtual_distance_m": 5, "minimum_clearance_m": 1}},
            {"elapsed_s": 7, "data": {"state": "idle", "sample_dt_s": 1, "virtual_position": {"x": 5, "y": 0, "z": 0}, "virtual_distance_m": 5, "minimum_clearance_m": 0.7}},
            {"elapsed_s": 8, "data": {"state": "reset", "sample_dt_s": 1, "virtual_position": {"x": 5, "y": 0, "z": 0}, "virtual_distance_m": 5, "minimum_clearance_m": 1}},
            {"elapsed_s": 10, "data": {"state": "active_walking", "sample_dt_s": 2, "virtual_position": {"x": 10, "y": 0, "z": 0}, "virtual_distance_m": 10, "minimum_clearance_m": 1.5}},
        ],
    )
    return directory


def test_build_session_result_is_rebuildable_and_keeps_valid_counts(tmp_path: Path) -> None:
    session = _session(tmp_path, "P1_S1")

    first = build_session_result(session)
    (session / "result.json").unlink()
    second = build_session_result(session)

    assert first == second
    assert first["navigation"]["virtual_distance_m"] == 10
    assert first["navigation"]["navigation_time_s"] == 8
    assert first["reset"]["reset_count"] == 2
    assert first["reset"]["vdbr_mean"] == 7
    assert first["reset"]["vdbr_median"] == 7
    assert first["reset"]["tebr_mean"] == 2
    assert first["reset"]["tebr_median"] == 2
    assert first["reset"]["vdbr_valid_n"] == 1
    assert first["physical_safety"]["near_boundary_events"] == 1
    assert first["physical_safety"]["collision_count"] == 1
    assert first["llm_efficiency"]["latency_p95_s"] == 1
    assert first["llm_efficiency"]["blocking_time_s"] == 4
    assert first["llm_efficiency"]["blocking_ratio"] == pytest.approx(0.5)
    assert first["source_counts"]["planner_waiting_valid_n"] == 2
    assert first["llm_efficiency"]["candidate_reduction_rate"] == pytest.approx(0.6)
    assert first["experiment_completed"] is False
    assert first["visited_exhibit_ids"] == []


def test_result_keeps_last_non_empty_visited_exhibit_order(tmp_path: Path) -> None:
    session = _session(tmp_path, "P1_S4", reset_count=0)
    events_path = session / "events.jsonl"
    events = [json.loads(line) for line in events_path.read_text(encoding="utf-8").splitlines()]
    events.insert(-1, {"elapsed_s": 2, "data": {"visited_exhibit_ids": ["E1", "E3"]}})
    events.insert(-1, {"elapsed_s": 8, "data": {"visited_exhibit_ids": ["E1", "E3", "E4", "E5"]}})
    _write_jsonl(events_path, events)

    result = build_session_result(session)

    assert result["visited_exhibit_ids"] == ["E1", "E3", "E4", "E5"]


def test_result_marks_experiment_completed_from_task_terminal_event(tmp_path: Path) -> None:
    session = _session(tmp_path, "P1_S2", reset_count=0)
    events_path = session / "events.jsonl"
    events = [json.loads(line) for line in events_path.read_text(encoding="utf-8").splitlines()]
    events.insert(-1, {"elapsed_s": 9, "data": {"event_type": "task_completed"}})
    _write_jsonl(events_path, events)

    result = build_session_result(session)

    assert result["experiment_completed"] is True


def test_result_accepts_legacy_completed_auto_end_reason(tmp_path: Path) -> None:
    session = _session(tmp_path, "P1_S3", reset_count=0)
    meta_path = session / "session_meta.json"
    meta = json.loads(meta_path.read_text(encoding="utf-8"))
    meta["end_reason"] = "completed_auto"
    meta_path.write_text(json.dumps(meta), encoding="utf-8")

    result = build_session_result(session)

    assert result["experiment_completed"] is True


def test_single_reset_session_is_not_dropped(tmp_path: Path) -> None:
    result = build_session_result(_session(tmp_path, "P2_S1", reset_count=1))

    assert result["reset"]["reset_count"] == 1
    assert result["reset"]["vdbr_valid_n"] == 0
    assert result["reset"]["vdbr_total_n"] == 0
    assert result["reset"]["vdbr_mean"] is None


def test_cross_session_zero_reset_rate_only_exists_in_csv(tmp_path: Path) -> None:
    with_resets = _session(tmp_path, "P1_S1", reset_count=2)
    without_resets = _session(tmp_path, "P2_S1", reset_count=0)
    output = tmp_path / "results.csv"

    single = build_session_result(without_resets)
    write_results_csv([with_resets, without_resets], output)
    row = next(csv.DictReader(output.open(encoding="utf-8")))

    assert "zero_reset_rate" not in single["reset"]
    assert single["reset"]["zero_reset"] is True
    assert float(row["zero_reset_rate"]) == 0.5


def test_results_csv_groups_sessions_by_condition(tmp_path: Path) -> None:
    first = _session(tmp_path, "P1_S1", reset_count=0)
    second = _session(tmp_path, "P2_S1", reset_count=1)
    meta = json.loads((second / "session_meta.json").read_text(encoding="utf-8"))
    meta["condition"] = "baseline"
    (second / "session_meta.json").write_text(json.dumps(meta), encoding="utf-8")
    output = tmp_path / "results.csv"

    write_results_csv([first, second], output)
    rows = list(csv.DictReader(output.open(encoding="utf-8")))

    assert [row["condition"] for row in rows] == ["baseline", "ours_rdw_grounded_topk"]
    assert all(row["session_count"] == "1" for row in rows)


def test_legacy_logs_fall_back_to_llm_call_intervals(tmp_path: Path) -> None:
    session = _session(tmp_path, "P3_S1", reset_count=0)
    events_path = session / "events.jsonl"
    events = [
        row
        for row in (json.loads(line) for line in events_path.read_text(encoding="utf-8").splitlines())
        if row.get("data", {}).get("event_type") not in {"planner_waiting_start", "planner_waiting_end"}
    ]
    _write_jsonl(events_path, events)

    result = build_session_result(session)

    assert result["llm_efficiency"]["blocking_time_s"] == 1
    assert result["source_counts"]["planner_waiting_valid_n"] == 1


def test_cancelled_reset_closes_duration_interval(tmp_path: Path) -> None:
    session = _session(tmp_path, "P4_S1", reset_count=0)
    events_path = session / "events.jsonl"
    events = [
        {"elapsed_s": 0, "data": {"event_type": "session_started"}},
        {"elapsed_s": 2, "data": {"event_type": "reset_started", "reset_id": "cancelled-1"}},
        {"elapsed_s": 5, "data": {"event_type": "reset_cancelled", "reset_id": "cancelled-1"}},
        {"elapsed_s": 6, "data": {"event_type": "session_ended"}},
    ]
    _write_jsonl(events_path, events)

    result = build_session_result(session)

    assert result["reset"]["reset_count"] == 0
    assert result["source_counts"]["reset_cancelled_n"] == 1
    assert result["reset"]["reset_duration_s"] == 3


def test_walking_time_requires_motion_and_ignores_replan_state_spam(tmp_path: Path) -> None:
    session = _session(tmp_path, "P5_S1", reset_count=0)
    events_path = session / "events.jsonl"
    events = [
        {"elapsed_s": 0, "data": {"event_type": "session_started"}},
        {"elapsed_s": 10, "data": {"event_type": "session_ended"}},
    ]
    events.extend(
        {"elapsed_s": value / 10, "data": {"event_type": "holding_for_replan"}}
        for value in range(1, 100)
    )
    _write_jsonl(events_path, events)
    _write_jsonl(
        session / "telemetry.jsonl",
        [
            {"elapsed_s": 0, "data": {"state": "idle", "sample_dt_s": 0, "virtual_position": {"x": 0, "z": 0}}},
            {"elapsed_s": 2, "data": {"state": "active_walking", "sample_dt_s": 2, "virtual_position": {"x": 1, "z": 0}}},
            {"elapsed_s": 4, "data": {"state": "idle", "sample_dt_s": 2, "virtual_position": {"x": 1, "z": 0}}},
            {"elapsed_s": 10, "data": {"state": "ended", "sample_dt_s": 6, "virtual_position": {"x": 1, "z": 0}}},
        ],
    )

    result = build_session_result(session)

    assert result["navigation"]["navigation_time_s"] == 10
    assert result["navigation"]["active_walking_time_s"] == 2
    assert result["navigation"]["walking_continuity"] == pytest.approx(0.2)
    assert result["source_counts"]["planner_waiting_valid_n"] == 0


def test_invalid_candidate_counts_are_not_used(tmp_path: Path) -> None:
    session = _session(tmp_path, "P6_S1", reset_count=0)
    events_path = session / "events.jsonl"
    events = [
        {"elapsed_s": 0, "data": {"event_type": "session_started", "k_before": 0, "k_after": 0}},
        {"elapsed_s": 1, "data": {"event_type": "guidance_requested", "k_before": 2, "k_after": 3}},
        {"elapsed_s": 2, "data": {"event_type": "guidance_requested", "k_before": 4, "k_after": 2}},
        {"elapsed_s": 10, "data": {"event_type": "session_ended"}},
    ]
    _write_jsonl(events_path, events)

    result = build_session_result(session)

    assert result["llm_efficiency"]["candidate_reduction_rate"] == pytest.approx(0.5)
    assert result["source_counts"]["candidate_reduction_valid_n"] == 1


def test_collision_and_latency_only_use_completed_pairs(tmp_path: Path) -> None:
    session = _session(tmp_path, "P7_S1", reset_count=0)
    events_path = session / "events.jsonl"
    events = [
        {"elapsed_s": 0, "data": {"event_type": "session_started"}},
        {"elapsed_s": 1, "trace": {"request_id": "ok"}, "data": {"event_type": "guidance_requested", "call_id": "ok"}},
        {"elapsed_s": 3, "trace": {"request_id": "ok"}, "data": {"event_type": "guidance_received", "call_id": "ok", "latency_s": 2}},
        {"elapsed_s": 4, "trace": {"request_id": "cancelled"}, "data": {"event_type": "guidance_requested", "call_id": "cancelled"}},
        {"elapsed_s": 5, "trace": {"request_id": "cancelled"}, "data": {"event_type": "guidance_cancelled", "call_id": "cancelled"}},
        {"elapsed_s": 5.5, "trace": {"request_id": "orphan"}, "data": {"event_type": "guidance_received", "call_id": "orphan", "latency_s": 99}},
        {"elapsed_s": 6, "data": {"event_type": "collision"}},
        {"elapsed_s": 6.1, "data": {"event_type": "collision_prediction_created"}},
        {"elapsed_s": 7, "data": {"event_type": "boundary_breach"}},
        {"elapsed_s": 10, "data": {"event_type": "session_ended"}},
    ]
    _write_jsonl(events_path, events)

    result = build_session_result(session)

    assert result["physical_safety"]["collision_count"] == 2
    assert result["physical_safety"]["obstacle_collision_count"] == 1
    assert result["physical_safety"]["boundary_breach_count"] == 1
    assert result["llm_efficiency"]["call_count"] == 2
    assert result["source_counts"]["call_completed_n"] == 1
    assert result["source_counts"]["call_cancelled_n"] == 1
    assert result["llm_efficiency"]["latency_mean_s"] == 2


def test_incomplete_reset_is_not_counted(tmp_path: Path) -> None:
    session = _session(tmp_path, "P8_S1", reset_count=1)
    events_path = session / "events.jsonl"
    events = [json.loads(line) for line in events_path.read_text(encoding="utf-8").splitlines()]
    events.append({"elapsed_s": 9, "data": {"event_type": "reset_started", "reset_id": "open"}})
    _write_jsonl(events_path, events)

    result = build_session_result(session)

    assert result["reset"]["reset_count"] == 1
    assert result["source_counts"]["reset_started_n"] == 2
    assert result["source_counts"]["reset_incomplete_n"] == 1
    assert result["reset"]["vdbr_mean"] is None
    assert result["reset"]["tebr_mean"] is None


def test_near_boundary_counts_clearance_reentries_not_logged_labels(tmp_path: Path) -> None:
    session = _session(tmp_path, "P9_S1", reset_count=0)
    _write_jsonl(
        session / "telemetry.jsonl",
        [
            {"elapsed_s": 0, "data": {"minimum_clearance_m": 0.8}},
            {"elapsed_s": 1, "data": {"minimum_clearance_m": 0.49}},
            {"elapsed_s": 2, "data": {"minimum_clearance_m": 0.2}},
            {"elapsed_s": 3, "data": {"minimum_clearance_m": 0.5}},
            {"elapsed_s": 4, "data": {"minimum_clearance_m": 0.1}},
        ],
    )

    result = build_session_result(session)

    assert result["physical_safety"]["near_boundary_threshold_m"] == 0.5
    assert result["physical_safety"]["near_boundary_events"] == 2


def test_candidate_reduction_is_undefined_without_safe_filter_condition(tmp_path: Path) -> None:
    session = _session(tmp_path, "P10_S1", reset_count=0)
    meta_path = session / "session_meta.json"
    meta = json.loads(meta_path.read_text(encoding="utf-8"))
    meta["condition"] = "B1_semantic_only"
    meta_path.write_text(json.dumps(meta), encoding="utf-8")

    result = build_session_result(session)

    assert result["llm_efficiency"]["candidate_reduction_rate"] is None
    assert result["source_counts"]["candidate_reduction_valid_n"] == 0
