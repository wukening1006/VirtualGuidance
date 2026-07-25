"""测试 RDWStateAgent 对边界、障碍物和 reset 风险的确定性计算。"""

from pathlib import Path

from backend.agents.rdw_state_agent import RDWStateAgent


CONFIG_DIR = Path(__file__).resolve().parents[2] / "Assets" / "Configs"


def make_agent() -> RDWStateAgent:
    return RDWStateAgent.from_config_dir(CONFIG_DIR)


def test_narrow_room_forward_is_unsafe_at_demo_pose() -> None:
    state = make_agent().evaluate("narrow_room", (1.0, 4.3), (0.0, 1.0))

    assert state.clearance_forward == 0.7
    assert "forward" in state.unsafe_directions
    assert state.boundary_risk == "high"


def test_narrow_room_left_safe_pose() -> None:
    state = make_agent().evaluate("narrow_room", (1.8, 4.3), (0.0, 1.0))

    assert state.clearance_left == 1.8
    assert "left" in state.safe_directions
    assert "forward" in state.unsafe_directions
    assert "right" in state.unsafe_directions


def test_obstacle_room_obstacle_ahead_marks_forward_unsafe() -> None:
    state = make_agent().evaluate("obstacle_room", (2.0, 1.2), (0.0, 1.0))

    assert state.clearance_forward == 0.55
    assert "forward" in state.unsafe_directions
    assert "left" in state.safe_directions
    assert "right" in state.safe_directions


def test_obstacle_room_obstacle_left_marks_left_unsafe() -> None:
    state = make_agent().evaluate("obstacle_room", (3.0, 2.2), (0.0, 1.0))

    assert state.clearance_left == 0.55
    assert "left" in state.unsafe_directions


def test_l_shaped_notch_outside_requires_reset() -> None:
    state = make_agent().evaluate("l_shaped_room", (3.0, 3.0), (0.0, 1.0))

    assert state.boundary_risk == "critical"
    assert state.reset_risk == "critical"
    assert state.reset_required is True
    assert state.unsafe_directions == ["forward", "left", "right"]
