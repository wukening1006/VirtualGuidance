#!/usr/bin/env python3
"""IEEE VR 用户实验参与者分配器。

设计原则
--------
1. C1--C4 为被试内因素，采用四阶 Williams balanced Latin square，
   平衡条件出现位置以及一阶顺序/携带效应。
2. L1--L4 为被试间物理布局，参与者只进入一种布局。
3. R1--R4 使用循环 Latin square，降低路线学习和疲劳效应。
4. 固定主分配表由无结果信息的约束分配生成；对于最终 20--40 名
   有效参与者的任意前缀，布局、Williams 序列和路线序列的人数差
   均不超过 1。不要在正式收数开始后修改主分配表。

使用示例
--------
python vr_participant_assignment.py 17
python vr_participant_assignment.py P017 --json
python vr_participant_assignment.py --audit --total 32
python vr_participant_assignment.py --export assignments_1_40.csv --total 40

正式参与者编号必须按开始第一轮正式实验的先后连续分配。Pilot 使用
独立编号，不占用这里的 P001、P002……。仅预约但未开始实验的人不占号；
一旦开始正式实验，即使中途退出也保留原编号和原分配，不根据结果换组。
"""

from __future__ import annotations

import argparse
import csv
import json
import re
from collections import Counter
from dataclasses import asdict, dataclass
from pathlib import Path
from typing import Iterable, Sequence


CONDITIONS = {
    "C1": "LLM + Walk + Reset",
    "C2": "LLM + Conventional RDW",
    "C3": "Ours-Plan",
    "C4": "Ours-Full",
}

# 经典四阶 Williams balanced Latin square，与实验手册一致。
CONDITION_SEQUENCES = {
    "A": ("C1", "C2", "C4", "C3"),
    "B": ("C2", "C3", "C1", "C4"),
    "C": ("C3", "C4", "C2", "C1"),
    "D": ("C4", "C1", "C3", "C2"),
}

# 四条难度匹配的共享语义路线池；这里只改变路线的呈现顺序。
ROUTE_SEQUENCES = {
    "RO1": ("R1", "R2", "R3", "R4"),
    "RO2": ("R2", "R3", "R4", "R1"),
    "RO3": ("R3", "R4", "R1", "R2"),
    "RO4": ("R4", "R1", "R2", "R3"),
}

LAYOUTS = {
    "L1": {
        "name": "Standard",
        "description": "5 m × 5 m 标准方形，无内部障碍物",
    },
    "L2": {
        "name": "Narrow",
        "description": "细长物理空间（按正式冻结的 L2 配置执行）",
    },
    "L3": {
        "name": "Obstacle",
        "description": "5 m × 5 m，含中央软包实体障碍物",
    },
    "L4": {
        "name": "Irregular-L",
        "description": "不规则 L 形物理房间（按正式冻结的 L4 配置执行）",
    },
}


