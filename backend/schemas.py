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


TASK_DEFINITION_VERSION = "natural-language-task-v1"
GUIDANCE_SCHEMA_VERSION = "guidance-schema-v3"
CANDIDATE_POLICY_VERSION = "candidate-policy-v2"
RISK_POLICY_VERSION = "unity-risk-policy-v2"
SEMANTIC_PLANNING_SCHEMA_VERSION = "semantic-local-planning-v2"
RDW_ROUTE_SELECTION_SCHEMA_VERSION = "rdw-route-selection-v4"
RESET_SEMANTIC_INTERVENTION_SCHEMA_VERSION = "reset-semantic-intervention-v2"


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


class SceneSemanticsDocument(BaseModel):
    """scene_semantics.json schema_version=4 的唯一 Backend DTO。"""

    model_config = ConfigDict(extra="forbid", allow_inf_nan=False)

    schema_version: Literal[4]
    scene_id: str = Field(min_length=1)
    scene_name: str = Field(min_length=1)
    coordinate_system: str = Field(min_length=1)
    layout: SceneLayout
    themes: list[SceneTheme]
    exhibits: list[SceneExhibit]
    semantic_edges: list[SceneSemanticEdge]

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

        if self.scene_id == "sanxingdui_gallery":
            if (len(self.themes), len(self.exhibits), len(self.semantic_edges)) != (7, 8, 14):
                raise ValueError("sanxingdui_graph_counts_invalid")
            if self._counts(self.semantic_edges) != {
                "context_of": 3, "same_category": 1, "variant_of": 1,
                "compare_with": 3, "thematic_association": 3, "synthesis_into": 3,
            }:
                raise ValueError("sanxingdui_semantic_relation_counts_invalid")
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


# 任务启动时 RDW 搜索使用的临时根节点；它不是语义展品。
RUNTIME_START_NODE_ID = "__runtime_start__"


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


class SemanticCandidate(BaseModel):
    """Unity 冻结并提供给模型的候选知识上下文。"""

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
    task_definition_version: Literal["natural-language-task-v1"]
    guidance_schema_version: Literal["guidance-schema-v3"]
    candidate_policy_version: Literal["candidate-policy-v2"]
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
    current_exhibit: str = ""
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
        return value

    @field_validator("user_physical_position", "user_physical_forward")
    @classmethod
    def validate_vector(cls, value: list[float]) -> list[float]:
        if len(value) not in (2, 3):
            raise ValueError("Expected a 2D [x,z] or 3D [x,y,z] vector.")
        return value

    @model_validator(mode="after")
    def validate_unity_snapshot_identity(self) -> "GuidanceRequest":
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
        # 启动阶段 final_target 是任务契约字段，但按策略不会进入候选集合；
        # preferred_task_target 必须始终来自本轮冻结候选。
        if self.preferred_task_target not in candidate_ids:
            raise ValueError("preferred_task_target must belong to candidate_ids.")

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


class FinalGoalContract(BaseModel):
    """任务不可变的最终展品目标。"""

    model_config = ConfigDict(extra="forbid")

    exhibit_id: str = Field(min_length=1)
    name: str = Field(min_length=1)
    required: bool = True


class IntermediateVisitPolicyContract(BaseModel):
    """最终目标解锁前的中间展品游览约束。"""

    model_config = ConfigDict(extra="forbid")

    minimum_visits: int = Field(ge=0)
    maximum_visits: int = Field(ge=1)
    exclude_final_until_ready: bool = True
    require_semantic_connection: bool = True

    @model_validator(mode="after")
    def validate_visit_range(self) -> "IntermediateVisitPolicyContract":
        if self.maximum_visits < self.minimum_visits:
            raise ValueError("maximum_visits must be greater than or equal to minimum_visits.")
        return self


class NaturalLanguageTaskContract(BaseModel):
    """三个实验条件共享的自然语言任务及其硬约束。"""

    model_config = ConfigDict(extra="forbid")

    task_id: str = Field(min_length=1)
    user_task: str = Field(min_length=1)
    final_goal: FinalGoalContract
    intermediate_policy: IntermediateVisitPolicyContract


