"""Unity/backend 接口、agent 中间结果和 JSONL 日志的 Pydantic schema 定义。"""

from __future__ import annotations

from datetime import datetime, timezone
from enum import Enum
import json
import math
from typing import Any, Literal

from pydantic import (
    BaseModel,
    ConfigDict,
    Field,
    ValidationError,
    field_validator,
    model_validator,
)


TASK_DEFINITION_VERSION = "t2-main-target-v2"
GUIDANCE_SCHEMA_VERSION = "guidance-schema-v3"
CANDIDATE_POLICY_VERSION = "t2-candidate-policy-v1"
RISK_POLICY_VERSION = "unity-risk-policy-v2"
SEMANTIC_PLANNING_SCHEMA_VERSION = "semantic-local-planning-v1"
RDW_ROUTE_SELECTION_SCHEMA_VERSION = "rdw-route-selection-v2"


class ExperimentProfile(BaseModel):
    """Unity 与 Backend 共用的三条件能力 Profile。"""

    model_config = ConfigDict(extra="forbid")

    condition_id: Literal[
        "B1_semantic_only",
        "B2_semantic_only_continuous",
        "ours_rdw_grounded_topk",
    ]
    planner_mode: Literal["SemanticOnly", "RdwGroundedTopK"]
    continuous_rdw_enabled: bool
    rdw_planning_mode: Literal["Legacy", "RdwConstrained"]
    reset_mode: Literal["Normal", "SemanticContextualized"]


FORMAL_EXPERIMENT_PROFILES: dict[str, ExperimentProfile] = {
    "B1_semantic_only": ExperimentProfile(
        condition_id="B1_semantic_only",
        planner_mode="SemanticOnly",
        continuous_rdw_enabled=False,
        rdw_planning_mode="Legacy",
        reset_mode="Normal",
    ),
    "B2_semantic_only_continuous": ExperimentProfile(
        condition_id="B2_semantic_only_continuous",
        planner_mode="SemanticOnly",
        continuous_rdw_enabled=True,
        rdw_planning_mode="Legacy",
        reset_mode="Normal",
    ),
    "ours_rdw_grounded_topk": ExperimentProfile(
        condition_id="ours_rdw_grounded_topk",
        planner_mode="RdwGroundedTopK",
        continuous_rdw_enabled=True,
        rdw_planning_mode="RdwConstrained",
        reset_mode="SemanticContextualized",
    ),
}


def formal_experiment_profile(condition_id: str) -> ExperimentProfile:
    """返回正式三条件 Profile；历史条件不会静默映射进正式实验。"""

    try:
        return FORMAL_EXPERIMENT_PROFILES[condition_id].model_copy(deep=True)
    except KeyError as exc:
        raise ValueError(f"Unknown formal experiment condition: {condition_id}") from exc


class SceneLayoutSize(BaseModel):
    model_config = ConfigDict(extra="forbid", allow_inf_nan=False)

    width: float
    length: float


class SceneLayoutBounds(BaseModel):
    model_config = ConfigDict(extra="forbid", allow_inf_nan=False)

    x_min: float
    x_max: float
    z_min: float
    z_max: float


class SceneLayout(BaseModel):
    model_config = ConfigDict(extra="forbid", allow_inf_nan=False)

    units: str = Field(min_length=1)
    floor_origin: list[float]
    floor_center: list[float]
    floor_size_m: SceneLayoutSize
    floor_bounds: SceneLayoutBounds
    entrance_side: str = Field(min_length=1)

    @field_validator("floor_origin", "floor_center")
    @classmethod
    def validate_layout_vectors(cls, value: list[float]) -> list[float]:
        if len(value) != 3 or any(not math.isfinite(item) for item in value):
            raise ValueError("Layout vectors must be finite 3D arrays.")
        return value


class SceneTheme(BaseModel):
    model_config = ConfigDict(extra="forbid")

    id: str = Field(min_length=1)
    name: str = Field(min_length=1)
    description: str = Field(min_length=1)


class SceneBinding(BaseModel):
    model_config = ConfigDict(extra="forbid", allow_inf_nan=False)

    scene_path: str = Field(min_length=1)
    position: list[float]
    viewpoint: list[float]
    gaze_target: list[float]

    @field_validator("position", "viewpoint", "gaze_target")
    @classmethod
    def validate_binding_vectors(cls, value: list[float]) -> list[float]:
        if len(value) != 3 or any(not math.isfinite(item) for item in value):
            raise ValueError("Scene binding vectors must be finite 3D arrays.")
        return value


class SceneInterpretationNote(BaseModel):
    model_config = ConfigDict(extra="forbid")

    statement: str = Field(min_length=1)
    status: str = Field(min_length=1)
    note: str | None = None


class SceneKnowledge(BaseModel):
    model_config = ConfigDict(extra="forbid")

    entity_type: str = Field(min_length=1)
    category: str = Field(min_length=1)
    material: list[str] | None
    culture: str = Field(min_length=1)
    chronology: str = Field(min_length=1)
    archaeological_context: str = Field(min_length=1)
    description: str = Field(min_length=1)
    visual_features: list[str] = Field(min_length=1)
    cultural_significance: list[str] = Field(min_length=1)
    key_concepts: list[str] = Field(min_length=1)
    themes: list[str] = Field(min_length=1)
    interpretation_notes: list[SceneInterpretationNote]

    @field_validator("material", "visual_features", "cultural_significance", "key_concepts", "themes")
    @classmethod
    def validate_knowledge_lists(cls, value: list[str] | None) -> list[str] | None:
        if value is not None and any(not item.strip() for item in value):
            raise ValueError("Knowledge list entries must be non-empty.")
        return value


class SceneExhibit(BaseModel):
    model_config = ConfigDict(extra="forbid")

    id: str = Field(min_length=1)
    name: str = Field(min_length=1)
    aliases: list[str]
    scene_binding: SceneBinding
    knowledge: SceneKnowledge