# (layout_id, Williams sequence, route-order sequence)
#
# 64 个唯一组合构成一个完整块。P001--P004 覆盖 L1--L4，后续布局持续
# 交错分配。前 40 个位置经过固定的约束分配，保证
# N=20...40 的任意停止点上，布局、Williams
# 序列、路线序列，以及 layout × Williams、layout × route-order 单元格
# 的最大计数差均不超过 1。
# 该表在任何参与者数据产生前固定，不使用实验结果进行自适应换组。
MASTER_SCHEDULE: tuple[tuple[str, str, str], ...] = (
    ("L2", "B", "RO1"),
    ("L3", "D", "RO3"),
    ("L4", "C", "RO4"),
    ("L1", "A", "RO2"),
    ("L3", "A", "RO4"),
    ("L2", "C", "RO2"),
    ("L4", "B", "RO3"),
    ("L1", "D", "RO1"),
    ("L3", "B", "RO2"),
    ("L2", "D", "RO4"),
    ("L3", "C", "RO1"),
    ("L2", "A", "RO3"),
    ("L4", "D", "RO2"),
    ("L1", "B", "RO4"),
    ("L4", "A", "RO1"),
    ("L1", "C", "RO3"),
    ("L4", "A", "RO4"),
    ("L1", "C", "RO2"),
    ("L2", "D", "RO1"),
    ("L3", "B", "RO3"),
    ("L4", "D", "RO3"),
    ("L1", "B", "RO1"),
    ("L3", "C", "RO4"),
    ("L2", "A", "RO2"),
    ("L4", "C", "RO1"),
    ("L1", "A", "RO3"),
    ("L3", "D", "RO2"),
    ("L2", "B", "RO4"),
    ("L2", "C", "RO3"),
    ("L1", "D", "RO4"),
    ("L3", "A", "RO1"),
    ("L4", "B", "RO2"),
    ("L3", "C", "RO2"),
    ("L2", "A", "RO4"),
    ("L4", "B", "RO1"),
    ("L1", "D", "RO3"),
    ("L4", "A", "RO2"),
    ("L3", "D", "RO1"),
    ("L2", "B", "RO3"),
    ("L1", "C", "RO4"),
    ("L3", "B", "RO4"),
    ("L4", "C", "RO3"),
    ("L2", "D", "RO2"),
    ("L1", "A", "RO1"),
    ("L3", "A", "RO3"),
    ("L4", "D", "RO4"),
    ("L2", "C", "RO1"),
    ("L1", "B", "RO2"),
    ("L2", "A", "RO1"),
    ("L3", "A", "RO2"),
    ("L4", "A", "RO3"),
    ("L1", "A", "RO4"),
    ("L3", "B", "RO1"),
    ("L2", "B", "RO2"),
    ("L1", "B", "RO3"),
    ("L4", "B", "RO4"),
    ("L1", "C", "RO1"),
    ("L4", "C", "RO2"),
    ("L3", "C", "RO3"),
    ("L2", "C", "RO4"),
    ("L4", "D", "RO1"),
    ("L1", "D", "RO2"),
    ("L2", "D", "RO3"),
    ("L3", "D", "RO4"),)


@dataclass(frozen=True)
class Trial:
    trial_no: int
    condition_id: str
    condition_name: str
    route_id: str


@dataclass(frozen=True)
class Assignment:
    participant_id: str
    participant_no: int
    layout_id: str
    layout_name: str
    layout_description: str
    williams_sequence: str
    route_sequence: str
    trials: tuple[Trial, ...]


def parse_participant_number(value: str | int) -> int:
    """接受 17、'17'、'P017' 等输入。"""
    if isinstance(value, int):
        number = value
    else:
        match = re.fullmatch(r"\s*[Pp]?(\d+)\s*", value)
        if not match:
            raise ValueError("参与者编号应为正整数，例如 17 或 P017。")
        number = int(match.group(1))
    if not 1 <= number <= len(MASTER_SCHEDULE):
        raise ValueError(
            f"当前固定主表支持 P001--P{len(MASTER_SCHEDULE):03d}；"
            "本研究建议最终样本量为 20--40。"
        )
    return number


def assignment_for(value: str | int) -> Assignment:
    number = parse_participant_number(value)
    layout_id, sequence_id, route_sequence_id = MASTER_SCHEDULE[number - 1]
    condition_order = CONDITION_SEQUENCES[sequence_id]
    route_order = ROUTE_SEQUENCES[route_sequence_id]
    trials = tuple(
        Trial(
            trial_no=index,
            condition_id=condition_id,
            condition_name=CONDITIONS[condition_id],
            route_id=route_id,
        )
        for index, (condition_id, route_id) in enumerate(
            zip(condition_order, route_order), start=1
        )
    )
    layout = LAYOUTS[layout_id]
    return Assignment(
        participant_id=f"P{number:03d}",
        participant_no=number,
        layout_id=layout_id,
        layout_name=layout["name"],
        layout_description=layout["description"],
        williams_sequence=sequence_id,
        route_sequence=route_sequence_id,
        trials=trials,
    )