class NaturalLanguageTaskState(BaseModel):
    """模型只读的任务进度快照。"""

    model_config = ConfigDict(extra="forbid")

    phase: Literal["exploring", "final_approach", "completed"]
    current_exhibit_id: str = ""
    visited_exhibit_ids: list[str]
    intermediate_visit_count: int = Field(ge=0)
    required_target_id: str = ""

    @model_validator(mode="after")
    def validate_progress(self) -> "NaturalLanguageTaskState":
        if len(set(self.visited_exhibit_ids)) != len(self.visited_exhibit_ids):
            raise ValueError("visited_exhibit_ids must be unique.")
        if not self.current_exhibit_id:
            if (
                self.phase != "exploring"
                or self.visited_exhibit_ids
                or self.intermediate_visit_count != 0
            ):
                raise ValueError(
                    "empty current_exhibit_id is only valid for the exploring startup snapshot."
                )
        elif self.current_exhibit_id not in self.visited_exhibit_ids:
            raise ValueError("current_exhibit_id must belong to visited_exhibit_ids.")
        if self.phase == "final_approach" and not self.required_target_id:
            raise ValueError("final_approach requires required_target_id.")
        if self.phase != "final_approach" and self.required_target_id:
            raise ValueError("required_target_id is only valid during final_approach.")
        return self


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
    interpretation_notes: list[CandidateInterpretationNote]
    semantic_edges: list[CandidateSemanticEdge]


class SemanticPlanRequest(BaseModel):
    """Semantic-only 局部规划请求；协议上不存在物理状态字段。"""

    model_config = ConfigDict(extra="forbid", allow_inf_nan=False)

    planning_schema_version: Literal["semantic-local-planning-v2"]
    session_id: str = Field(min_length=1)
    request_id: str = Field(min_length=1)
    generation: int = Field(ge=1)
    task_state_version: int = Field(ge=0)
    task: NaturalLanguageTaskContract
    task_state: NaturalLanguageTaskState
    local_semantic_nodes: list[SemanticPlanningNode] = Field(min_length=1)
    local_semantic_edges: list[CandidateSemanticEdge]

    @model_validator(mode="after")
    def validate_local_graph(self) -> "SemanticPlanRequest":
        node_ids = [node.id for node in self.local_semantic_nodes]
        if len(node_ids) != len(set(node_ids)):
            raise ValueError("local_semantic_nodes ids must be unique.")
        if self.task_state.current_exhibit_id and self.task_state.current_exhibit_id not in node_ids:
            raise ValueError("current_exhibit_id must belong to local_semantic_nodes.")
        policy = self.task.intermediate_policy
        if self.task_state.intermediate_visit_count > policy.maximum_visits:
            raise ValueError("intermediate_visit_count exceeds maximum_visits.")
        if self.task_state.phase == "final_approach" and (
            self.task_state.required_target_id != self.task.final_goal.exhibit_id
        ):
            raise ValueError("required_target_id must match final_goal during final_approach.")
        node_set = set(node_ids)
        for edge in self.local_semantic_edges:
            if edge.source not in node_set or edge.target not in node_set:
                raise ValueError("local graph edges must reference local_semantic_nodes.")
        return self


class SemanticPlanDraft(BaseModel):
    """LLM 唯一可控制的语义计划字段。"""

    model_config = ConfigDict(extra="forbid")

    ordered_node_ids: list[str] = Field(min_length=1, max_length=1)
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

    planning_schema_version: Literal["semantic-local-planning-v2"]
    session_id: str = Field(min_length=1)
    request_id: str = Field(min_length=1)
    generation: int = Field(ge=1)
    ordered_node_ids: list[str] = Field(min_length=1, max_length=1)
    narration: str = Field(min_length=1, max_length=200)
    verifier: VerifierResult


