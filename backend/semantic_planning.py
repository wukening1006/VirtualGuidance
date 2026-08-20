"""自然语言任务驱动的 Semantic-only Local Planner 与静态图 Verifier。"""

from __future__ import annotations

import json
import logging
import os
from typing import Any, Mapping, Protocol

from fastapi import HTTPException
from pydantic import ValidationError

from backend.schemas import (
    SemanticPlanDraft,
    SemanticPlanRequest,
    SemanticPlanResponse,
    VerifierResult,
    verify_semantic_plan,
)
from backend.services.mllm_provider import (
    MLLMConfigurationError,
    MLLMHTTPError,
    MLLMProvider,
    MLLMResponseError,
    MLLMTimeoutError,
    DeepSeekOpenAICompatibleProvider,
    DeepSeekProviderSettings,
    QwenOpenAICompatibleProvider,
    QwenProviderSettings,
    VLLMOpenAICompatibleProvider,
    VLLMProviderSettings,
)


SEMANTIC_PLAN_TEMPERATURE = 0.0
SEMANTIC_PLAN_MAX_TOKENS = 512
SEMANTIC_PLAN_SYSTEM_PROMPT = (
    "你是博物馆自然语言任务的 Semantic-only Local Planner。"
    "只能依据 task、task_state 和冻结的任务候选节点生成 1 个展品节点。"
    "final_goal 是不可更改的最终目标；达到 minimum_visits 前不得选择它，"
    "达到 maximum_visits 或进入 final_approach 后必须只选择它。"
    "Choose from the allowed exhibit candidates based on semantic relevance "
    "and narrative coherence. "
    "Semantic edges are supporting relationship evidence. "
    "A missing direct semantic edge does not make an exhibit unavailable. "
    "narration 应自然说明所选展品与最终目标之间的联系。"
    "必须返回 ordered_node_ids 与 narration。"
    "不得使用、推断或输出 Physical Risk、Clearance、RDW、Gain、Reset 或执行方向。"
    "只能使用给定的展品节点，不能创建不存在的节点。"
)
SEMANTIC_PLAN_JSON_SCHEMA: dict[str, Any] = {
    "type": "object",
    "additionalProperties": False,
    "properties": {
        "ordered_node_ids": {
            "type": "array",
            "items": {"type": "string", "minLength": 1},
            "minItems": 1,
            "maxItems": 1,
        },
        "narration": {"type": "string", "minLength": 1, "maxLength": 200},
    },
    "required": ["ordered_node_ids", "narration"],
}

logger = logging.getLogger(__name__)


class SemanticPlanGenerator(Protocol):
    """Semantic Planner 依赖的最小异步接口。"""

    async def generate(self, request: SemanticPlanRequest) -> SemanticPlanDraft:
        """生成一份待静态验证的局部语义计划。"""


class MLLMSemanticPlanGenerator:
    """使用现有 OpenAI-compatible Provider 生成严格语义计划。"""

    def __init__(self, provider: MLLMProvider) -> None:
        self.provider = provider

    async def generate(self, request: SemanticPlanRequest) -> SemanticPlanDraft:
        # request schema 已经从类型层排除了物理事实，序列化前再做一层固定上下文裁剪。
        context = {
            "task": request.task.model_dump(mode="json"),
            "task_state": request.task_state.model_dump(mode="json"),
            "local_semantic_nodes": [
                node.model_dump(mode="json") for node in request.local_semantic_nodes
            ],
            "local_semantic_edges": [
                edge.model_dump(mode="json") for edge in request.local_semantic_edges
            ],
        }
        content = await self.provider.complete_json(
            messages=[
                {"role": "system", "content": SEMANTIC_PLAN_SYSTEM_PROMPT},
                {
                    "role": "user",
                    "content": json.dumps(context, ensure_ascii=False, sort_keys=True),
                },
            ],
            json_schema=SEMANTIC_PLAN_JSON_SCHEMA,
            temperature=SEMANTIC_PLAN_TEMPERATURE,
            max_tokens=SEMANTIC_PLAN_MAX_TOKENS,
        )
        return SemanticPlanDraft.parse_model_json(content, request)


