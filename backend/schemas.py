"""Unity/backend 接口、agent 中间结果和 JSONL 日志的 Pydantic schema 定义。"""

from __future__ import annotations

from datetime import datetime, timezone
from enum import Enum
import math
from typing import Any, Literal

from pydantic import BaseModel, ConfigDict, Field, field_validator, model_validator


TASK_DEFINITION_VERSION = "t2-main-target-v2"
GUIDANCE_SCHEMA_VERSION = "guidance-schema-v2"
CANDIDATE_POLICY_VERSION = "t2-candidate-policy-v1"
RISK_POLICY_VERSION = "unity-risk-policy-v1"


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


class SemanticCandidate(BaseModel):
    """Unity 冻结并提供给模型的候选语义元数据。"""

    model_config = ConfigDict(extra="forbid")

    id: str = Field(min_length=1)
    target_role: NavigationTargetRole
    relative_direction: Literal["forward", "left", "right", "behind"]
    name: str = ""
    description: str = ""
    semantic_role: str = ""
    tags: list[str] = Field(default_factory=list)
    related_exhibits: list[str] = Field(default_factory=list)

    @field_validator("tags", "related_exhibits")
    @classmethod
    def validate_unique_metadata(cls, value: list[str]) -> list[str]:
        if len(value) != len(set(value)):
            raise ValueError("Candidate metadata values must be unique.")
        return value


class MovementInstruction(str, Enum):
    MOVE_FORWARD_SLOWLY = "move_forward_slowly"
    TURN_LEFT_SLOWLY = "turn_left_slowly"
    TURN_RIGHT_SLOWLY = "turn_right_slowly"
    LOOK_LEFT = "look_left"
    LOOK_RIGHT = "look_right"
    LOOK_FORWARD = "look_forward"
    PAUSE_AND_WAIT = "pause_and_wait"
    RESET_REQUIRED = "reset_required"


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
    risk_policy_version: Literal["unity-risk-policy-v1"]
    physical_space: str = Field(min_length=1)
    physical_position: list[float]
    physical_forward: list[float]
    boundary_context: RiskBoundaryContext
    obstacle_context: list[RiskObstacleContext]
    clearance_forward: float = Field(ge=0)
    clearance_left: float = Field(ge=0)
    clearance_right: float = Field(ge=0)
    risk_level: Literal["low", "medium", "high", "critical"]
    safe_directions: list[str]
    unsafe_directions: list[str]
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

    @field_validator("safe_directions", "unsafe_directions")
    @classmethod
    def validate_directions(cls, value: list[str]) -> list[str]:
        allowed = {"forward", "left", "right", "behind"}
        if len(value) != len(set(value)) or any(item not in allowed for item in value):
            raise ValueError("RiskSnapshot directions must be unique known values.")
        return value

    @model_validator(mode="after")
    def validate_direction_partition(self) -> "RiskSnapshot":
        if set(self.safe_directions).intersection(self.unsafe_directions):
            raise ValueError("RiskSnapshot safe and unsafe directions must be disjoint.")
        return self


class GuidanceRequest(BaseModel):
    model_config = ConfigDict(use_enum_values=True, extra="forbid")

    # 字段名对应 Unity/backend 接口契约；旧版本请求必须显式失败，不能与新客户端混用。
    task_definition_version: Literal["t2-main-target-v2"]
    guidance_schema_version: Literal["guidance-schema-v2"]
    candidate_policy_version: Literal["t2-candidate-policy-v1"]
    risk_policy_version: Literal["unity-risk-policy-v1"]
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


class GuidanceResponse(BaseModel):
    model_config = ConfigDict(use_enum_values=True, extra="forbid")

    task_definition_version: Literal["t2-main-target-v2"]
    guidance_schema_version: Literal["guidance-schema-v2"]
    candidate_policy_version: Literal["t2-candidate-policy-v1"]
    risk_policy_version: Literal["unity-risk-policy-v1"]
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
    clearance_forward: float
    clearance_left: float
    clearance_right: float
    boundary_risk: str
    reset_risk: str
    unsafe_directions: list[str]
    safe_directions: list[str]
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
    clearance_forward: float
    clearance_left: float
    clearance_right: float
    boundary_risk: str
    reset_risk: str
    unsafe_directions: list[str]
    safe_directions: list[str]
    intervention_type: str
    narration: str
    movement_instruction: str
    verifier_approved: bool
    verifier_rejection_reason: str
    explicit_reset_triggered: bool


class HealthResponse(BaseModel):
    status: str
    version: str
    physical_spaces: list[str]
    exhibit_count: int


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