class SceneSemanticEdge(BaseModel):
    model_config = ConfigDict(extra="forbid")

    id: str = Field(min_length=1)
    source: str = Field(min_length=1)
    target: str = Field(min_length=1)
    relation_type: Literal[
        "context_of",
        "same_category",
        "variant_of",
        "compare_with",
        "thematic_association",
        "synthesis_into",
    ]
    directed: bool
    description: str = Field(min_length=1)
    evidence_status: Literal["direct", "supported", "contextual", "interpretive"]


class SceneSpatialEdge(BaseModel):
    model_config = ConfigDict(extra="forbid", allow_inf_nan=False)

    id: str = Field(min_length=1)
    source: str = Field(min_length=1)
    target: str = Field(min_length=1)
    relation_type: Literal[
        "adjacent_central_axis",
        "adjacent_diagonal",
        "adjacent_branch",
        "same_row",
    ]
    bidirectional: bool
    virtual_distance_m: float = Field(gt=0)
    relative_direction_from_source: str = Field(min_length=1)


class SceneSemanticsDocument(BaseModel):
    """scene_semantics.json schema_version=3 的唯一 Backend DTO。"""

    model_config = ConfigDict(extra="forbid", allow_inf_nan=False)

    schema_version: Literal[3]
    scene_id: str = Field(min_length=1)
    scene_name: str = Field(min_length=1)
    coordinate_system: str = Field(min_length=1)
    layout: SceneLayout
    themes: list[SceneTheme]
    exhibits: list[SceneExhibit]
    semantic_edges: list[SceneSemanticEdge]
    spatial_edges: list[SceneSpatialEdge]

    @model_validator(mode="after")
    def validate_graph(self) -> "SceneSemanticsDocument":
        theme_ids = [theme.id for theme in self.themes]
        exhibit_ids = [exhibit.id for exhibit in self.exhibits]
        if len(theme_ids) != len(set(theme_ids)):
            raise ValueError("duplicate_theme_id")
        if len(exhibit_ids) != len(set(exhibit_ids)):
            raise ValueError("duplicate_exhibit_id")
        theme_set = set(theme_ids)
        exhibit_set = set(exhibit_ids)
        for exhibit in self.exhibits:
            unknown = set(exhibit.knowledge.themes) - theme_set
            if unknown:
                raise ValueError(f"unknown_theme_reference:{sorted(unknown)[0]}")

        semantic_ids: set[str] = set()
        semantic_logical: set[tuple[str, str, str]] = set()
        for edge in self.semantic_edges:
            if edge.id in semantic_ids:
                raise ValueError(f"duplicate_semantic_edge_id:{edge.id}")
            semantic_ids.add(edge.id)
            if edge.source not in exhibit_set or edge.target not in exhibit_set:
                raise ValueError(f"broken_semantic_edge:{edge.id}")
            if edge.source == edge.target:
                raise ValueError(f"semantic_edge_self_loop:{edge.id}")
            source, target = (edge.source, edge.target) if edge.directed or edge.source < edge.target else (edge.target, edge.source)
            key = (source, target, edge.relation_type)
            if key in semantic_logical:
                raise ValueError(f"duplicate_semantic_logical_edge:{edge.relation_type}")
            semantic_logical.add(key)

        positions = {exhibit.id: exhibit.scene_binding.position for exhibit in self.exhibits}
        spatial_ids: set[str] = set()
        spatial_logical: set[tuple[str, str, str]] = set()
        for edge in self.spatial_edges:
            if edge.id in spatial_ids:
                raise ValueError(f"duplicate_spatial_edge_id:{edge.id}")
            spatial_ids.add(edge.id)
            if edge.source not in exhibit_set or edge.target not in exhibit_set:
                raise ValueError(f"broken_spatial_edge:{edge.id}")
            if edge.source == edge.target:
                raise ValueError(f"spatial_edge_self_loop:{edge.id}")
            source, target = (edge.source, edge.target) if edge.source < edge.target else (edge.target, edge.source)
            key = (source, target, edge.relation_type) if edge.bidirectional else (edge.source, edge.target, edge.relation_type)
            if key in spatial_logical:
                raise ValueError(f"duplicate_spatial_logical_edge:{edge.relation_type}")
            spatial_logical.add(key)
            dx = positions[edge.source][0] - positions[edge.target][0]
            dz = positions[edge.source][2] - positions[edge.target][2]
            computed = math.sqrt(dx * dx + dz * dz)
            if abs(computed - edge.virtual_distance_m) > 0.05:
                raise ValueError(f"spatial_distance_mismatch:{edge.id}")

        if self.scene_id == "sanxingdui_gallery":
            if (len(self.themes), len(self.exhibits), len(self.semantic_edges), len(self.spatial_edges)) != (7, 8, 14, 14):
                raise ValueError("sanxingdui_graph_counts_invalid")
            if self._counts(self.semantic_edges) != {
                "context_of": 3, "same_category": 1, "variant_of": 1,
                "compare_with": 3, "thematic_association": 3, "synthesis_into": 3,
            }:
                raise ValueError("sanxingdui_semantic_relation_counts_invalid")
            if self._counts(self.spatial_edges) != {
                "adjacent_central_axis": 3, "adjacent_diagonal": 8,
                "adjacent_branch": 2, "same_row": 1,
            }:
                raise ValueError("sanxingdui_spatial_relation_counts_invalid")
            if any(not edge.bidirectional for edge in self.spatial_edges):
                raise ValueError("sanxingdui_spatial_edges_must_be_bidirectional")
        return self

    @staticmethod
    def _counts(edges: list[Any]) -> dict[str, int]:
        counts: dict[str, int] = {}
        for edge in edges:
            counts[edge.relation_type] = counts.get(edge.relation_type, 0) + 1
        return counts


