"""从 Session 原始 JSONL 重建单次实验结果和跨 Session 汇总。

Unity 日志是唯一事实源。本模块只读取 ``session_meta.json``、``events.jsonl``
和 ``telemetry.jsonl``，因此删除 ``result.json`` 后仍可重复生成相同的结果。
"""

from __future__ import annotations

import argparse
import csv
import json
import math
import statistics
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Iterable


RESULT_HEADERS = [
    "experiment_version", "condition", "participant_count", "session_count",
    "virtual_distance_mean", "virtual_distance_sd", "navigation_time_mean",
    "navigation_time_sd", "reset_count_mean", "reset_rate_per_100m_mean",
    "zero_reset_rate", "vdbr_mean", "vdbr_median", "tebr_mean", "tebr_median",
    "walking_continuity_mean", "walking_continuity_sd", "minimum_clearance_mean",
    "minimum_clearance_sd", "near_boundary_events_mean", "collision_count_mean",
    "blocking_time_mean", "blocking_ratio_mean", "latency_mean", "latency_median",
    "latency_p95", "call_density_per_100m_mean", "candidate_reduction_rate_mean",
]

DEFAULT_DATA_ROOT = Path(r"D:\SoftwareFiles\MyCode\实验数据")

MIN_WALKING_SPEED_MPS = 0.05
CALL_START_EVENTS = {"guidance_requested", "semantic_plan_requested", "planning_route_selection_requested", "llm_call_start"}
CALL_END_EVENTS = {"guidance_received", "semantic_plan_received", "planning_route_selected", "llm_call_end"}


def _read_json(path: Path, default: dict[str, Any] | None = None) -> dict[str, Any]:
    try:
        value = json.loads(path.read_text(encoding="utf-8"))
        return value if isinstance(value, dict) else (default or {})
    except (OSError, ValueError):
        return default or {}


def _read_jsonl(path: Path) -> list[dict[str, Any]]:
    rows: list[dict[str, Any]] = []
    try:
        with path.open(encoding="utf-8") as stream:
            for line in stream:
                try:
                    value = json.loads(line)
                except ValueError:
                    continue
                if isinstance(value, dict):
                    rows.append(value)
    except OSError:
        pass
    return rows


def _payload(row: dict[str, Any]) -> dict[str, Any]:
    data = row.get("data")
    return data if isinstance(data, dict) else row


def _event_type(row: dict[str, Any]) -> str:
    value = row.get("event_type") or _payload(row).get("event_type")
    if value:
        return str(value).lower()
    name = str(row.get("event_name", "")).lower()
    aliases = {
        "session.started": "session_started",
        "session.ended": "session_ended",
        "guidance.requested": "guidance_requested",
        "guidance.received": "guidance_received",
        "guidance.holding_for_replan": "holding_for_replan",
        "guidance.replan_resumed": "replan_hold_resumed",
        "safety.pause_reset_started": "reset_started",
        "safety.pause_reset_completed": "reset_completed",
        "safety.near_boundary_enter": "near_boundary_enter",
        "safety.near_boundary_exit": "near_boundary_exit",
        "safety.collision": "collision",
        "safety.boundary_breach": "boundary_breach",
        "system.semantic.plan.requested": "semantic_plan_requested",
        "system.semantic.plan.received": "semantic_plan_received",
        "rdw.planning.selection_requested": "planning_route_selection_requested",
        "rdw.planning.route_selected": "planning_route_selected",
    }
    if name in aliases:
        return aliases[name]
    return name.rsplit(".", 1)[-1].replace(".", "_")


def _timestamp(row: dict[str, Any], fallback: float = 0.0) -> float:
    for key in ("timestamp_unix_s", "time_s", "elapsed_s"):
        value = row.get(key)
        if isinstance(value, (int, float)):
            return float(value)
    value = row.get("elapsed_ms")
    if isinstance(value, (int, float)):
        return float(value) / 1000.0
    text = row.get("timestamp")
    if isinstance(text, str):
        try:
            normalized = text.replace("Z", "+00:00")
            parsed = datetime.fromisoformat(normalized)
            if parsed.tzinfo is None:
                parsed = parsed.replace(tzinfo=timezone.utc)
            return parsed.timestamp()
        except ValueError:
            pass
    return fallback


