"""统一 MLLM Draft Generator 与六条件模型上下文裁剪。"""

from __future__ import annotations

import json
import os
from typing import Any, Mapping, Protocol

from pydantic import BaseModel, ConfigDict, Field, ValidationError

from backend.schemas import (
    Affordance,
    Condition,
    GuidanceRequest,
    GuideDraft,
    MovementInstruction,
    PhysicalRiskState,
    RouteDecision,
    gaze_cue_for_direction,
    movement_for_direction,
)
from backend.services.mllm_provider import (
    MLLMConfigurationError,
    MLLMProvider,
    QwenOpenAICompatibleProvider,
    QwenProviderSettings,
    VLLMOpenAICompatibleProvider,
    VLLMProviderSettings,
)


MLLM_TEMPERATURE = 0.0
MLLM_MAX_TOKENS = 512
MLLM_SYSTEM_PROMPT = (
    "你是博物馆虚拟导览系统的目标选择与话术生成模块。"
    "只能依据给定上下文，从候选目标中选择一个目标，并严格返回 JSON Schema 要求的三个字段。"
    "移动方向与注视方向由系统按物理几何确定性推导，不由你决定，也不要在输出中描述具体转向角度。"
    "不得修改最终目标、风险事实、候选集合、任务状态或触发 Reset。"
)

MLLM_DRAFT_JSON_SCHEMA: dict[str, Any] = {
    "type": "object",
    "additionalProperties": False,
    "properties": {
        "selected_target": {"type": "string", "minLength": 1},
        "route_decision": {
            "type": "string",
            "enum": [item.value for item in RouteDecision],
        },
        "narration": {"type": "string", "minLength": 1, "maxLength": 200},
    },
    "required": [
        "selected_target",
        "route_decision",
        "narration",
    ],
}


class GuideDraftGenerator(Protocol):
    """Pipeline 依赖的最小异步 Draft Generator 接口。"""

    async def generate(
        self,
        request: GuidanceRequest,
        risk_state: PhysicalRiskState,
        affordance: Affordance,
    ) -> GuideDraft:
        """为一次冻结 Candidate Set 生成待验证 Draft。"""


class MLLMGuidanceDraft(BaseModel):
    """模型唯一允许控制的三字段严格输出；方向类字段不在其中。"""

    model_config = ConfigDict(extra="forbid")

    selected_target: str = Field(min_length=1)
    route_decision: RouteDecision
    narration: str = Field(min_length=1, max_length=200)

    @classmethod
    def parse_model_json(
        cls,
        content: str,
        request: GuidanceRequest,
    ) -> "MLLMGuidanceDraft":
        """解析严格 JSON，并执行不可关闭的 Candidate Membership 校验。"""

        try:
            data = json.loads(content)
        except json.JSONDecodeError as exc:
            preview = " ".join(content.strip().split())
            if len(preview) > 240:
                preview = f"{preview[:237]}..."
            raise ValueError(
                f"MLLM draft content is not valid JSON; content preview={preview!r}."
            ) from exc

        try:
            # extra="forbid" 同时挡住模型自行补写 movement_instruction 等方向类字段。
            draft = cls.model_validate(data)
        except ValidationError as exc:
            raise ValueError(f"MLLM draft schema validation failed: {exc}") from exc
        if draft.selected_target not in request.model_candidate_ids:
            raise ValueError(
                "MLLM selected_target must belong to the frozen model_candidate_ids."
            )
        return draft