class Condition(str, Enum):
    """实验条件枚举，需要在 Unity、后端日志和分析脚本之间保持稳定。"""

    B1_MLLM_ONLY = "B1_mllm_only"
    B2_RAW_PHYSICAL_DATA = "B2_raw_physical_data"
    B3_RISK_LABELS = "B3_risk_labels"
    B4_SCRIPTED_RESET = "B4_scripted_reset"
    B5_OURS_WITHOUT_VERIFIER = "B5_ours_without_verifier"
    OURS_FULL = "ours_full"


class ConditionCapabilityMode(str, Enum):
    """实验条件对单项能力的固定使用方式。"""

    DISABLED = "disabled"
    ENABLED = "enabled"
    SCRIPTED = "scripted"


class ConditionPolicy(BaseModel):
    """冻结七项实验能力边界，避免公共逻辑提前改变 baseline 输出。"""

    policy_version: str = "condition-policy-v1"
    condition: Condition
    use_raw_physical_data: ConditionCapabilityMode
    use_risk_labels: ConditionCapabilityMode
    use_rdw_affordance: ConditionCapabilityMode
    use_safe_candidate_filter: ConditionCapabilityMode
    use_semantic_redirection: ConditionCapabilityMode
    use_rotation_gain: ConditionCapabilityMode
    use_model_verifier: ConditionCapabilityMode


def _policy(
    condition: Condition,
    raw: ConditionCapabilityMode,
    risk: ConditionCapabilityMode,
    affordance: ConditionCapabilityMode,
    safe_filter: ConditionCapabilityMode,
    redirection: ConditionCapabilityMode,
    gain: ConditionCapabilityMode,
    verifier: ConditionCapabilityMode,
) -> ConditionPolicy:
    return ConditionPolicy(
        condition=condition,
        use_raw_physical_data=raw,
        use_risk_labels=risk,
        use_rdw_affordance=affordance,
        use_safe_candidate_filter=safe_filter,
        use_semantic_redirection=redirection,
        use_rotation_gain=gain,
        use_model_verifier=verifier,
    )


_D = ConditionCapabilityMode.DISABLED
_E = ConditionCapabilityMode.ENABLED
_S = ConditionCapabilityMode.SCRIPTED

CONDITION_POLICIES: dict[Condition, ConditionPolicy] = {
    Condition.B1_MLLM_ONLY: _policy(Condition.B1_MLLM_ONLY, _D, _D, _D, _D, _D, _D, _D),
    Condition.B2_RAW_PHYSICAL_DATA: _policy(Condition.B2_RAW_PHYSICAL_DATA, _E, _D, _D, _D, _D, _D, _D),
    Condition.B3_RISK_LABELS: _policy(Condition.B3_RISK_LABELS, _D, _E, _D, _D, _D, _D, _D),
    Condition.B4_SCRIPTED_RESET: _policy(Condition.B4_SCRIPTED_RESET, _D, _E, _S, _D, _D, _D, _D),
    Condition.B5_OURS_WITHOUT_VERIFIER: _policy(Condition.B5_OURS_WITHOUT_VERIFIER, _E, _E, _E, _E, _E, _E, _D),
    Condition.OURS_FULL: _policy(Condition.OURS_FULL, _E, _E, _E, _E, _E, _E, _E),
}


def condition_policy_for(condition: str | Condition) -> ConditionPolicy:
    """返回已冻结策略；未知 Condition 不允许静默进入任一实验分支。"""

    return CONDITION_POLICIES[Condition(condition)]


class TaskId(str, Enum):
    T1_OVERVIEW = "T1_overview"
    T2_MAIN_TARGET = "T2_main_target"
    T3_COMPARE = "T3_compare"
    T4_NEXT_STEP = "T4_next_step"


class NavigationTargetRole(str, Enum):
    FINAL_TARGET = "final_target"
    ROUTE_SUBGOAL = "route_subgoal"
    TURN_CUE_ANCHOR = "turn_cue_anchor"
    NAVIGABLE_DETOUR = "navigable_detour"


class RouteDecision(str, Enum):
    """MLLM Draft 允许返回的有限路线决策。"""

    DIRECT_TO_TARGET = "direct_to_target"
    VISIT_ROUTE_SUBGOAL = "visit_route_subgoal"
    SEMANTIC_REDIRECT = "semantic_redirect"
    REGUIDANCE_SAME_TARGET = "reguidance_same_target"


class CandidateInterpretationNote(BaseModel):
    model_config = ConfigDict(extra="forbid")

    statement: str = Field(min_length=1)
    status: str = Field(min_length=1)
    note: str | None = None


class CandidateSemanticEdge(BaseModel):
    model_config = ConfigDict(extra="forbid")

    id: str = Field(min_length=1)
    source: str = Field(min_length=1)
    target: str = Field(min_length=1)
    relation_type: Literal[
        "context_of", "same_category", "variant_of", "compare_with",
        "thematic_association", "synthesis_into",
    ]
    directed: bool
    description: str = Field(min_length=1)
    evidence_status: Literal["direct", "supported", "contextual", "interpretive"]


class CandidateSpatialEdge(BaseModel):
    model_config = ConfigDict(extra="forbid", allow_inf_nan=False)

    id: str = Field(min_length=1)
    source: str = Field(min_length=1)
    target: str = Field(min_length=1)
    relation_type: Literal[
        "adjacent_central_axis", "adjacent_diagonal", "adjacent_branch", "same_row",
    ]
    bidirectional: bool
    virtual_distance_m: float = Field(gt=0)
    relative_direction_from_source: str = Field(min_length=1)