def _call_identity(row: dict[str, Any]) -> str:
    payload = _payload(row)
    trace = row.get("trace") if isinstance(row.get("trace"), dict) else {}
    semantic_request = payload.get("semantic_plan_request") if isinstance(payload.get("semantic_plan_request"), dict) else {}
    semantic_response = payload.get("semantic_plan_response") if isinstance(payload.get("semantic_plan_response"), dict) else {}
    return str(
        payload.get("call_id")
        or payload.get("planning_request_id")
        or payload.get("request_id")
        or semantic_request.get("request_id")
        or semantic_response.get("request_id")
        or trace.get("request_id")
        or row.get("request_id")
        or ""
    )


def _number(value: Any) -> float | None:
    if isinstance(value, bool):
        return None
    try:
        number = float(value)
        return number if math.isfinite(number) else None
    except (TypeError, ValueError):
        return None


def _position(row: dict[str, Any], name: str) -> tuple[float, float, float] | None:
    value = row.get(name)
    if value is None:
        value = _payload(row).get(name)
    if value is None:
        value = _payload(row).get(name + "_m")
    if not isinstance(value, dict):
        return None
    x, y, z = _number(value.get("x")), _number(value.get("y")), _number(value.get("z"))
    if x is None:
        return None
    return x, y or 0.0, z if z is not None else (y or 0.0)


def _intervals(events: Iterable[dict[str, Any]], starts: set[str], ends: set[str]) -> list[tuple[float, float]]:
    active: dict[str, list[float]] = {}
    result: list[tuple[float, float]] = []
    for row in sorted(events, key=_timestamp):
        event = _event_type(row)
        time = _timestamp(row)
        payload = _payload(row)
        key = str(payload.get("reset_id") or payload.get("call_id") or payload.get("interval_id") or "default")
        if event in starts:
            active[key] = [time]
        elif event in ends:
            start = active.pop(key, None)
            if start is not None and time >= start[0]:
                result.append((start[0], time))
    return result


def _duration(intervals: Iterable[tuple[float, float]]) -> float:
    # 合并重叠区间，避免一个连续等待被多类事件重复累计。
    merged: list[list[float]] = []
    for start, end in sorted(intervals):
        if not merged or start > merged[-1][1]:
            merged.append([start, end])
        else:
            merged[-1][1] = max(merged[-1][1], end)
    return sum(max(0.0, end - start) for start, end in merged)


def _subtract_intervals(
    intervals: Iterable[tuple[float, float]],
    exclusions: Iterable[tuple[float, float]],
) -> list[tuple[float, float]]:
    """从待统计区间中扣除 Reset、展品停留等不可计入区间。"""
    remaining = list(intervals)
    for excluded_start, excluded_end in sorted(exclusions):
        next_remaining: list[tuple[float, float]] = []
        for start, end in remaining:
            if excluded_end <= start or excluded_start >= end:
                next_remaining.append((start, end))
                continue
            if start < excluded_start:
                next_remaining.append((start, excluded_start))
            if excluded_end < end:
                next_remaining.append((excluded_end, end))
        remaining = next_remaining
    return remaining


def _percentile(values: list[float], percentile: float) -> float | None:
    if not values:
        return None
    values = sorted(values)
    index = (len(values) - 1) * percentile
    lower, upper = math.floor(index), math.ceil(index)
    if lower == upper:
        return values[lower]
    return values[lower] + (values[upper] - values[lower]) * (index - lower)


def _mean(values: list[float]) -> float | None:
    return statistics.fmean(values) if values else None


def _sd(values: list[float]) -> float | None:
    return statistics.stdev(values) if len(values) > 1 else (0.0 if values else None)


