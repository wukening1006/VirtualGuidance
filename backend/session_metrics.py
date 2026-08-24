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
DEFAULT_NEAR_BOUNDARY_THRESHOLD_M = 0.5
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
        "task.completed": "task_completed",
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
    # Session 内所有记录使用单调 elapsed_s；墙上时间只用于旧日志回退。
    for key in ("elapsed_s", "time_s", "timestamp_unix_s"):
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


def _metric_value(row: dict[str, Any], *keys: str) -> float | None:
    payload = _payload(row)
    for key in keys:
        value = row.get(key)
        if value is None:
            value = payload.get(key)
        number = _number(value)
        if number is not None:
            return number
    return None


def _clip_intervals(
    intervals: Iterable[tuple[float, float]],
    start: float,
    end: float,
) -> list[tuple[float, float]]:
    clipped: list[tuple[float, float]] = []
    for interval_start, interval_end in intervals:
        left, right = max(start, interval_start), min(end, interval_end)
        if right > left:
            clipped.append((left, right))
    return clipped


def _session_bounds(
    events: Iterable[dict[str, Any]],
    telemetry: Iterable[dict[str, Any]],
) -> tuple[float, float]:
    ordered_events = sorted(events, key=_timestamp)
    started = next(
        (_timestamp(row) for row in ordered_events if _event_type(row) == "session_started"),
        None,
    )
    ended = next(
        (_timestamp(row) for row in reversed(ordered_events) if _event_type(row) == "session_ended"),
        None,
    )
    all_times = [_timestamp(row) for row in (*ordered_events, *telemetry)]
    if started is None:
        started = min(all_times) if all_times else 0.0
    if ended is None:
        ended = max(all_times) if all_times else started
    return started, max(started, ended)


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


def _walking_intervals(telemetry: Iterable[dict[str, Any]]) -> list[tuple[float, float]]:
    """把每个 ACTIVE_WALKING 样本还原为以该样本结束的时间区间。"""
    ordered = sorted(telemetry, key=_timestamp)
    result: list[tuple[float, float]] = []
    previous: dict[str, Any] | None = None
    for row in ordered:
        payload = _payload(row)
        state = str(row.get("state", payload.get("state", payload.get("runtime_phase", "")))).lower()
        end = _timestamp(row)
        sample_dt = _metric_value(row, "sample_dt_s")
        if sample_dt is not None and sample_dt > 0:
            start = end - sample_dt
            moved = True
        elif previous is not None:
            start = _timestamp(previous)
            duration = end - start
            previous_position = _position(previous, "virtual_position")
            current_position = _position(row, "virtual_position")
            moved = (
                duration > 0
                and previous_position is not None
                and current_position is not None
                and math.dist(previous_position, current_position) / duration >= MIN_WALKING_SPEED_MPS
            )
        else:
            previous = row
            continue

        if end > start and state in {"active_walking", "walking", "running"} and moved:
            result.append((start, end))
        previous = row
    return result


def _count_threshold_entries(values: Iterable[float | None], threshold: float) -> int:
    """按离散样本的 false -> true 边沿统计进入阈值区间的次数。"""
    inside = False
    entries = 0
    for value in values:
        if value is None:
            continue
        current = value < threshold
        if current and not inside:
            entries += 1
        inside = current
    return entries


def _reset_id(row: dict[str, Any]) -> str:
    return str(_payload(row).get("reset_id") or "").strip()