class SemanticCandidate(BaseModel):
    """Unity 冻结并提供给模型的 v3 候选知识上下文。"""

    model_config = ConfigDict(extra="forbid")

    id: str = Field(min_length=1)
    target_role: NavigationTargetRole
    relative_direction: Literal["forward", "left", "right", "behind"]
    relative_yaw_deg: float = Field(ge=-180.0, le=180.0, exclude=True)
    suggested_movement_instruction: Literal[
        "move_forward_slowly",
        "turn_left_slowly",
        "turn_right_slowly",
    ] = Field(exclude=True)
    name: str = Field(min_length=1)
    category: str = Field(min_length=1)
    culture: str = Field(min_length=1)
    chronology: str = Field(min_length=1)
    archaeological_context: str = Field(min_length=1)
    description: str = Field(min_length=1)
    visual_features: list[str]
    cultural_significance: list[str]
    key_concepts: list[str]
    themes: list[str]
    interpretation_notes: list[CandidateInterpretationNote]
    semantic_edges: list[CandidateSemanticEdge]
    spatial_edges: list[CandidateSpatialEdge]


class MovementInstruction(str, Enum):
    MOVE_FORWARD_SLOWLY = "move_forward_slowly"
    TURN_LEFT_SLOWLY = "turn_left_slowly"
    TURN_RIGHT_SLOWLY = "turn_right_slowly"
    LOOK_LEFT = "look_left"
    LOOK_RIGHT = "look_right"
    LOOK_FORWARD = "look_forward"
    PAUSE_AND_WAIT = "pause_and_wait"
    RESET_REQUIRED = "reset_required"


# 方向权威归 Unity：movement 与 gaze 一律由 relative_direction 推导，禁止模型自行填写方向。
# behind 不构成可执行前进方向，退化为原地等待，避免诱导参观者向身后移动。
MOVEMENT_BY_DIRECTION: dict[str, MovementInstruction] = {
    "forward": MovementInstruction.MOVE_FORWARD_SLOWLY,
    "left": MovementInstruction.TURN_LEFT_SLOWLY,
    "right": MovementInstruction.TURN_RIGHT_SLOWLY,
    "behind": MovementInstruction.PAUSE_AND_WAIT,
}

GAZE_CUE_BY_DIRECTION: dict[str, str] = {
    "forward": "look_forward",
    "left": "look_left",
    "right": "look_right",
    "behind": "look_forward",
}


def movement_for_direction(direction: str) -> MovementInstruction:
    """把语义方向映射为唯一可执行的 movement enum；未知方向按原地等待兜底。"""

    return MOVEMENT_BY_DIRECTION.get(direction, MovementInstruction.PAUSE_AND_WAIT)


def movement_for_candidate_bearing(
    suggested_movement_instruction: str,
    relative_yaw_deg: float,
) -> MovementInstruction:
    """复用 Unity 动作分类，并以连续偏航符号校验转向语义。"""

    suggested_movement = MovementInstruction(suggested_movement_instruction)
    if suggested_movement == MovementInstruction.MOVE_FORWARD_SLOWLY:
        return suggested_movement
    if relative_yaw_deg < 0.0:
        return MovementInstruction.TURN_LEFT_SLOWLY
    if relative_yaw_deg > 0.0:
        return MovementInstruction.TURN_RIGHT_SLOWLY
    return suggested_movement


def gaze_cue_for_direction(direction: str) -> str:
    """把语义方向映射为 Unity 可识别的 gaze cue；未知方向退化为看向前方。"""

    return GAZE_CUE_BY_DIRECTION.get(direction, "look_forward")


class RiskBoundaryContext(BaseModel):
    """Unity 物理边界的严格协议对象。"""

    model_config = ConfigDict(extra="forbid", allow_inf_nan=False)

    points: list[list[float]]

    @field_validator("points")
    @classmethod
    def validate_points(cls, value: list[list[float]]) -> list[list[float]]:
        if any(len(point) != 2 or any(not math.isfinite(item) for item in point) for point in value):
            raise ValueError("Boundary points must be finite Physical XZ pairs.")
        return value


class RiskObstacleContext(BaseModel):
    """Unity 单个物理障碍物的严格协议对象。"""

    model_config = ConfigDict(extra="forbid", allow_inf_nan=False)

    id: str = Field(min_length=1)
    center: list[float]
    radius: float = Field(ge=0)

    @field_validator("center")
    @classmethod
    def validate_center(cls, value: list[float]) -> list[float]:
        if len(value) != 2 or any(not math.isfinite(item) for item in value):
            raise ValueError("Obstacle center must be a finite Physical XZ pair.")
        return value


class RiskSnapshot(BaseModel):
    """Unity 生成的在线风险事实；Backend 只做协议、范围和身份校验。"""

    model_config = ConfigDict(extra="forbid", allow_inf_nan=False)

    source: Literal["unity"]
    risk_snapshot_id: str = Field(min_length=1)
    risk_revision: int = Field(ge=1)
    calibration_id: str = Field(min_length=1)
    sampled_at_utc: str = Field(min_length=1)
    risk_policy_version: Literal["unity-risk-policy-v2"]
    physical_space: str = Field(min_length=1)
    physical_position: list[float]
    physical_forward: list[float]
    boundary_context: RiskBoundaryContext
    obstacle_context: list[RiskObstacleContext]
    risk_level: Literal["low", "medium", "high", "critical"]
    critical: bool
    reset_required: bool

    @field_validator("physical_position", "physical_forward")
    @classmethod
    def validate_snapshot_vector(cls, value: list[float]) -> list[float]:
        if len(value) not in (2, 3) or any(not math.isfinite(item) for item in value):
            raise ValueError("RiskSnapshot vectors must be finite 2D or 3D arrays.")
        return [float(item) for item in value]

    @field_validator("sampled_at_utc")
    @classmethod
    def validate_sampled_at_utc(cls, value: str) -> str:
        try:
            sampled_at = datetime.fromisoformat(value)
        except ValueError as exc:
            raise ValueError("sampled_at_utc must be an ISO 8601 timestamp.") from exc
        if sampled_at.tzinfo is None or sampled_at.utcoffset() != timezone.utc.utcoffset(sampled_at):
            raise ValueError("sampled_at_utc must use UTC.")
        return value