def build_session_result(session_dir: str | Path) -> dict[str, Any]:
    """重建一个 Session 的可计算指标并写入 ``result.json``。"""
    directory = Path(session_dir)
    meta = _read_json(directory / "session_meta.json")
    events = _read_jsonl(directory / "events.jsonl")
    telemetry = sorted(_read_jsonl(directory / "telemetry.jsonl"), key=_timestamp)

    positions = [_position(row, "virtual_position") for row in telemetry]
    positions = [item for item in positions if item is not None]
    distance = 0.0
    for previous, current in zip(positions, positions[1:]):
        distance += math.dist(previous, current)
    explicit_distances = [_number(row.get("virtual_distance_m", _payload(row).get("virtual_distance_m"))) for row in telemetry]
    explicit_distances = [value for value in explicit_distances if value is not None]
    if explicit_distances:
        distance = max(explicit_distances)

    event_times = [_timestamp(row) for row in events if row.get("timestamp") or row.get("elapsed_ms") is not None]
    telemetry_times = [_timestamp(row) for row in telemetry]
    all_times = [*event_times, *telemetry_times]
    elapsed = max(all_times) - min(all_times) if len(all_times) > 1 else 0.0

    active_intervals = _intervals(events, {"active_walking_start", "active_walking", "active_walking_enter"}, {"active_walking_end", "active_walking_exit"})
    walking_time = _duration(active_intervals)
    if not walking_time:
        for current, following in zip(telemetry, telemetry[1:]):
            current_payload = _payload(current)
            state = str(current.get("state", current_payload.get("state", current_payload.get("runtime_phase", "")))).lower()
            duration = max(0.0, _timestamp(following) - _timestamp(current))
            current_position = _position(current, "virtual_position")
            following_position = _position(following, "virtual_position")
            moved = (
                duration > 0
                and current_position is not None
                and following_position is not None
                and math.dist(current_position, following_position) / duration >= MIN_WALKING_SPEED_MPS
            )
            if state in {"active_walking", "walking", "running"} and moved:
                walking_time += duration

    reset_starts = [row for row in events if _event_type(row) in {"reset_started", "reset_start"}]
    reset_count = len({str(_payload(row).get("reset_id") or index) for index, row in enumerate(reset_starts)})
    reset_intervals = _intervals(events, {"reset_started", "reset_start"}, {"reset_completed", "reset_end"})
    waiting = _intervals(events, {"planner_waiting_start", "planning_waiting_start", "holding_for_replan"}, {"planner_waiting_end", "planning_waiting_end", "replan_hold_resumed"})
    dwell_intervals = _intervals(events, {"exhibit_dwell_start", "exhibit_dwell"}, {"exhibit_dwell_end", "exhibit_dwell_exit"})
    has_explicit_waiting = any(
        _event_type(row) in {"planner_waiting_start", "planning_waiting_start"}
        for row in events
    )

    latencies: list[float] = []
    call_intervals: list[tuple[float, float]] = []
    calls: dict[str, float] = {}
    for index, row in enumerate(sorted(events, key=_timestamp)):
        event, payload = _event_type(row), _payload(row)
        request_id = _call_identity(row) or f"anonymous-{index}"
        if event in CALL_START_EVENTS:
            calls[request_id] = _timestamp(row)
        elif event in CALL_END_EVENTS:
            started = calls.pop(request_id, None)
            latency = _number(payload.get("latency_s") or row.get("latency_s"))
            if latency is None and started is not None:
                latency = _timestamp(row) - started
            if latency is not None and latency >= 0:
                latencies.append(latency)
            if started is not None and _timestamp(row) >= started:
                call_intervals.append((started, _timestamp(row)))
    blocking_sources = waiting if has_explicit_waiting else call_intervals
    blocking_time = _duration(_subtract_intervals(blocking_sources, [*reset_intervals, *dwell_intervals]))
    call_count = sum(_event_type(row) in CALL_START_EVENTS for row in events)

    clearances: list[float | None] = []
    for row in telemetry:
        payload = _payload(row)
        heading = payload.get("current_heading") if isinstance(payload.get("current_heading"), dict) else {}
        clearances.append(_number(payload.get("clearance_m", heading.get("clearance_m", row.get("clearance_m")))))
    clearances = [value for value in clearances if value is not None]
    near_boundary = sum(_event_type(row) in {"near_boundary_enter", "near_boundary"} for row in events)
    collisions = sum(_event_type(row) == "collision" for row in events)

    reset_points: list[tuple[float, float]] = []
    for reset_row in sorted(reset_starts, key=_timestamp):
        reset_time = _timestamp(reset_row)
        payload = _payload(reset_row)
        metric_snapshot = payload.get("metric_snapshot") if isinstance(payload.get("metric_snapshot"), dict) else {}
        reset_distance = _number(metric_snapshot.get("virtual_distance_m"))
        if reset_distance is None:
            samples_before = [row for row in telemetry if _timestamp(row) <= reset_time]
            sample = samples_before[-1] if samples_before else None
            reset_distance = _number(_payload(sample).get("virtual_distance_m")) if sample else None
        if reset_distance is not None:
            reset_points.append((reset_time, reset_distance))
    tebr_values = [current[0] - previous[0] for previous, current in zip(reset_points, reset_points[1:])]
    vdbr_values = [current[1] - previous[1] for previous, current in zip(reset_points, reset_points[1:])]

    reductions: list[float] = []
    for row in events:
        payload = _payload(row)
        before = _number(payload.get("k_before"))
        after = _number(payload.get("k_after"))
        if before is None:
            before = _number(payload.get("candidate_count_before"))
        if after is None:
            after = _number(payload.get("candidate_count_after"))
        request = payload.get("request") if isinstance(payload.get("request"), dict) else {}
        if before is None and isinstance(request.get("candidate_ids"), list):
            before = float(len(request["candidate_ids"]))
        if after is None and isinstance(request.get("model_candidate_ids"), list):
            after = float(len(request["model_candidate_ids"]))
        if before and after is not None and before > 0:
            reductions.append(1.0 - after / before)

    navigation_time = max(0.0, elapsed - _duration(dwell_intervals))
    result: dict[str, Any] = {
        "schema_version": "session-result-v1",
        "session_id": meta.get("session_id", directory.name),
        "participant_id": meta.get("participant_id", ""),
        "condition": meta.get("condition", ""),
        "route_id": meta.get("route_id", ""),
        "navigation": {
            "virtual_distance_m": distance,
            "navigation_time_s": navigation_time,
            "active_walking_time_s": walking_time,
            "walking_continuity": walking_time / navigation_time if navigation_time > 0 else None,
        },
        "reset": {
            "reset_count": reset_count,
            "reset_rate_per_100m": 100.0 * reset_count / distance if distance > 0 else None,
            "zero_reset": reset_count == 0,
            "reset_duration_s": _duration(reset_intervals),
            "vdbr_mean": _mean(vdbr_values), "vdbr_median": statistics.median(vdbr_values) if vdbr_values else None,
            "vdbr_valid_n": len(vdbr_values), "vdbr_total_n": max(0, reset_count - 1),
            "tebr_mean": _mean(tebr_values), "tebr_median": statistics.median(tebr_values) if tebr_values else None,
            "tebr_valid_n": len(tebr_values), "tebr_total_n": max(0, reset_count - 1),
        },
        "physical_safety": {
            "minimum_clearance_m": min(clearances) if clearances else None,
            "near_boundary_events": near_boundary,
            "collision_count": collisions,
            "boundary_breach_count": sum(_event_type(row) == "boundary_breach" for row in events),
        },
        "llm_efficiency": {
            "call_count": call_count,
            "call_density_per_100m": 100.0 * call_count / distance if distance > 0 else None,
            "blocking_time_s": blocking_time,
            "blocking_ratio": blocking_time / navigation_time if navigation_time > 0 else None,
            "latency_mean_s": _mean(latencies),
            "latency_median_s": statistics.median(latencies) if latencies else None,
            "latency_p95_s": _percentile(latencies, 0.95),
            "candidate_reduction_rate": _mean(reductions),
        },
        "source_counts": {
            "events": len(events),
            "telemetry": len(telemetry),
            "latency_valid_n": len(latencies),
            "candidate_reduction_valid_n": len(reductions),
            "planner_waiting_valid_n": len(waiting) if has_explicit_waiting else len(call_intervals),
        },
        "definitions": {
            "vdbr": "相邻两次 reset_start 时累计虚拟距离之差，单位：米",
            "tebr": "相邻两次 reset_start 的时间差，单位：秒",
        },
    }
    (directory / "result.json").write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8")
    return result