def verify_semantic_plan(
    request: SemanticPlanRequest,
    draft: SemanticPlanDraft,
) -> VerifierResult:
    """只验证冻结节点成员关系和任务策略，不读取 Physical Risk。"""

    node_ids = {node.id for node in request.local_semantic_nodes}
    if any(node_id not in node_ids for node_id in draft.ordered_node_ids):
        return VerifierResult(approved=False, reason="node_outside_local_graph")
    if len(set(draft.ordered_node_ids)) != len(draft.ordered_node_ids):
        return VerifierResult(approved=False, reason="plan_contains_cycle")
    progress = request.task_state
    policy = request.task.intermediate_policy
    final_target_id = request.task.final_goal.exhibit_id
    if progress.current_exhibit_id in draft.ordered_node_ids:
        return VerifierResult(approved=False, reason="semantic_plan_starts_at_current_exhibit")
    visited = set(progress.visited_exhibit_ids)
    if any(node_id in visited for node_id in draft.ordered_node_ids):
        return VerifierResult(approved=False, reason="semantic_plan_targets_visited_exhibit")
    final_index = next(
        (index for index, node_id in enumerate(draft.ordered_node_ids)
         if node_id == final_target_id),
        -1,
    )
    final_ready = progress.intermediate_visit_count >= policy.minimum_visits
    final_required = (
        progress.phase == "final_approach"
        or progress.intermediate_visit_count >= policy.maximum_visits
    )
    if final_index >= 0 and (
        not final_ready or final_index != 0 or len(draft.ordered_node_ids) != 1
    ):
        reason = (
            "semantic_plan_final_goal_must_be_single_target"
            if final_ready
            else "semantic_plan_final_goal_locked"
        )
        return VerifierResult(approved=False, reason=reason)
    if final_required and final_index != 0:
        return VerifierResult(approved=False, reason="semantic_plan_final_goal_required")

    # Semantic Edge 只提供相关性证据；节点的动态几何与物理安全由 Unity 规划层处理。
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


class RouteSemanticTheme(BaseModel):
    """OURS Route Selection 使用的主题定义。"""

    model_config = ConfigDict(extra="forbid", allow_inf_nan=False)

    id: str = Field(min_length=1)
    name: str = Field(min_length=1)
    description: str = Field(min_length=1)


class RouteSemanticContextNode(BaseModel):
    """OURS Route Selection 使用的局部展品知识；不包含物理执行事实。"""

    model_config = ConfigDict(extra="forbid", allow_inf_nan=False)

    id: str = Field(min_length=1)
    name: str = Field(min_length=1)
    category: str = Field(min_length=1)
    culture: str = Field(min_length=1)
    chronology: str = Field(min_length=1)
    archaeological_context: str = Field(min_length=1)
    description: str = Field(min_length=1)
    visual_features: list[str] = Field(min_length=1)
    cultural_significance: list[str] = Field(min_length=1)
    key_concepts: list[str] = Field(min_length=1)
    themes: list[str] = Field(min_length=1)
    interpretation_notes: list[CandidateInterpretationNote]