class GuidanceRequest(BaseModel):
    model_config = ConfigDict(use_enum_values=True, extra="forbid")

    # 字段名对应 Unity/backend 接口契约；旧版本请求必须显式失败，不能与新客户端混用。
    task_definition_version: Literal["t2-main-target-v2"]
    guidance_schema_version: Literal["guidance-schema-v3"]
    candidate_policy_version: Literal["t2-candidate-policy-v1"]
    risk_policy_version: Literal["unity-risk-policy-v2"]
    session_id: str = Field(min_length=1)
    calibration_id: str = Field(min_length=1)
    request_id: str = Field(min_length=1)
    decision_id: str = Field(min_length=1)
    candidate_set_id: str = Field(min_length=1)
    state_version: int = Field(ge=0)
    risk_snapshot_id: str = Field(min_length=1)
    condition: Condition
    task_id: TaskId
    physical_space_id: str = Field(min_length=1)
    current_exhibit: str = Field(min_length=1)
    risk_snapshot: RiskSnapshot
    user_physical_position: list[float]
    user_physical_forward: list[float]
    participant_id: str = "P001"
    user_task: str | None = None
    final_target: str = Field(min_length=1)
    preferred_task_target: str = Field(min_length=1)
    candidate_ids: list[str] = Field(min_length=1)
    model_candidate_ids: list[str] = Field(min_length=1)
    allowed_candidate_ids: list[str] = Field(default_factory=list)
    semantic_candidates: list[SemanticCandidate] = Field(min_length=1)
    user_virtual_position: list[float] | None = None
    user_virtual_forward: list[float] | None = None

    @field_validator("task_id")
    @classmethod
    def validate_active_task(cls, value: TaskId) -> TaskId:
        if value != TaskId.T2_MAIN_TARGET:
            raise ValueError("Formal sessions only support T2_main_target.")
        return value

    @field_validator("user_physical_position", "user_physical_forward")
    @classmethod
    def validate_vector(cls, value: list[float]) -> list[float]:
        if len(value) not in (2, 3):
            raise ValueError("Expected a 2D [x,z] or 3D [x,y,z] vector.")
        return value

    @model_validator(mode="after")
    def validate_unity_snapshot_identity(self) -> "GuidanceRequest":
        if self.task_id != TaskId.T2_MAIN_TARGET:
            return self
        if self.risk_snapshot.source != "unity":
            raise ValueError("risk_snapshot.source must be unity.")
        if self.risk_snapshot.risk_snapshot_id != self.risk_snapshot_id:
            raise ValueError("risk_snapshot_id must match risk_snapshot.risk_snapshot_id.")
        if self.risk_snapshot.calibration_id != self.calibration_id:
            raise ValueError("calibration_id must match risk_snapshot.calibration_id.")
        if self.risk_snapshot.risk_policy_version != self.risk_policy_version:
            raise ValueError("risk_policy_version must match risk_snapshot.risk_policy_version.")
        if self.risk_snapshot.physical_space != self.physical_space_id:
            raise ValueError("physical_space_id must match risk_snapshot.physical_space.")
        if self.user_physical_position != self.risk_snapshot.physical_position:
            raise ValueError("user_physical_position must mirror Unity risk_snapshot.")
        if self.user_physical_forward != self.risk_snapshot.physical_forward:
            raise ValueError("user_physical_forward must mirror Unity risk_snapshot.")

        candidate_ids = set(self.candidate_ids)
        model_candidate_ids = set(self.model_candidate_ids)
        allowed_candidate_ids = set(self.allowed_candidate_ids)
        semantic_candidate_ids = {candidate.id for candidate in self.semantic_candidates}
        if len(candidate_ids) != len(self.candidate_ids):
            raise ValueError("candidate_ids must be unique.")
        if len(model_candidate_ids) != len(self.model_candidate_ids):
            raise ValueError("model_candidate_ids must be unique.")
        if len(allowed_candidate_ids) != len(self.allowed_candidate_ids):
            raise ValueError("allowed_candidate_ids must be unique.")
        if len(semantic_candidate_ids) != len(self.semantic_candidates):
            raise ValueError("semantic_candidates ids must be unique.")
        if not model_candidate_ids.issubset(candidate_ids):
            raise ValueError("model_candidate_ids must be a subset of candidate_ids.")
        if not allowed_candidate_ids.issubset(candidate_ids):
            raise ValueError("allowed_candidate_ids must be a subset of candidate_ids.")
        if semantic_candidate_ids != model_candidate_ids:
            raise ValueError("semantic_candidates ids must exactly match model_candidate_ids.")
        if self.final_target not in candidate_ids or self.preferred_task_target not in candidate_ids:
            raise ValueError("final_target and preferred_task_target must belong to candidate_ids.")

        policy = condition_policy_for(self.condition)
        if policy.use_safe_candidate_filter == ConditionCapabilityMode.ENABLED:
            if self.model_candidate_ids != self.allowed_candidate_ids:
                raise ValueError("Safe-filter conditions require model_candidate_ids == allowed_candidate_ids.")
        elif self.model_candidate_ids != self.candidate_ids:
            raise ValueError("Baseline conditions require model_candidate_ids == candidate_ids.")
        return self

    def candidate_for(self, target_id: str) -> SemanticCandidate | None:
        """返回冻结模型候选中的目标元数据。"""

        return next((candidate for candidate in self.semantic_candidates if candidate.id == target_id), None)

    @property
    def physical_position_xz(self) -> tuple[float, float]:
        return project_to_xz(self.user_physical_position)

    @property
    def physical_forward_xz(self) -> tuple[float, float]:
        return project_to_xz(self.user_physical_forward)


class VerifierResult(BaseModel):
    model_config = ConfigDict(extra="forbid")

    approved: bool
    reason: str = Field(min_length=1)


