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
        json.dumps({"session_id": name, "participant_id": name[:2], "condition": "ours", "route_id": "E1>E5"}),
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
            {"elapsed_s": at + 1, "data": {"event_type": "reset_completed", "reset_id": f"r{index}"}},
        ])
    events.append({"elapsed_s": 10, "data": {"event_type": "session_ended"}})
    _write_jsonl(directory / "events.jsonl", events)
    _write_jsonl(
        directory / "telemetry.jsonl",
        [
            {"elapsed_s": 0, "data": {"state": "active_walking", "virtual_position": {"x": 0, "y": 0, "z": 0}, "virtual_distance_m": 0, "clearance_m": 2}},
            {"elapsed_s": 3, "data": {"state": "reset", "virtual_position": {"x": 3, "y": 0, "z": 0}, "virtual_distance_m": 3, "clearance_m": 0.5}},
            {"elapsed_s": 7, "data": {"state": "reset", "virtual_position": {"x": 7, "y": 0, "z": 0}, "virtual_distance_m": 7, "clearance_m": 1}},
            {"elapsed_s": 10, "data": {"state": "ended", "virtual_position": {"x": 10, "y": 0, "z": 0}, "virtual_distance_m": 10, "clearance_m": 1.5}},
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
    assert first["reset"]["vdbr_mean"] == 8
    assert first["reset"]["tebr_mean"] == 4
    assert first["reset"]["vdbr_valid_n"] == 1
    assert first["physical_safety"]["near_boundary_events"] == 1
    assert first["physical_safety"]["collision_count"] == 1
    assert first["llm_efficiency"]["latency_p95_s"] == 1
    assert first["llm_efficiency"]["blocking_time_s"] == 4
    assert first["llm_efficiency"]["blocking_ratio"] == pytest.approx(0.5)
    assert first["source_counts"]["planner_waiting_valid_n"] == 2
    assert first["llm_efficiency"]["candidate_reduction_rate"] == pytest.approx(0.6)


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

    assert [row["condition"] for row in rows] == ["baseline", "ours"]
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

    assert result["reset"]["reset_count"] == 1
    assert result["reset"]["reset_duration_s"] == 3