def write_results_csv(session_dirs: Iterable[str | Path], output_path: str | Path) -> None:
    """将多个 Session 结果汇总为文档约定的 results.csv。"""
    output = Path(output_path)
    output.parent.mkdir(parents=True, exist_ok=True)
    results = [build_session_result(path) for path in session_dirs]
    if not results:
        output.write_text(",".join(RESULT_HEADERS) + "\n", encoding="utf-8")
        return

    def values(group: list[dict[str, Any]], key: str) -> list[float]:
        found: list[float] = []
        for item in group:
            value = item
            for part in key.split("."):
                value = value.get(part) if isinstance(value, dict) else None
            if isinstance(value, (int, float)):
                found.append(float(value))
        return found

    def aggregate(group: list[dict[str, Any]], key: str, deviation: bool = False) -> float | None:
        data = values(group, key)
        return _sd(data) if deviation else _mean(data)

    groups: dict[str, list[dict[str, Any]]] = {}
    for result in results:
        groups.setdefault(str(result.get("condition", "")), []).append(result)

    with output.open("w", encoding="utf-8", newline="") as stream:
        writer = csv.DictWriter(stream, fieldnames=RESULT_HEADERS)
        writer.writeheader()
        for condition, group in sorted(groups.items()):
            row = {
                "experiment_version": "session-result-v1", "condition": condition,
                "participant_count": len({str(item.get("participant_id", "")) for item in group}), "session_count": len(group),
                "virtual_distance_mean": aggregate(group, "navigation.virtual_distance_m"), "virtual_distance_sd": aggregate(group, "navigation.virtual_distance_m", True),
                "navigation_time_mean": aggregate(group, "navigation.navigation_time_s"), "navigation_time_sd": aggregate(group, "navigation.navigation_time_s", True),
                "reset_count_mean": aggregate(group, "reset.reset_count"), "reset_rate_per_100m_mean": aggregate(group, "reset.reset_rate_per_100m"),
                "zero_reset_rate": sum(item["reset"]["zero_reset"] for item in group) / len(group),
                "vdbr_mean": aggregate(group, "reset.vdbr_mean"), "vdbr_median": statistics.median(values(group, "reset.vdbr_median")) if values(group, "reset.vdbr_median") else None,
                "tebr_mean": aggregate(group, "reset.tebr_mean"), "tebr_median": statistics.median(values(group, "reset.tebr_median")) if values(group, "reset.tebr_median") else None,
                "walking_continuity_mean": aggregate(group, "navigation.walking_continuity"), "walking_continuity_sd": aggregate(group, "navigation.walking_continuity", True),
                "minimum_clearance_mean": aggregate(group, "physical_safety.minimum_clearance_m"), "minimum_clearance_sd": aggregate(group, "physical_safety.minimum_clearance_m", True),
                "near_boundary_events_mean": aggregate(group, "physical_safety.near_boundary_events"), "collision_count_mean": aggregate(group, "physical_safety.collision_count"),
                "blocking_time_mean": aggregate(group, "llm_efficiency.blocking_time_s"), "blocking_ratio_mean": aggregate(group, "llm_efficiency.blocking_ratio"),
                "latency_mean": aggregate(group, "llm_efficiency.latency_mean_s"), "latency_median": statistics.median(values(group, "llm_efficiency.latency_median_s")) if values(group, "llm_efficiency.latency_median_s") else None,
                "latency_p95": aggregate(group, "llm_efficiency.latency_p95_s"), "call_density_per_100m_mean": aggregate(group, "llm_efficiency.call_density_per_100m"),
                "candidate_reduction_rate_mean": aggregate(group, "llm_efficiency.candidate_reduction_rate"),
            }
            writer.writerow({key: "" if value is None else value for key, value in row.items()})