class ThemeGoal(BaseModel):
    """Semantic-only Planner 使用的静态主题目标。"""

    model_config = ConfigDict(extra="forbid")

    theme_id: str = Field(min_length=1)
    name: str = Field(min_length=1)
    description: str = Field(min_length=1)


class SemanticPlanningNode(BaseModel):
    """不包含 Physical Risk 的语义规划节点。"""

    model_config = ConfigDict(extra="forbid", allow_inf_nan=False)

    id: str = Field(min_length=1)
    name: str = Field(min_length=1)
    category: str = Field(min_length=1)
    culture: str = Field(min_length=1)
    chronology: str = Field(min_length=1)
    description: str = Field(min_length=1)
    visual_features: list[str]
    cultural_significance: list[str]
    key_concepts: list[str]
    themes: list[str]
    interpretation_notes: list[CandidateInterpretationNote]
    semantic_edges: list[CandidateSemanticEdge]
    spatial_edges: list[CandidateSpatialEdge]


class SemanticPlanRequest(BaseModel):
    """Semantic-only 局部规划请求；协议上不存在物理状态字段。"""

    model_config = ConfigDict(extra="forbid", allow_inf_nan=False)

    planning_schema_version: Literal["semantic-local-planning-v1"]
    session_id: str = Field(min_length=1)
    request_id: str = Field(min_length=1)
    generation: int = Field(ge=1)
    task_state_version: int = Field(ge=0)
    theme_goal: ThemeGoal
    current_exhibit_id: str = Field(min_length=1)
    visited_exhibit_ids: list[str]
    theme_progress: dict[str, float]
    local_semantic_nodes: list[SemanticPlanningNode] = Field(min_length=1)
    local_spatial_edges: list[CandidateSpatialEdge]
    local_semantic_edges: list[CandidateSemanticEdge]

    @field_validator("theme_progress")
    @classmethod
    def validate_theme_progress(cls, value: dict[str, float]) -> dict[str, float]:
        if any(not math.isfinite(progress) or progress < 0.0 or progress > 1.0
               for progress in value.values()):
            raise ValueError("theme_progress values must be finite numbers in [0, 1].")
        return value

    @model_validator(mode="after")
    def validate_local_graph(self) -> "SemanticPlanRequest":
        node_ids = [node.id for node in self.local_semantic_nodes]
        if len(node_ids) != len(set(node_ids)):
            raise ValueError("local_semantic_nodes ids must be unique.")
        if self.current_exhibit_id not in node_ids:
            raise ValueError("current_exhibit_id must belong to local_semantic_nodes.")
        if len(set(self.visited_exhibit_ids)) != len(self.visited_exhibit_ids):
            raise ValueError("visited_exhibit_ids must be unique.")
        node_set = set(node_ids)
        for edge in [*self.local_spatial_edges, *self.local_semantic_edges]:
            if edge.source not in node_set or edge.target not in node_set:
                raise ValueError("local graph edges must reference local_semantic_nodes.")
        return self


class SemanticPlanDraft(BaseModel):
    """LLM 唯一可控制的语义计划字段。"""

    model_config = ConfigDict(extra="forbid")

    ordered_node_ids: list[str] = Field(min_length=1, max_length=3)
    narration: str = Field(min_length=1, max_length=200)

    @classmethod
    def parse_model_json(
        cls,
        content: str,
        request: SemanticPlanRequest,
    ) -> "SemanticPlanDraft":
        try:
            payload = json.loads(content)
        except json.JSONDecodeError as exc:
            raise ValueError("Semantic plan output is not valid JSON.") from exc
        try:
            draft = cls.model_validate(payload)
        except ValidationError as exc:
            raise ValueError(f"Semantic plan schema validation failed: {exc}") from exc
        if any(node_id not in {node.id for node in request.local_semantic_nodes} for node_id in draft.ordered_node_ids):
            raise ValueError("Semantic plan contains a node outside local_semantic_nodes.")
        return draft


class SemanticPlanResponse(BaseModel):
    """经过确定性 Verifier 后返回给 Unity 的语义计划。"""

    model_config = ConfigDict(extra="forbid")

    planning_schema_version: Literal["semantic-local-planning-v1"]
    session_id: str = Field(min_length=1)
    request_id: str = Field(min_length=1)
    generation: int = Field(ge=1)
    ordered_node_ids: list[str] = Field(min_length=1, max_length=3)
    narration: str = Field(min_length=1, max_length=200)
    verifier: VerifierResult


def verify_semantic_plan(
    request: SemanticPlanRequest,
    draft: SemanticPlanDraft,
) -> VerifierResult:
    """只验证静态节点和 Spatial Edge，不读取 Physical Risk。"""

    node_ids = {node.id for node in request.local_semantic_nodes}
    if any(node_id not in node_ids for node_id in draft.ordered_node_ids):
        return VerifierResult(approved=False, reason="node_outside_local_graph")
    if len(set(draft.ordered_node_ids)) != len(draft.ordered_node_ids):
        return VerifierResult(approved=False, reason="plan_contains_cycle")
    if request.current_exhibit_id in draft.ordered_node_ids:
        return VerifierResult(approved=False, reason="plan_starts_at_current_exhibit")
    visited = set(request.visited_exhibit_ids)
    if any(node_id in visited for node_id in draft.ordered_node_ids):
        return VerifierResult(approved=False, reason="plan_targets_visited_exhibit")
    spatial_pairs = {
        (edge.source, edge.target)
        for edge in request.local_spatial_edges
    }
    spatial_pairs.update(
        (edge.target, edge.source)
        for edge in request.local_spatial_edges
        if edge.bidirectional
    )
    for source, target in zip(
        [request.current_exhibit_id, *draft.ordered_node_ids],
        draft.ordered_node_ids,
    ):
        if (source, target) not in spatial_pairs:
            return VerifierResult(
                approved=False,
                reason=f"missing_spatial_edge:{source}->{target}",
            )

    node_by_id = {node.id: node for node in request.local_semantic_nodes}
    planned_nodes = [node_by_id[node_id] for node_id in draft.ordered_node_ids]
    theme_id = request.theme_goal.theme_id
    semantic_edges = {
        (edge.source, edge.target)
        for edge in request.local_semantic_edges
    }
    semantic_edges.update(
        (edge.target, edge.source)
        for edge in request.local_semantic_edges
        if not edge.directed
    )
    for node in planned_nodes:
        if not node.themes or theme_id in node.themes:
            continue
        connected_to_theme_node = any(
            other is not node
            and other.themes
            and theme_id in other.themes
            and ((node.id, other.id) in semantic_edges or (other.id, node.id) in semantic_edges)
            for other in planned_nodes
        )
        if not connected_to_theme_node:
            return VerifierResult(approved=False, reason="plan_leaves_theme_context")
    for left, right in zip(planned_nodes, planned_nodes[1:]):
        shares_theme = bool(set(left.themes).intersection(right.themes))
        if semantic_edges and not shares_theme and (left.id, right.id) not in semantic_edges:
            return VerifierResult(
                approved=False,
                reason=f"semantic_discontinuity:{left.id}->{right.id}",
            )
    return VerifierResult(approved=True, reason="semantic_plan_is_graph_valid")