class DeterministicSemanticPlanGenerator:
    """离线测试用 Planner，按冻结任务候选节点稳定生成合法路径。"""

    async def generate(self, request: SemanticPlanRequest) -> SemanticPlanDraft:
        progress = request.task_state
        policy = request.task.intermediate_policy
        final_target_id = request.task.final_goal.exhibit_id
        visited = set(progress.visited_exhibit_ids)
        candidate_ids = sorted(node.id for node in request.local_semantic_nodes)

        final_ready = progress.intermediate_visit_count >= policy.minimum_visits
        final_required = (
            progress.phase == "final_approach"
            or progress.intermediate_visit_count >= policy.maximum_visits
        )
        current = progress.current_exhibit_id
        if final_ready:
            return SemanticPlanDraft(
                ordered_node_ids=[final_target_id],
                narration="前面的参观已经建立了理解线索，现在前往最终目标展品。",
            )
        if final_required:
            return SemanticPlanDraft(
                ordered_node_ids=[final_target_id],
                narration="前面的参观已经建立了理解线索，现在前往最终目标展品。",
            )

        candidates = [
            node_id
            for node_id in candidate_ids
            if node_id != final_target_id
            and node_id != current
            and node_id not in visited
        ]
        if not candidates:
            raise ValueError("semantic planner found no local executable graph node")
        return SemanticPlanDraft(
            ordered_node_ids=[candidates[0]],
            narration="先参观一个相关展品，建立理解最终目标所需的文化线索。",
        )


def create_semantic_plan_generator_from_env(
    env: Mapping[str, str] | None = None,
) -> SemanticPlanGenerator:
    """显式创建正式 MLLM 或离线 Mock，禁止静默降级。"""

    runtime_env = os.environ if env is None else env
    provider_name = runtime_env.get("MLLM_PROVIDER", "").strip().lower()
    if provider_name in {"mock", "offline_mock"}:
        return DeterministicSemanticPlanGenerator()
    if provider_name in {"qwen", "qwen_openai_compatible"}:
        return MLLMSemanticPlanGenerator(
            QwenOpenAICompatibleProvider(QwenProviderSettings.from_env(runtime_env))
        )
    if provider_name in {"deepseek", "deepseek_openai_compatible"}:
        return MLLMSemanticPlanGenerator(
            DeepSeekOpenAICompatibleProvider(DeepSeekProviderSettings.from_env(runtime_env))
        )
    if provider_name in {"vllm", "vllm_openai_compatible"}:
        return MLLMSemanticPlanGenerator(
            VLLMOpenAICompatibleProvider(VLLMProviderSettings.from_env(runtime_env))
        )
    raise MLLMConfigurationError(
        "MLLM_PROVIDER must explicitly configure a formal provider or offline mock."
    )


class SemanticPlanPipeline:
    """执行 Semantic-only Planner 与确定性计划 Verifier。"""

    def __init__(self, planner: SemanticPlanGenerator | None = None) -> None:
        self.planner = planner

    async def _deterministic_fallback(
        self,
        request: SemanticPlanRequest,
        reason: str,
    ) -> tuple[SemanticPlanDraft | None, VerifierResult | None]:
        """模型不可用或计划不合法时，使用静态图生成可验证的局部计划。"""
        try:
            fallback_draft = await DeterministicSemanticPlanGenerator().generate(request)
        except ValueError:
            return None, None

        fallback_verifier = verify_semantic_plan(request, fallback_draft)
        if not fallback_verifier.approved:
            return None, None

        logger.warning(
            "Semantic planner fallback activated: %s; request_id=%s",
            reason,
            request.request_id,
        )
        return fallback_draft, fallback_verifier

    async def handle(self, request: SemanticPlanRequest) -> SemanticPlanResponse:
        try:
            if self.planner is None:
                self.planner = create_semantic_plan_generator_from_env()
            draft = await self.planner.generate(request)
        except MLLMConfigurationError as exc:
            raise HTTPException(status_code=503, detail=str(exc)) from exc
        except (
            MLLMTimeoutError,
            MLLMHTTPError,
            MLLMResponseError,
            ValueError,
            ValidationError,
        ) as exc:
            fallback_draft, fallback_verifier = await self._deterministic_fallback(
                request,
                f"model failure: {exc}",
            )
            if fallback_draft is None or fallback_verifier is None:
                raise HTTPException(
                    status_code=502,
                    detail=f"Semantic plan unavailable: {exc}",
                ) from exc
            draft = fallback_draft
            verifier = fallback_verifier
        else:
            verifier = verify_semantic_plan(request, draft)
            if not verifier.approved:
                fallback_draft, fallback_verifier = await self._deterministic_fallback(
                    request,
                    f"verifier rejection: {verifier.reason}",
                )
                if fallback_draft is not None and fallback_verifier is not None:
                    draft = fallback_draft
                    verifier = fallback_verifier

        if not verifier.approved:
            raise HTTPException(
                status_code=502,
                detail=f"Semantic plan verifier rejected plan: {verifier.reason}",
            )
        return SemanticPlanResponse(
            planning_schema_version=request.planning_schema_version,
            session_id=request.session_id,
            request_id=request.request_id,
            generation=request.generation,
            ordered_node_ids=draft.ordered_node_ids,
            narration=draft.narration,
            verifier=verifier,
        )