def _completed_resets(
    events: Iterable[dict[str, Any]],
) -> tuple[list[tuple[dict[str, Any], dict[str, Any]]], int, int, int]:
    """仅返回具有同一 reset_id 的 reset_start -> reset_end 完成配对。"""
    active: dict[str, dict[str, Any]] = {}
    completed: list[tuple[dict[str, Any], dict[str, Any]]] = []
    completed_ids: set[str] = set()
    started_ids: set[str] = set()
    cancelled_ids: set[str] = set()
    for row in sorted(events, key=_timestamp):
        event = _event_type(row)
        reset_id = _reset_id(row)
        if not reset_id:
            continue
        if event in {"reset_started", "reset_start"}:
            started_ids.add(reset_id)
            if reset_id not in completed_ids:
                active.setdefault(reset_id, row)
        elif event in {"reset_completed", "reset_end"}:
            started = active.pop(reset_id, None)
            if started is not None and reset_id not in completed_ids and _timestamp(row) >= _timestamp(started):
                completed.append((started, row))
                completed_ids.add(reset_id)
        elif event == "reset_cancelled":
            if active.pop(reset_id, None) is not None:
                cancelled_ids.add(reset_id)
    completed.sort(key=lambda pair: _timestamp(pair[0]))
    incomplete_count = len(started_ids - completed_ids - cancelled_ids)
    return completed, len(started_ids), len(cancelled_ids), incomplete_count


def _reset_distance(row: dict[str, Any], boundary: str) -> float | None:
    payload = _payload(row)
    explicit_key = "virtual_distance_at_reset_start_m" if boundary == "start" else "virtual_distance_at_reset_end_m"
    value = _number(payload.get(explicit_key))
    snapshot = payload.get("metric_snapshot") if isinstance(payload.get("metric_snapshot"), dict) else {}
    return value if value is not None else _number(snapshot.get("virtual_distance_m"))


def _candidate_reduction_enabled(condition: str) -> bool:
    normalized = condition.strip().lower()
    return normalized == "ours_rdw_grounded_topk" or normalized == "c3" or normalized == "c4" or normalized.startswith(("c3_", "c4_"))


def _experiment_completed(meta: dict[str, Any], events: Iterable[dict[str, Any]]) -> bool:
    """根据任务终态事件或自动完成原因判断实验是否顺利完成。"""
    end_reason = str(meta.get("end_reason", "")).strip().lower()
    return end_reason == "completed_auto" or any(
        _event_type(row) == "task_completed" for row in events
    )


def _visited_exhibit_ids(events: Iterable[dict[str, Any]]) -> list[str]:
    """读取日志中最后一次有效的展品访问顺序快照。"""
    visited: list[str] = []
    for row in sorted(events, key=_timestamp):
        value = _payload(row).get("visited_exhibit_ids")
        if not isinstance(value, list):
            continue
        snapshot = [item.strip() for item in value if isinstance(item, str) and item.strip()]
        if snapshot:
            visited = snapshot
    return visited