def main() -> None:
    """提供可用于实验结束后处理和全部实验汇总的命令行入口。"""
    parser = argparse.ArgumentParser(description="重建 VirtualGuidance Session 实验指标")
    parser.add_argument("session_dirs", nargs="*", help="一个或多个 Sessions/<session_id> 目录；省略时自动扫描数据根目录")
    parser.add_argument("--data-root", default=str(DEFAULT_DATA_ROOT), help="实验数据根目录")
    parser.add_argument("--csv", dest="csv_path", help="跨 Session results.csv 输出路径")
    arguments = parser.parse_args()

    explicit_session_dirs = bool(arguments.session_dirs)
    if explicit_session_dirs:
        session_dirs = [Path(path) for path in arguments.session_dirs]
    else:
        sessions_root = Path(arguments.data_root) / "Sessions"
        session_dirs = sorted(path for path in sessions_root.iterdir() if path.is_dir()) if sessions_root.exists() else []

    csv_path = arguments.csv_path
    if not explicit_session_dirs and csv_path is None:
        csv_path = str(Path(arguments.data_root) / "ExperimentResults" / "results.csv")
    if csv_path:
        write_results_csv(session_dirs, csv_path)
    else:
        for session_dir in session_dirs:
            build_session_result(session_dir)


if __name__ == "__main__":
    main()
