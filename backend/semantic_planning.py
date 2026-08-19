"""Theme-driven Semantic-only Local Planner 与静态图 Verifier。"""

from __future__ import annotations

import json
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
    QwenOpenAICompatibleProvider,
    QwenProviderSettings,
    VLLMOpenAICompatibleProvider,
    VLLMProviderSettings,
)


SEMANTIC_PLAN_TEMPERATURE = 0.0
SEMANTIC_PLAN_MAX_TOKENS = 512
SEMANTIC_PLAN_SYSTEM_PROMPT = (
    "你是博物馆主题导览的 Semantic-only Local Planner。"
    "只能依据 ThemeGoal、当前展品、已参观展品和语义/虚拟空间图生成 1-3 个展品节点。"
    "必须返回 ordered_node_ids 与 narration。"
    "不得使用、推断或输出 Physical Risk、Clearance、RDW、Gain、Reset 或执行方向。"
    "只能使用给定的展品和 Spatial Edge，不能创建不存在的节点或连接。"
)
SEMANTIC_PLAN_JSON_SCHEMA: dict[str, Any] = {
    "type": "object",
    "additionalProperties": False,
    "properties": {
        "ordered_node_ids": {
            "type": "array",
            "items": {"type": "string", "minLength": 1},
            "minItems": 1,
            "maxItems": 3,
        },
        "narration": {"type": "string", "minLength": 1, "maxLength": 200},
    },
    "required": ["ordered_node_ids", "narration"],
}


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
            "theme_goal": request.theme_goal.model_dump(mode="json"),
            "current_exhibit_id": request.current_exhibit_id,
            "visited_exhibit_ids": list(request.visited_exhibit_ids),
            "theme_progress": dict(request.theme_progress),
            "local_semantic_nodes": [
                node.model_dump(mode="json") for node in request.local_semantic_nodes
            ],
            "local_spatial_edges": [
                edge.model_dump(mode="json") for edge in request.local_spatial_edges
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
    """离线测试用 Planner，按局部 Spatial Graph 稳定生成合法路径。"""

    async def generate(self, request: SemanticPlanRequest) -> SemanticPlanDraft:
        visited = set(request.visited_exhibit_ids)
        adjacency: dict[str, list[str]] = {
            node.id: [] for node in request.local_semantic_nodes
        }
        for edge in request.local_spatial_edges:
            adjacency.setdefault(edge.source, []).append(edge.target)
            if edge.bidirectional:
                adjacency.setdefault(edge.target, []).append(edge.source)
        for values in adjacency.values():
            values.sort()

        path: list[str] = []
        current = request.current_exhibit_id
        seen = {current}
        for _ in range(3):
            candidates = [
                node_id
                for node_id in adjacency.get(current, [])
                if node_id not in seen and node_id not in visited
            ]
            if not candidates:
                break
            current = candidates[0]
            path.append(current)
            seen.add(current)
        if not path:
            # 主题接近完成时允许选择一个已访问邻居作为保守的语义收束点。
            fallback = [
                node_id
                for node_id in adjacency.get(request.current_exhibit_id, [])
                if node_id != request.current_exhibit_id
            ]
            path = fallback[:1]
        if not path:
            raise ValueError("semantic planner found no local executable graph node")
        return SemanticPlanDraft(
            ordered_node_ids=path,
            narration="我们沿着当前主题线索继续参观。",
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

    async def handle(self, request: SemanticPlanRequest) -> SemanticPlanResponse:
        try:
            if self.planner is None:
                self.planner = create_semantic_plan_generator_from_env()
            draft = await self.planner.generate(request)
        except MLLMConfigurationError as exc:
            raise HTTPException(status_code=503, detail=str(exc)) from exc
        except MLLMTimeoutError as exc:
            raise HTTPException(status_code=504, detail=str(exc)) from exc
        except (MLLMHTTPError, MLLMResponseError) as exc:
            raise HTTPException(status_code=502, detail=str(exc)) from exc
        except (ValueError, ValidationError) as exc:
            raise HTTPException(
                status_code=502,
                detail=f"Semantic plan contract violation: {exc}",
            ) from exc

        verifier = verify_semantic_plan(request, draft)
        if not verifier.approved and verifier.reason.startswith("missing_spatial_edge:"):
            # 模型可能混淆语义边与空间边；只对空间图失败使用确定性路径兜底。
            try:
                fallback_draft = await DeterministicSemanticPlanGenerator().generate(request)
            except ValueError:
                fallback_draft = None
            if fallback_draft is not None:
                fallback_verifier = verify_semantic_plan(request, fallback_draft)
                if fallback_verifier.approved:
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
