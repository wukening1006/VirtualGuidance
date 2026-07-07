"""Unity/backend 接口、agent 中间结果和 JSONL 日志的 Pydantic schema 定义。"""

from __future__ import annotations

from enum import Enum
from typing import Any, Literal

from pydantic import BaseModel, ConfigDict, Field, field_validator


class Condition(str, Enum):
    """实验条件枚举，需要在 Unity、后端日志和分析脚本之间保持稳定。"""

    B1_MLLM_ONLY = "B1_mllm_only"
    B2_RAW_PHYSICAL_DATA = "B2_raw_physical_data"
    B3_RISK_LABELS = "B3_risk_labels"
    B4_SCRIPTED_RESET = "B4_scripted_reset"
    B5_OURS_WITHOUT_VERIFIER = "B5_ours_without_verifier"
    OURS_FULL = "ours_full"


class TaskId(str, Enum):
    T1_OVERVIEW = "T1_overview"
    T2_MAIN_TARGET = "T2_main_target"
    T3_COMPARE = "T3_compare"
    T4_NEXT_STEP = "T4_next_step"


class AvatarAction(str, Enum):
    POINT_LEFT = "point_left"
    POINT_RIGHT = "point_right"
    POINT_FORWARD = "point_forward"
    LOOK_AT_TARGET = "look_at_target"
    WAIT = "wait"


class MovementInstruction(str, Enum):
    MOVE_FORWARD_SLOWLY = "move_forward_slowly"
    TURN_LEFT_SLOWLY = "turn_left_slowly"
    TURN_RIGHT_SLOWLY = "turn_right_slowly"
    LOOK_LEFT = "look_left"
    LOOK_RIGHT = "look_right"
    LOOK_FORWARD = "look_forward"
    PAUSE_AND_WAIT = "pause_and_wait"
    RESET_REQUIRED = "reset_required"


Direction = Literal["forward", "left", "right"]


class NearbyExhibit(BaseModel):
    id: str
    relative_direction: Direction
    semantic_relevance: float = Field(default=0.0, ge=0.0, le=1.0)


class GuidanceRequest(BaseModel):
    model_config = ConfigDict(use_enum_values=True)

    # 字段名对应 Unity/backend 接口契约；除非同步更新 Unity 反序列化和分析脚本，否则不要改名。
    condition: Condition
    task_id: TaskId
    physical_space_id: str
    current_exhibit: str
    user_physical_position: list[float]
    user_physical_forward: list[float]
    participant_id: str = "P001"
    user_task: str | None = None
    semantic_next_target: str | None = None
    user_virtual_position: list[float] | None = None
    user_virtual_forward: list[float] | None = None
    nearby_exhibits: list[NearbyExhibit] | None = None

    @field_validator("user_physical_position", "user_physical_forward")
    @classmethod
    def validate_vector(cls, value: list[float]) -> list[float]:
        if len(value) not in (2, 3):
            raise ValueError("Expected a 2D [x,z] or 3D [x,y,z] vector.")
        return value

    @property
    def physical_position_xz(self) -> tuple[float, float]:
        return project_to_xz(self.user_physical_position)

    @property
    def physical_forward_xz(self) -> tuple[float, float]:
        return project_to_xz(self.user_physical_forward)


class VerifierResult(BaseModel):
    approved: bool
    reason: str


class GuidanceResponse(BaseModel):
    selected_target: str
    route_decision: str
    narration: str
    gaze_cue: str
    gaze_target_id: str
    avatar_action: AvatarAction
    movement_instruction: MovementInstruction
    physical_intent: str
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


class Affordance(BaseModel):
    intervention_type: str
    preferred_direction: str | None
    semantic_anchor: str
    physical_goal: str
    avoided_direction: str | None = None


class GuideDraft(BaseModel):
    selected_target: str
    route_decision: str
    narration: str
    gaze_cue: str
    gaze_target_id: str
    avatar_action: AvatarAction
    movement_instruction: MovementInstruction
    physical_intent: str
    intervention_type: str
    explicit_reset_triggered: bool = False


class GuidanceLogRecord(BaseModel):
    """每个 condition 通用的稳定 JSONL 日志行，供实验分析使用。"""

    timestamp: str
    participant_id: str
    condition: str
    physical_space: str
    task_id: str
    current_exhibit: str
    semantic_next_target: str
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
    avatar_action: str
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


def response_from_draft(draft: GuideDraft, verifier: VerifierResult) -> GuidanceResponse:
    """在不改动 Unity guidance 字段名的前提下附加 verifier 结果。"""

    return GuidanceResponse(
        selected_target=draft.selected_target,
        route_decision=draft.route_decision,
        narration=draft.narration,
        gaze_cue=draft.gaze_cue,
        gaze_target_id=draft.gaze_target_id,
        avatar_action=draft.avatar_action,
        movement_instruction=draft.movement_instruction,
        physical_intent=draft.physical_intent,
        verifier=verifier,
    )


JsonDict = dict[str, Any]