class PlanningRouteEdge(BaseModel):
    """Unity 已完成 Exact Safety 后发布的单条通用路线边代价。"""

    model_config = ConfigDict(extra="forbid", allow_inf_nan=False)

    from_node_id: str
    to_node_id: str = Field(min_length=1)
    relative_yaw_deg: float = Field(ge=-180.0, le=180.0)
    clearance_m: float = Field(ge=0.0)
    rdw_benefit: float = Field(ge=0.0, le=1.0)
    gain_cost: float = Field(ge=0.0, le=1.0)
    reset_pressure: float = Field(ge=0.0, le=1.0)


class PlanningRouteCandidate(BaseModel):
    """MLLM 只能按 route_id 选择的冻结路线候选。"""

    model_config = ConfigDict(extra="forbid", allow_inf_nan=False)

    route_id: str = Field(min_length=1)
    ordered_node_ids: list[str] = Field(min_length=1)
    first_target_id: str = Field(min_length=1)
    route_utility: float
    semantic_progress: float = Field(default=0.0, ge=0.0, le=1.0)
    intervention_level: Literal["low", "medium", "high"]
    estimator_id: str = Field(min_length=1)
    estimator_version: str = Field(min_length=1)
    edges: list[PlanningRouteEdge] = Field(min_length=1)

    @model_validator(mode="after")
    def validate_route_shape(self) -> "PlanningRouteCandidate":
        if len(set(self.ordered_node_ids)) != len(self.ordered_node_ids):
            raise ValueError("ordered_node_ids must not contain cycles.")
        if self.first_target_id != self.ordered_node_ids[0]:
            raise ValueError("first_target_id must match ordered_node_ids[0].")
        if len(self.edges) != len(self.ordered_node_ids):
            raise ValueError("edges must align one-to-one with ordered_node_ids.")
        if any(edge.to_node_id != node_id for edge, node_id in zip(self.edges, self.ordered_node_ids)):
            raise ValueError("each edge.to_node_id must align with ordered_node_ids.")
        return self


class RouteSelectionRequest(BaseModel):
    """Unity Top-K 路线选择请求；不允许 Backend 重算 Physical Safety。"""

    model_config = ConfigDict(extra="forbid", allow_inf_nan=False)

    planning_schema_version: Literal["rdw-route-selection-v1", "rdw-route-selection-v2"]
    session_id: str = Field(min_length=1)
    request_id: str = Field(min_length=1)
    plan_id: str = Field(min_length=1)
    snapshot_id: str = Field(min_length=1)
    generation: int = Field(ge=1)
    task_state_version: int = Field(ge=0)
    calibration_id: str = Field(min_length=1)
    physical_space_id: str = Field(min_length=1)
    risk_snapshot_id: str = Field(min_length=1)
    risk_revision: int = Field(ge=1)
    current_node_id: str = Field(min_length=1)
    final_target_id: str = ""
    estimator_id: str = Field(min_length=1)
    estimator_version: str = Field(min_length=1)
    route_candidates: list[PlanningRouteCandidate] = Field(min_length=1, max_length=5)
    theme_goal: ThemeGoal | None = None
    visited_exhibit_ids: list[str] = Field(default_factory=list)
    theme_progress: dict[str, float] = Field(default_factory=dict)

    @model_validator(mode="after")
    def validate_candidate_identity(self) -> "RouteSelectionRequest":
        if self.planning_schema_version == RDW_ROUTE_SELECTION_SCHEMA_VERSION and self.theme_goal is None:
            raise ValueError("rdw-route-selection-v2 requires theme_goal.")
        if self.planning_schema_version == "rdw-route-selection-v1" and not self.final_target_id:
            raise ValueError("rdw-route-selection-v1 requires final_target_id.")
        if len(set(self.visited_exhibit_ids)) != len(self.visited_exhibit_ids):
            raise ValueError("visited_exhibit_ids must be unique.")
        route_ids = [candidate.route_id for candidate in self.route_candidates]
        if len(route_ids) != len(set(route_ids)):
            raise ValueError("route_candidates route_id values must be unique.")
        if any(
            candidate.estimator_id != self.estimator_id
            or candidate.estimator_version != self.estimator_version
            for candidate in self.route_candidates
        ):
            raise ValueError("route candidate estimator identity must match the request.")
        return self

    def route_for(self, route_id: str) -> PlanningRouteCandidate | None:
        """按冻结 route_id 查询候选。"""

        return next(
            (candidate for candidate in self.route_candidates if candidate.route_id == route_id),
            None,
        )