def _spread(counter: Counter, keys: Iterable) -> int:
    values = [counter[key] for key in keys]
    return max(values) - min(values)


def audit(total: int) -> dict:
    """返回从 P001 到 P{total} 的分配平衡摘要。"""
    if not 1 <= total <= len(MASTER_SCHEDULE):
        raise ValueError(f"total 必须在 1--{len(MASTER_SCHEDULE)} 之间。")

    assignments = [assignment_for(i) for i in range(1, total + 1)]
    layout_counts = Counter(a.layout_id for a in assignments)
    sequence_counts = Counter(a.williams_sequence for a in assignments)
    route_order_counts = Counter(a.route_sequence for a in assignments)
    layout_sequence_counts = Counter(
        (a.layout_id, a.williams_sequence) for a in assignments
    )
    layout_route_counts = Counter(
        (a.layout_id, a.route_sequence) for a in assignments
    )
    condition_route_counts = Counter(
        (trial.condition_id, trial.route_id)
        for assignment in assignments
        for trial in assignment.trials
    )

    return {
        "total": total,
        "layout_counts": dict(sorted(layout_counts.items())),
        "williams_sequence_counts": dict(sorted(sequence_counts.items())),
        "route_sequence_counts": dict(sorted(route_order_counts.items())),
        "max_min_differences": {
            "layout": _spread(layout_counts, LAYOUTS),
            "williams_sequence": _spread(sequence_counts, CONDITION_SEQUENCES),
            "route_sequence": _spread(route_order_counts, ROUTE_SEQUENCES),
            "layout_x_williams": _spread(
                layout_sequence_counts,
                (
                    (layout, sequence)
                    for layout in LAYOUTS
                    for sequence in CONDITION_SEQUENCES
                ),
            ),
            "layout_x_route_sequence": _spread(
                layout_route_counts,
                (
                    (layout, route_order)
                    for layout in LAYOUTS
                    for route_order in ROUTE_SEQUENCES
                ),
            ),
            "condition_x_route": _spread(
                condition_route_counts,
                (
                    (condition, route)
                    for condition in CONDITIONS
                    for route in ("R1", "R2", "R3", "R4")
                ),
            ),
        },
    }


def format_assignment(assignment: Assignment) -> str:
    lines = [
        f"参与者：{assignment.participant_id}",
        (
            f"物理布局：{assignment.layout_id} {assignment.layout_name} | "
            f"{assignment.layout_description}"
        ),
        f"Williams 条件序列：{assignment.williams_sequence}",
        f"路线顺序：{assignment.route_sequence}",
        "",
        "正式实验顺序：",
    ]
    for trial in assignment.trials:
        lines.append(
            f"  第 {trial.trial_no} 轮：{trial.condition_id} "
            f"({trial.condition_name}) + {trial.route_id}"
        )
    lines.extend(
        [
            "",
            "执行提醒：每轮开始前恢复该布局统一 physical start pose；",
            "条件结束后立即填写 Post-SSQ、IPQ、NASA Raw-TLX 和补充量表。",
        ]
    )
    return "\n".join(lines)


def format_audit(report: dict) -> str:
    differences = report["max_min_differences"]
    lines = [
        f"P001--P{report['total']:03d} 分配平衡检查",
        f"布局人数：{report['layout_counts']}",
        f"Williams 序列人数：{report['williams_sequence_counts']}",
        f"路线序列人数：{report['route_sequence_counts']}",
        "最大计数差（越小越好）：",
    ]
    lines.extend(f"  {key}: {value}" for key, value in differences.items())
    lines.append(
        "说明：当总人数不能被 4、4 或 16 整除时，所有单元格数学上不可能完全相等。"
    )
    return "\n".join(lines)


