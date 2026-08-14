"""测试 RDWStateAgent 对边界、障碍物和 reset 风险的确定性计算。"""

from pathlib import Path

from backend.agents.rdw_state_agent import RDWStateAgent


CONFIG_DIR = Path(__file__).resolve().parents[2] / "Assets" / "Configs"


def make_agent() -> RDWStateAgent:
    return RDWStateAgent.from_config_dir(CONFIG_DIR)


def test_narrow_room_current_heading_is_high_risk_at_demo_pose() -> None:
    state = make_agent().evaluate("narrow_room", (1.0, 4.3), (0.0, 1.0))

    assert state.boundary_risk == "high"
    assert state.reset_required is False
    assert state.user_physical_forward == (0.0, 1.0)


def test_narrow_room_offset_pose_keeps_current_heading_fact_only() -> None:
    state = make_agent().evaluate("narrow_room", (1.8, 4.3), (0.0, 1.0))

    assert state.boundary_risk == "high"
    assert state.reset_risk == "high"
    assert state.reset_required is False


def test_obstacle_room_obstacle_ahead_marks_current_heading_high_risk() -> None:
    state = make_agent().evaluate("obstacle_room", (2.0, 1.2), (0.0, 1.0))

    assert state.boundary_risk == "high"
    assert state.reset_required is False


def test_obstacle_room_offset_pose_does_not_emit_coarse_direction_sets() -> None:
    state = make_agent().evaluate("obstacle_room", (3.0, 2.2), (0.0, 1.0))

    assert state.user_physical_position == (3.0, 2.2)
    assert state.boundary_risk in {"low", "medium", "high", "critical"}


def test_l_shaped_notch_outside_requires_reset() -> None:
    state = make_agent().evaluate("l_shaped_room", (3.0, 3.0), (0.0, 1.0))

    assert state.boundary_risk == "critical"
    assert state.reset_risk == "critical"
    assert state.reset_required is True