class RouteSelectionRequest(BaseModel):
    """Unity Top-K 路线选择请求；不允许 Backend 重算 Physical Safety。"""

    model_config = ConfigDict(extra="forbid", allow_inf_nan=False)

    planning_schema_version: Literal["rdw-route-selection-v4"]
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
    task: NaturalLanguageTaskContract
    task_state: NaturalLanguageTaskState
    local_semantic_nodes: list[RouteSemanticContextNode] = Field(min_length=1)
    local_semantic_edges: list[CandidateSemanticEdge]
    semantic_themes: list[RouteSemanticTheme] = Field(min_length=1)

    @model_validator(mode="after")
    def validate_candidate_identity(self) -> "RouteSelectionRequest":
        if self.final_target_id != self.task.final_goal.exhibit_id:
            raise ValueError("final_target_id must match task.final_goal.exhibit_id.")
        is_runtime_start = self.current_node_id == RUNTIME_START_NODE_ID
        if is_runtime_start:
            if self.task_state.current_exhibit_id:
                raise ValueError("runtime start node requires an empty current_exhibit_id.")
        elif self.current_node_id != self.task_state.current_exhibit_id:
            raise ValueError("current_node_id must match task_state.current_exhibit_id.")
        node_ids = {node.id for node in self.local_semantic_nodes}
        if not is_runtime_start and self.current_node_id not in node_ids:
            raise ValueError("current_node_id must belong to local_semantic_nodes.")
        if any(
            node_id not in node_ids
            for candidate in self.route_candidates
            for node_id in candidate.ordered_node_ids
        ):
            raise ValueError("route candidate nodes must belong to local_semantic_nodes.")
        for edge in self.local_semantic_edges:
            if edge.source not in node_ids or edge.target not in node_ids:
                raise ValueError("local_semantic_edges must reference local_semantic_nodes.")
        if self.task_state.phase == "final_approach" and (
            self.task_state.required_target_id != self.final_target_id
        ):
            raise ValueError("final_approach must require final_target_id.")
        policy = self.task.intermediate_policy
        final_ready = self.task_state.intermediate_visit_count >= policy.minimum_visits
        final_required = (
            self.task_state.phase == "final_approach"
            or self.task_state.intermediate_visit_count >= policy.maximum_visits
        )
        for candidate in self.route_candidates:
            targets_final = candidate.first_target_id == self.final_target_id
            if targets_final and not final_ready:
                raise ValueError("route candidate selects final goal before minimum_visits.")
            if final_required and not targets_final:
                raise ValueError("all route candidates must select final goal during final_approach.")
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

    planning_schema_version: Literal["rdw-route-selection-v4"]
    session_id: str = Field(min_length=1)
    request_id: str = Field(min_length=1)
    plan_id: str = Field(min_length=1)
    snapshot_id: str = Field(min_length=1)
    generation: int = Field(ge=1)
    route_id: str = Field(min_length=1)
    narration: str = Field(min_length=1, max_length=200)
    verifier: VerifierResult


class ResetSemanticInterventionRequest(BaseModel):
    """Reset 已由 Unity 决定后提交的稳定语义上下文。"""

    model_config = ConfigDict(extra="forbid", allow_inf_nan=False)

    planning_schema_version: Literal["reset-semantic-intervention-v2"]
    session_id: str = Field(min_length=1, max_length=160)
    reset_id: str = Field(min_length=1, max_length=80)
    variation_seed: int = Field(ge=0, le=2_147_483_647)
    current_exhibit_id: str = Field(min_length=1, max_length=80)
    current_exhibit_name: str = Field(min_length=1, max_length=120)
    next_target_id: str = Field(default="", max_length=80)
    next_target_name: str = Field(default="", max_length=120)
    current_topic: str = Field(default="", max_length=500)
    semantic_hint: str = Field(min_length=1, max_length=60)
    semantic_hint_key: str = Field(min_length=1, max_length=160)
    recent_narrations: list[str] = Field(default_factory=list, max_length=6)
    recent_content_keys: list[str] = Field(default_factory=list, max_length=6)

    @field_validator("recent_narrations", "recent_content_keys")
    @classmethod
    def validate_reset_history(cls, value: list[str]) -> list[str]:
        if any(not item.strip() for item in value):
            raise ValueError("Reset semantic history entries must be non-empty.")
        return value


class ResetSemanticInterventionResponse(BaseModel):
    """Reset 语义旁路的严格两字段响应。"""

    model_config = ConfigDict(extra="forbid")

    narration: str = Field(min_length=20, max_length=45)
    content_key: str = Field(min_length=1, max_length=160)


class GuidanceResponse(BaseModel):
    model_config = ConfigDict(use_enum_values=True, extra="forbid")

    task_definition_version: Literal["natural-language-task-v1"]
    guidance_schema_version: Literal["guidance-schema-v3"]
    candidate_policy_version: Literal["candidate-policy-v2"]
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