class RouteSelectionResponse(BaseModel):
    """后端只返回已有 route_id、narration 和 membership verifier。"""

    model_config = ConfigDict(extra="forbid")

    planning_schema_version: Literal["rdw-route-selection-v1", "rdw-route-selection-v2"]
    session_id: str = Field(min_length=1)
    request_id: str = Field(min_length=1)
    plan_id: str = Field(min_length=1)
    snapshot_id: str = Field(min_length=1)
    generation: int = Field(ge=1)
    route_id: str = Field(min_length=1)
    narration: str = Field(min_length=1, max_length=200)
    verifier: VerifierResult


class GuidanceResponse(BaseModel):
    model_config = ConfigDict(use_enum_values=True, extra="forbid")

    task_definition_version: Literal["t2-main-target-v2"]
    guidance_schema_version: Literal["guidance-schema-v3"]
    candidate_policy_version: Literal["t2-candidate-policy-v1"]
    risk_policy_version: Literal["unity-risk-policy-v2"]
    session_id: str = Field(min_length=1)
    calibration_id: str = Field(min_length=1)
    request_id: str = Field(min_length=1)
    decision_id: str = Field(min_length=1)
    candidate_set_id: str = Field(min_length=1)
    state_version: int = Field(ge=0)
    risk_snapshot_id: str = Field(min_length=1)
    risk_revision: int = Field(ge=1)
    target_role: NavigationTargetRole
    selected_target: str = Field(min_length=1)
    route_decision: str = Field(min_length=1)
    narration: str = Field(min_length=1)
    gaze_cue: str = Field(min_length=1)
    gaze_target_id: str = Field(min_length=1)
    movement_instruction: MovementInstruction
    physical_intent: str = Field(min_length=1)
    verifier: VerifierResult


class PhysicalRiskState(BaseModel):
    """由规则模块计算出的物理安全状态，供 guide 和 verifier 使用。"""

    physical_space: str
    user_physical_position: tuple[float, float]
    user_physical_forward: tuple[float, float]
    boundary_risk: str
    reset_risk: str
    reset_required: bool = False
    risk_snapshot_id: str = "offline-risk"
    risk_revision: int = 1
    calibration_id: str = "offline-calibration"
    sampled_at_utc: str = "offline"
    risk_policy_version: str = RISK_POLICY_VERSION
    source: str = "offline"
    risk_level: str = "low"
    critical: bool = False


class Affordance(BaseModel):
    intervention_type: str
    preferred_direction: str | None
    semantic_anchor: str
    physical_goal: str
    avoided_direction: str | None = None


class GuideDraft(BaseModel):
    selected_target: str
    route_decision: RouteDecision
    narration: str
    gaze_cue: str
    gaze_target_id: str
    movement_instruction: MovementInstruction
    physical_intent: str
    intervention_type: str
    explicit_reset_triggered: bool = False


class GuidanceLogRecord(BaseModel):
    """每个 condition 通用的稳定 JSONL 日志行，供实验分析使用。"""

    timestamp: str
    session_id: str
    calibration_id: str
    request_id: str
    decision_id: str
    candidate_set_id: str
    state_version: int
    risk_snapshot_id: str
    risk_revision: int
    target_role: str
    policy_version: str
    raw_guidance: dict[str, Any]
    condition_verifier_result: dict[str, Any]
    runtime_validation_result: str
    runtime_intervention: bool | None
    executed_guidance: dict[str, Any] | None
    participant_id: str
    condition: str
    physical_space: str
    task_id: str
    current_exhibit: str
    final_target: str
    preferred_task_target: str
    candidate_ids: list[str]
    model_candidate_ids: list[str]
    allowed_candidate_ids: list[str]
    semantic_candidates: list[dict[str, Any]]
    selected_target: str
    boundary_risk: str
    reset_risk: str
    intervention_type: str
    narration: str
    movement_instruction: str
    verifier_approved: bool
    verifier_rejection_reason: str
    explicit_reset_triggered: bool


class HealthResponse(BaseModel):
    model_config = ConfigDict(extra="forbid", allow_inf_nan=False)

    status: str
    version: str
    physical_spaces: list[str]
    scene_id: str
    schema_version: int
    exhibit_count: int
    semantic_edge_count: int
    spatial_edge_count: int
    semantic_ready: bool


def project_to_xz(value: list[float]) -> tuple[float, float]:
    """兼容 Unity 传入的 [x,z] 或 [x,y,z] 向量，并统一投影到物理 XZ 平面。"""

    if len(value) == 2:
        return float(value[0]), float(value[1])
    return float(value[0]), float(value[2])


def response_from_draft(
    request: GuidanceRequest,
    draft: GuideDraft,
    verifier: VerifierResult,
) -> GuidanceResponse:
    """在不改动 Unity guidance 字段名的前提下附加 verifier 结果。"""

    selected_candidate = request.candidate_for(draft.selected_target)
    if selected_candidate is None:
        raise ValueError("selected_target must belong to frozen model candidates.")

    return GuidanceResponse(
        task_definition_version=request.task_definition_version,
        guidance_schema_version=request.guidance_schema_version,
        candidate_policy_version=request.candidate_policy_version,
        risk_policy_version=request.risk_policy_version,
        session_id=request.session_id,
        calibration_id=request.calibration_id,
        request_id=request.request_id,
        decision_id=request.decision_id,
        candidate_set_id=request.candidate_set_id,
        state_version=request.state_version,
        risk_snapshot_id=request.risk_snapshot_id,
        risk_revision=request.risk_snapshot.risk_revision,
        target_role=selected_candidate.target_role,
        selected_target=draft.selected_target,
        route_decision=draft.route_decision,
        narration=draft.narration,
        gaze_cue=draft.gaze_cue,
        gaze_target_id=draft.gaze_target_id,
        movement_instruction=draft.movement_instruction,
        physical_intent=draft.physical_intent,
        verifier=verifier,
    )


JsonDict = dict[str, Any]