def build_session_result(session_dir: str | Path) -> dict[str, Any]:
    """重建一个 Session 的可计算指标并写入 ``result.json``。"""
    directory = Path(session_dir)
    meta = _read_json(directory / "session_meta.json")
    events = _read_jsonl(directory / "events.jsonl")
    telemetry = sorted(_read_jsonl(directory / "telemetry.jsonl"), key=_timestamp)

    session_start, session_end = _session_bounds(events, telemetry)
    positions = [_position(row, "virtual_position") for row in telemetry]
    distance = 0.0
    valid_positions = [item for item in positions if item is not None]
    for previous, current in zip(valid_positions, valid_positions[1:]):
        distance += math.dist(previous, current)
    explicit_distances = [_metric_value(row, "virtual_distance_m") for row in telemetry]
    explicit_distances = [value for value in explicit_distances if value is not None]
    if explicit_distances:
        # Logger 写入的累计距离是事实源，但只接受单调非负值。
        monotonic_distances: list[float] = []
        last_distance = 0.0
        for value in explicit_distances:
            if value >= last_distance:
                monotonic_distances.append(value)
                last_distance = value
        if monotonic_distances:
            distance = monotonic_distances[-1]

    walking_intervals = _walking_intervals(telemetry)
    walking_time = _duration(_clip_intervals(walking_intervals, session_start, session_end))

    completed_resets, reset_started_n, reset_cancelled_n, reset_incomplete_n = _completed_resets(events)
    reset_count = len(completed_resets)
    # Reset 时长保留已完成和已取消区间，但 RC 只统计完成配对。
    reset_intervals = _intervals(
        events,
        {"reset_started", "reset_start"},
        {"reset_completed", "reset_end", "reset_cancelled"},
    )
    # holding_for_replan 是状态通知，不是一个可配对的时间区间。
    waiting = _intervals(
        events,
        {"planner_waiting_start", "planning_waiting_start"},
        {"planner_waiting_end", "planning_waiting_end"},
    )
    dwell_intervals = _intervals(events, {"exhibit_dwell_start", "exhibit_dwell"}, {"exhibit_dwell_end", "exhibit_dwell_exit"})
    has_explicit_waiting = any(
        _event_type(row) in {"planner_waiting_start", "planning_waiting_start"}
        for row in events
    )

    latencies: list[float] = []
    call_intervals: list[tuple[float, float]] = []
    calls: dict[str, float] = {}
    call_started_n = 0
    call_completed_n = 0
    call_cancelled_n = 0
    for index, row in enumerate(sorted(events, key=_timestamp)):
        event, payload = _event_type(row), _payload(row)
        request_id = _call_identity(row)
        if event in CALL_START_EVENTS:
            call_started_n += 1
            if request_id:
                calls[request_id] = _timestamp(row)
        elif event in CALL_END_EVENTS:
            started = calls.pop(request_id, None) if request_id else None
            if started is not None and _timestamp(row) >= started:
                latency = _metric_value(row, "latency_s")
                if latency is None:
                    latency = _timestamp(row) - started
                call_completed_n += 1
                call_intervals.append((started, _timestamp(row)))
                if latency is not None and latency >= 0:
                    latencies.append(latency)
        elif event in {"guidance_cancelled", "guidance_expired", "guidance_failed", "llm_call_cancelled"}:
            if request_id and request_id in calls:
                calls.pop(request_id, None)
                call_cancelled_n += 1
    blocking_sources = waiting if has_explicit_waiting else call_intervals
    blocking_time = _duration(_subtract_intervals(blocking_sources, [*reset_intervals, *dwell_intervals]))
    call_count = call_started_n

    clearance_samples = [_metric_value(row, "minimum_clearance_m", "clearance_m") for row in telemetry]
    clearances = [value for value in clearance_samples if value is not None]
    threshold = _number(meta.get("near_boundary_threshold_m"))
    if threshold is None:
        threshold = next(
            (_metric_value(row, "near_boundary_threshold_m") for row in telemetry if _metric_value(row, "near_boundary_threshold_m") is not None),
            DEFAULT_NEAR_BOUNDARY_THRESHOLD_M,
        )
    threshold = max(0.0, threshold)
    near_boundary = _count_threshold_entries(clearance_samples, threshold)
    obstacle_collisions = sum(_event_type(row) == "collision" for row in events)
    boundary_breaches = sum(_event_type(row) == "boundary_breach" for row in events)
    collisions = obstacle_collisions + boundary_breaches

    vdbr_values: list[float] = []
    tebr_values: list[float] = []
    for previous, current in zip(completed_resets, completed_resets[1:]):
        previous_end = previous[1]
        current_start = current[0]
        gap_start, gap_end = _timestamp(previous_end), _timestamp(current_start)
        if gap_end < gap_start:
            continue
        previous_distance = _reset_distance(previous_end, "end")
        current_distance = _reset_distance(current_start, "start")
        if previous_distance is not None and current_distance is not None and current_distance >= previous_distance:
            vdbr_values.append(current_distance - previous_distance)
        tebr_values.append(_duration(_clip_intervals(walking_intervals, gap_start, gap_end)))

    reductions: list[float] = []
    condition = str(meta.get("condition", ""))
    if _candidate_reduction_enabled(condition):
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
            if before is not None and after is not None and before > 0 and 0 <= after <= before:
                reductions.append(1.0 - after / before)

    navigation_time = max(
        0.0,
        session_end - session_start - _duration(
            _clip_intervals(dwell_intervals, session_start, session_end)
        ),
    )
    result: dict[str, Any] = {
        "schema_version": "session-result-v2",
        "session_id": meta.get("session_id", directory.name),
        "participant_id": meta.get("participant_id", ""),
        "condition": condition,
        "route_id": meta.get("route_id", ""),
        "experiment_completed": _experiment_completed(meta, events),
        "visited_exhibit_ids": _visited_exhibit_ids(events),
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
            "vdbr_samples_m": vdbr_values,
            "tebr_samples_s": tebr_values,
        },
        "physical_safety": {
            "minimum_clearance_m": min(clearances) if clearances else None,
            "near_boundary_threshold_m": threshold,
            "near_boundary_events": near_boundary,
            "collision_count": collisions,
            "obstacle_collision_count": obstacle_collisions,
            "boundary_breach_count": boundary_breaches,
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
            "latency_samples_s": latencies,
            "candidate_reduction_samples": reductions,
        },
        "source_counts": {
            "events": len(events),
            "telemetry": len(telemetry),
            "latency_valid_n": len(latencies),
            "call_started_n": call_started_n,
            "call_completed_n": call_completed_n,
            "call_cancelled_n": call_cancelled_n,
            "reset_started_n": reset_started_n,
            "reset_completed_n": reset_count,
            "reset_cancelled_n": reset_cancelled_n,
            "reset_incomplete_n": reset_incomplete_n,
            "candidate_reduction_valid_n": len(reductions),
            "candidate_reduction_total_n": sum(
                1
                for row in events
                if _number(_payload(row).get("k_before")) is not None
                or _number(_payload(row).get("candidate_count_before")) is not None
            ) if _candidate_reduction_enabled(condition) else 0,
            "planner_waiting_valid_n": len(waiting) if has_explicit_waiting else len(call_intervals),
            "near_boundary_logged_n": sum(_event_type(row) in {"near_boundary_enter", "near_boundary"} for row in events),
        },
        "definitions": {
            "rc": "具有相同 reset_id 的 reset_start -> reset_end 完成配对数；取消和未完成不计",
            "vdbr": "相邻已完成 Reset 的 reset_i_end 到 reset_(i+1)_start 的累计虚拟距离差，单位：米；仅 RC>=2 定义",
            "tebr": "相邻已完成 Reset 的 reset_i_end 到 reset_(i+1)_start 内 ACTIVE_WALKING 区间总时长，单位：秒；仅 RC>=2 定义",
            "near_boundary_events": "minimum_clearance_m 从不小于阈值进入小于阈值的次数",
            "collision_count": "障碍物碰撞进入次数与 boundary breach 进入次数之和；同一持续区间只计一次",
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
                "experiment_version": "session-result-v2", "condition": condition,
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
                "latency_mean": _mean([
                    float(value)
                    for item in group
                    for value in item.get("llm_efficiency", {}).get("latency_samples_s", [])
                ]),
                "latency_median": statistics.median([
                    float(value)
                    for item in group
                    for value in item.get("llm_efficiency", {}).get("latency_samples_s", [])
                ]) if any(item.get("llm_efficiency", {}).get("latency_samples_s", []) for item in group) else None,
                "latency_p95": _percentile([
                    float(value)
                    for item in group
                    for value in item.get("llm_efficiency", {}).get("latency_samples_s", [])
                ], 0.95),
                "call_density_per_100m_mean": aggregate(group, "llm_efficiency.call_density_per_100m"),
                "candidate_reduction_rate_mean": _mean([
                    float(value)
                    for item in group
                    for value in item.get("llm_efficiency", {}).get("candidate_reduction_samples", [])
                ]),
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