def export_csv(total: int, destination: Path) -> None:
    destination.parent.mkdir(parents=True, exist_ok=True)
    fieldnames = [
        "participant_id",
        "layout_id",
        "layout_name",
        "williams_sequence",
        "route_sequence",
        "trial_no",
        "condition_id",
        "condition_name",
        "route_id",
    ]
    with destination.open("w", encoding="utf-8-sig", newline="") as handle:
        writer = csv.DictWriter(handle, fieldnames=fieldnames)
        writer.writeheader()
        for number in range(1, total + 1):
            assignment = assignment_for(number)
            for trial in assignment.trials:
                writer.writerow(
                    {
                        "participant_id": assignment.participant_id,
                        "layout_id": assignment.layout_id,
                        "layout_name": assignment.layout_name,
                        "williams_sequence": assignment.williams_sequence,
                        "route_sequence": assignment.route_sequence,
                        "trial_no": trial.trial_no,
                        "condition_id": trial.condition_id,
                        "condition_name": trial.condition_name,
                        "route_id": trial.route_id,
                    }
                )


def _json_default(value):
    if isinstance(value, tuple):
        return list(value)
    raise TypeError(f"不能序列化 {type(value).__name__}")


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        description="为 LLM × RDW IEEE VR 实验分配布局、Williams 条件顺序和路线顺序。"
    )
    parser.add_argument(
        "participant_id",
        nargs="?",
        help="正式参与者编号，例如 17 或 P017；省略时进入交互输入。",
    )
    parser.add_argument(
        "--total",
        type=int,
        default=40,
        help="用于 --audit/--export 的计划人数，默认 40，支持 1--64。",
    )
    parser.add_argument(
        "--audit",
        action="store_true",
        help="检查 P001 到 P<total> 的分配平衡。",
    )
    parser.add_argument(
        "--export",
        type=Path,
        metavar="CSV_PATH",
        help="将 P001 到 P<total> 的逐轮分配导出为 CSV。",
    )
    parser.add_argument(
        "--json",
        action="store_true",
        help="以 JSON 输出单个参与者的分配。",
    )
    return parser


def self_check() -> None:
    assert len(MASTER_SCHEDULE) == 64
    assert len(set(MASTER_SCHEDULE)) == 64
    # 首批参与者必须覆盖全部布局，避免 L4 被推迟到后半程。
    assert {layout for layout, _, _ in MASTER_SCHEDULE[:4]} == set(LAYOUTS)
    assert all(set(order) == set(CONDITIONS) for order in CONDITION_SEQUENCES.values())
    assert all(
        set(order) == {"R1", "R2", "R3", "R4"}
        for order in ROUTE_SEQUENCES.values()
    )
    for total in range(20, 41):
        differences = audit(total)["max_min_differences"]
        assert differences["layout"] <= 1
        assert differences["williams_sequence"] <= 1
        assert differences["route_sequence"] <= 1
        assert differences["layout_x_williams"] <= 1
        assert differences["layout_x_route_sequence"] <= 1
        assert differences["condition_x_route"] <= 2


def main(argv: Sequence[str] | None = None) -> int:
    self_check()
    parser = build_parser()
    args = parser.parse_args(argv)

    if args.audit:
        print(format_audit(audit(args.total)))

    if args.export:
        export_csv(args.total, args.export)
        print(f"已导出：{args.export.resolve()}")
        if not args.audit:
            print(format_audit(audit(args.total)))

    if args.audit or args.export:
        if args.participant_id is None:
            return 0

    participant_id = args.participant_id
    if participant_id is None:
        participant_id = input("请输入正式参与者编号（例如 17 或 P017）：").strip()

    assignment = assignment_for(participant_id)
    if args.json:
        print(json.dumps(asdict(assignment), ensure_ascii=False, indent=2, default=_json_default))
    else:
        print(format_assignment(assignment))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
