"""RDWStateAgent：根据物理空间边界、障碍物和用户朝向计算确定性风险状态。"""

from __future__ import annotations

import json
import math
from dataclasses import dataclass
from pathlib import Path

from backend.schemas import PhysicalRiskState


EPSILON = 0.0001


@dataclass(frozen=True)
class PhysicalObstacle:
    id: str
    center: tuple[float, float]
    radius: float


@dataclass(frozen=True)
class PhysicalSpace:
    id: str
    boundary: tuple[tuple[float, float], ...]
    obstacles: tuple[PhysicalObstacle, ...]


class RDWStateAgent:
    """确定性计算边界、障碍物和 reset 风险状态。"""

    def __init__(
        self,
        spaces: dict[str, PhysicalSpace],
        max_clearance_meters: float = 3.0,
        unsafe_clearance_meters: float = 0.8,
        safe_clearance_meters: float = 1.2,
        critical_clearance_meters: float = 0.45,
    ) -> None:
        self.spaces = spaces
        self.max_clearance_meters = max_clearance_meters
        self.unsafe_clearance_meters = unsafe_clearance_meters
        self.safe_clearance_meters = safe_clearance_meters
        self.critical_clearance_meters = critical_clearance_meters

    @classmethod
    def from_config_dir(cls, config_dir: Path) -> "RDWStateAgent":
        spaces: dict[str, PhysicalSpace] = {}
        for path in sorted(config_dir.glob("physical_*.json")):
            with path.open("r", encoding="utf-8") as handle:
                payload = json.load(handle)
            space = parse_physical_space(payload)
            spaces[space.id] = space

        if not spaces:
            raise ValueError(f"No physical space configs found in {config_dir}")

        return cls(spaces)

    def evaluate(
        self,
        physical_space_id: str,
        position: tuple[float, float],
        forward: tuple[float, float],
    ) -> PhysicalRiskState:
        if physical_space_id not in self.spaces:
            raise ValueError(f"Unknown physical space id: {physical_space_id}")

        space = self.spaces[physical_space_id]
        forward = normalize(forward)
        if not is_point_inside_polygon(position, space.boundary):
            # 用户已经在 tracking polygon 外时，局部导览不再可靠，因此强制进入 reset 路径。
            return PhysicalRiskState(
                physical_space=space.id,
                user_physical_position=position,
                user_physical_forward=forward,
                boundary_risk="critical",
                reset_risk="critical",
                reset_required=True,
            )

        current_clearance = self._compute_clearance(space, position, forward)
        boundary_risk = self._clearance_to_risk(current_clearance)
        reset_required = current_clearance < self.critical_clearance_meters

        return PhysicalRiskState(
            physical_space=space.id,
            user_physical_position=position,
            user_physical_forward=forward,
            boundary_risk=boundary_risk,
            reset_risk="critical" if reset_required else boundary_risk,
            reset_required=reset_required,
        )

    def _compute_clearance(
        self,
        space: PhysicalSpace,
        origin: tuple[float, float],
        direction: tuple[float, float],
    ) -> float:
        # ray 命中最近的物理约束即停止：房间边界或障碍物。
        # 距离上限用于保持风险标签稳定，避免远距离差异影响当前导览决策。
        best_distance = self.max_clearance_meters

        for index, start in enumerate(space.boundary):
            end = space.boundary[(index + 1) % len(space.boundary)]
            distance = ray_segment_distance(origin, direction, start, end)
            if distance is not None:
                best_distance = min(best_distance, distance)

        for obstacle in space.obstacles:
            distance = ray_circle_distance(origin, direction, obstacle.center, obstacle.radius)
            if distance is not None:
                best_distance = min(best_distance, distance)

        return max(0.0, min(self.max_clearance_meters, best_distance))

    def _clearance_to_risk(self, clearance: float) -> str:
        if clearance < self.critical_clearance_meters:
            return "critical"
        if clearance < self.unsafe_clearance_meters:
            return "high"
        if clearance < self.safe_clearance_meters:
            return "medium"
        return "low"


def parse_physical_space(payload: dict) -> PhysicalSpace:
    obstacles = tuple(
        PhysicalObstacle(
            id=str(obstacle["id"]),
            center=(float(obstacle["center"][0]), float(obstacle["center"][1])),
            radius=max(0.0, float(obstacle["radius"])),
        )
        for obstacle in payload.get("obstacles", [])
    )
    boundary = tuple((float(point[0]), float(point[1])) for point in payload["boundary"])
    return PhysicalSpace(id=str(payload["id"]), boundary=boundary, obstacles=obstacles)


def normalize(vector: tuple[float, float]) -> tuple[float, float]:
    length = math.hypot(vector[0], vector[1])
    if length <= EPSILON:
        return (0.0, 1.0)
    return (vector[0] / length, vector[1] / length)


def cross(a: tuple[float, float], b: tuple[float, float]) -> float:
    return a[0] * b[1] - a[1] * b[0]


def subtract(a: tuple[float, float], b: tuple[float, float]) -> tuple[float, float]:
    return a[0] - b[0], a[1] - b[1]


def dot(a: tuple[float, float], b: tuple[float, float]) -> float:
    return a[0] * b[0] + a[1] * b[1]


def is_point_inside_polygon(
    point: tuple[float, float],
    polygon: tuple[tuple[float, float], ...],
) -> bool:
    """判断物理 XZ 点是否位于 tracking polygon 内。"""

    if len(polygon) < 3:
        return False

    inside = False
    previous_index = len(polygon) - 1
    for index, current in enumerate(polygon):
        previous = polygon[previous_index]
        intersects = ((current[1] > point[1]) != (previous[1] > point[1])) and (
            point[0]
            < (previous[0] - current[0])
            * (point[1] - current[1])
            / (previous[1] - current[1] + EPSILON)
            + current[0]
        )
        if intersects:
            inside = not inside
        previous_index = index
    return inside


def ray_segment_distance(
    origin: tuple[float, float],
    direction: tuple[float, float],
    segment_start: tuple[float, float],
    segment_end: tuple[float, float],
) -> float | None:
    """计算 forward ray 到墙体线段的距离；未命中时返回 None。"""

    segment = subtract(segment_end, segment_start)
    denominator = cross(direction, segment)
    if abs(denominator) < EPSILON:
        return None

    delta = subtract(segment_start, origin)
    ray_t = cross(delta, segment) / denominator
    segment_t = cross(delta, direction) / denominator
    if ray_t >= 0.0 and 0.0 <= segment_t <= 1.0:
        return ray_t
    return None


def ray_circle_distance(
    origin: tuple[float, float],
    direction: tuple[float, float],
    center: tuple[float, float],
    radius: float,
) -> float | None:
    """计算 forward ray 到圆形障碍物的距离；未命中时返回 None。"""

    if radius <= 0.0:
        return None

    to_origin = subtract(origin, center)
    c_value = dot(to_origin, to_origin) - radius * radius
    if c_value <= 0.0:
        return 0.0

    b_value = dot(to_origin, direction)
    discriminant = b_value * b_value - c_value
    if discriminant < 0.0:
        return None

    ray_t = -b_value - math.sqrt(discriminant)
    if ray_t >= 0.0:
        return ray_t
    return None