class ConditionContextBuilder:
    """只裁剪模型可见信息，不修改 CandidateSetReservation 或 RiskSnapshot。"""

    def build(
        self,
        request: GuidanceRequest,
        affordance: Affordance,
    ) -> dict[str, Any]:
        """按六条件冻结模型上下文信息边界。"""

        context: dict[str, Any] = {
            "task": {
                "task_id": request.task_id,
                "task_description": request.user_task or "引导参观者到达最终目标展品。",
                "final_goal": request.final_target,
            },
            "navigation": {
                "current_exhibit": request.current_exhibit,
                "preferred_task_target": request.preferred_task_target,
            },
            "candidate_set": {
                "model_candidate_ids": list(request.model_candidate_ids),
                "semantic_candidates": [
                    candidate.model_dump(mode="json")
                    for candidate in request.semantic_candidates
                ],
            },
        }

        condition = Condition(request.condition)
        snapshot = request.risk_snapshot
        if condition == Condition.B2_RAW_PHYSICAL_DATA:
            context["raw_physical_data"] = {
                "position": list(snapshot.physical_position),
                "forward": list(snapshot.physical_forward),
                "boundary": snapshot.boundary_context.model_dump(mode="json"),
                "obstacles": [
                    obstacle.model_dump(mode="json")
                    for obstacle in snapshot.obstacle_context
                ],
            }
        elif condition == Condition.B3_RISK_LABELS:
            context["risk_labels"] = {
                "risk_level": snapshot.risk_level,
                "critical": snapshot.critical,
                "reset_required": snapshot.reset_required,
            }
        elif condition in {
            Condition.B5_OURS_WITHOUT_VERIFIER,
            Condition.OURS_FULL,
        }:
            context["rdw_affordance"] = affordance.model_dump(mode="json")
            context["allowed_candidate_ids"] = list(request.allowed_candidate_ids)
        return context


class MLLMDraftGenerator:
    """使用同一 Provider、Prompt、Schema 与采样参数生成六条件 Draft。"""

    def __init__(
        self,
        provider: MLLMProvider,
        context_builder: ConditionContextBuilder | None = None,
    ) -> None:
        self.provider = provider
        self.context_builder = context_builder or ConditionContextBuilder()

    async def generate(
        self,
        request: GuidanceRequest,
        risk_state: PhysicalRiskState,
        affordance: Affordance,
    ) -> GuideDraft:
        context = self.context_builder.build(request, affordance)
        messages = [
            {"role": "system", "content": MLLM_SYSTEM_PROMPT},
            {
                "role": "user",
                "content": json.dumps(context, ensure_ascii=False, sort_keys=True),
            },
        ]
        content = await self.provider.complete_json(
            messages=messages,
            json_schema=MLLM_DRAFT_JSON_SCHEMA,
            temperature=MLLM_TEMPERATURE,
            max_tokens=MLLM_MAX_TOKENS,
        )
        model_draft = MLLMGuidanceDraft.parse_model_json(content, request)
        candidate = request.candidate_for(model_draft.selected_target)
        if candidate is None:
            raise ValueError("Selected target metadata is missing from the frozen Candidate Set.")

        # reset 阶段优先于几何推导：所有方向都不可执行时只允许原地等待。
        movement_instruction = (
            MovementInstruction.PAUSE_AND_WAIT
            if risk_state.reset_required
            else movement_for_direction(candidate.relative_direction)
        )
        condition = Condition(request.condition)
        uses_affordance = condition in {
            Condition.B5_OURS_WITHOUT_VERIFIER,
            Condition.OURS_FULL,
        }
        return GuideDraft(
            selected_target=model_draft.selected_target,
            route_decision=model_draft.route_decision,
            narration=model_draft.narration,
            gaze_cue=gaze_cue_for_direction(candidate.relative_direction),
            gaze_target_id=f"{model_draft.selected_target}_GazeTarget",
            movement_instruction=movement_instruction,
            physical_intent=(
                affordance.physical_goal
                if uses_affordance
                else f"{condition.value}_model_interpretation"
            ),
            intervention_type=affordance.intervention_type if uses_affordance else "none",
        )


def create_draft_generator_from_env(
    env: Mapping[str, str] | None = None,
) -> GuideDraftGenerator:
    """显式选择正式模型或离线 Mock；缺失配置时禁止静默降级。"""

    runtime_env = os.environ if env is None else env
    provider_name = runtime_env.get("MLLM_PROVIDER", "").strip().lower()
    if provider_name in {"mock", "offline_mock"}:
        from backend.agents.guide_mock import GuideMockAgent

        return GuideMockAgent()
    if provider_name in {"qwen", "qwen_openai_compatible"}:
        settings = QwenProviderSettings.from_env(runtime_env)
        return MLLMDraftGenerator(QwenOpenAICompatibleProvider(settings))
    if provider_name in {"vllm", "vllm_openai_compatible"}:
        settings = VLLMProviderSettings.from_env(runtime_env)
        return MLLMDraftGenerator(VLLMOpenAICompatibleProvider(settings))

    raise MLLMConfigurationError(
        "MLLM_PROVIDER must be qwen_openai_compatible or "
        "vllm_openai_compatible for formal sessions, or mock for explicit "
        "offline tests."
    )
