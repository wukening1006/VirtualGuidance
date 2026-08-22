from __future__ import annotations

from dataclasses import dataclass
import json
import os
from typing import Any, Mapping, Protocol, Sequence

from fastapi import HTTPException
from pydantic import ValidationError

from backend.schemas import (
    ResetSemanticInterventionRequest,
    ResetSemanticInterventionResponse,
    SceneExhibit,
    SceneSemanticEdge,
    SceneSemanticsDocument,
)
from backend.services.mllm_provider import (
    DeepSeekOpenAICompatibleProvider,
    DeepSeekProviderSettings,
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


RESET_SEMANTIC_TEMPERATURE = 0.0
RESET_SEMANTIC_MAX_TOKENS = 128
RESET_SEMANTIC_SYSTEM_PROMPT = (
    "你是博物馆导览中的短语义观察模块。"
    "只能从用户消息给出的 grounded_candidates 中选择一项，并原样返回对应的 content_key 与 narration。"
    "不得补充候选之外的事实，不得输出方向、角度、转身动作、Reset 控制、Gain、Safety、"
    "Clearance、边界或任何物理执行建议。"
)
FORBIDDEN_NARRATION_TOKENS = (
    "左转",
    "右转",
    "向左",
    "向右",
    "转身",
    "转一圈",
    "朝向",
    "角度",
    "180",
    "360",
    "reset",
    "gain",
    "safety",
    "clearance",
    "安全距离",
    "物理边界",
)


@dataclass(frozen=True)
class GroundedResetSemanticCandidate:
    """从受校验静态语义图派生的一条可展示短语义。"""

    content_key: str
    narration: str


class ResetSemanticGrounder:
    """把稳定展品 ID 解析为短语义候选，不读取任何物理状态。"""

    def __init__(self, scene_semantics: SceneSemanticsDocument) -> None:
        self.exhibits = {item.id: item for item in scene_semantics.exhibits}
        self.themes = {item.id: item for item in scene_semantics.themes}
        self.edges = tuple(scene_semantics.semantic_edges)

    def candidates_for(
        self,
        request: ResetSemanticInterventionRequest,
    ) -> tuple[GroundedResetSemanticCandidate, ...]:
        current = self.exhibits.get(request.current_exhibit_id)
        if current is None:
            raise ValueError(
                f"Unknown current_exhibit_id: {request.current_exhibit_id}"
            )
        target = self.exhibits.get(request.next_target_id)

        candidates: list[GroundedResetSemanticCandidate] = []
        if target is not None:
            for edge in self.edges:
                related_id = self._related_id(target.id, edge)
                if related_id in {None, current.id, target.id}:
                    continue
                related = self.exhibits.get(related_id)
                if related is None:
                    continue
                self._add(
                    candidates,
                    f"target-edge:{edge.id}",
                    self._target_relation_narration(
                        target,
                        related,
                        edge.relation_type,
                    ),
                )

        # 有目标关联展品时只返回这些“其他展品”；无关联时才回退到当前展品语义。
        if candidates:
            return tuple(candidates)

        for index, feature in enumerate(current.knowledge.visual_features):
            self._add(
                candidates,
                f"exhibit:{current.id}:visual:{index}",
                f"观察{current.name}时，可以特别留意{feature}这一造型细节。",
            )
        for index, concept in enumerate(current.knowledge.key_concepts):
            self._add(
                candidates,
                f"exhibit:{current.id}:concept:{index}",
                f"回想{current.name}时，可以注意“{concept}”这一关键线索。",
            )
        for theme_id in current.knowledge.themes:
            theme = self.themes.get(theme_id)
            if theme is not None:
                self._add(
                    candidates,
                    f"theme:{theme.id}",
                    f"{current.name}也与“{theme.name}”这一主题相连，可以回想其中的线索。",
                )
        for edge in self.edges:
            neighbor = self._outgoing_neighbor(current.id, edge)
            if neighbor is not None:
                self._add(
                    candidates,
                    f"edge:{edge.id}",
                    self._edge_narration(current, neighbor, edge.relation_type),
                )

        if not candidates:
            self._add(
                candidates,
                f"exhibit:{current.id}:category",
                f"{current.name}属于{current.knowledge.category}，可以留意它独特的文化表达。",
            )
        return tuple(candidates)

    @staticmethod
    def _related_id(target_id: str, edge: SceneSemanticEdge) -> str | None:
        if edge.source == target_id:
            return edge.target
        if edge.target == target_id:
            return edge.source
        return None

    def _outgoing_neighbor(
        self,
        current_id: str,
        edge: SceneSemanticEdge,
    ) -> SceneExhibit | None:
        if edge.source == current_id:
            return self.exhibits.get(edge.target)
        if not edge.directed and edge.target == current_id:
            return self.exhibits.get(edge.source)
        return None

    @staticmethod
    def _edge_narration(
        current: SceneExhibit,
        neighbor: SceneExhibit,
        relation_type: str,
    ) -> str:
        if relation_type == "context_of":
            return f"{current.name}为理解{neighbor.name}提供了背景线索，可以留意两者的联系。"
        if relation_type == "same_category":
            return f"{current.name}与{neighbor.name}属于相近类型，可以比较它们的造型。"
        if relation_type == "variant_of":
            return f"{current.name}与{neighbor.name}构成变体关系，可以比较材质与造型。"
        if relation_type == "compare_with":
            return f"{current.name}与{neighbor.name}适合相互比较，可以留意造型差异。"
        if relation_type == "thematic_association":
            return f"{current.name}与{neighbor.name}共享一条主题线索，可以想想其中的联系。"
        if relation_type == "synthesis_into":
            return f"{current.name}为理解{neighbor.name}提供了一条重要的文化线索。"
        return f"{current.name}与{neighbor.name}之间存在值得留意的文化联系。"

    @staticmethod
    def _target_relation_narration(
        target: SceneExhibit,
        related: SceneExhibit,
        relation_type: str,
    ) -> str:
        if relation_type == "thematic_association":
            return f"{target.name}与{related.name}都关联自然象征，可以一起看看。"
        if relation_type == "compare_with":
            return f"{target.name}与{related.name}适合比较造型细节，可以留意差异。"
        if relation_type == "same_category":
            return f"{target.name}与{related.name}属于相近类型，可以比较造型。"
        if relation_type == "variant_of":
            return f"{target.name}与{related.name}可比较材质与形制，值得留意。"
        if relation_type == "context_of":
            return f"{related.name}可以补充理解{target.name}的背景线索。"
        if relation_type == "synthesis_into":
            return f"{target.name}与{related.name}都能补充祭祀世界的线索。"
        return f"{target.name}与{related.name}之间有语义联系，可以一起看看。"

    @staticmethod
    def _add(
        candidates: list[GroundedResetSemanticCandidate],
        content_key: str,
        narration: str,
    ) -> None:
        normalized = _normalize_narration(narration)
        candidates.append(GroundedResetSemanticCandidate(content_key, normalized))


class ResetSemanticInterventionGenerator(Protocol):
    """Reset 语义生成器的最小异步接口。"""

    async def generate(
        self,
        request: ResetSemanticInterventionRequest,
        candidates: Sequence[GroundedResetSemanticCandidate],
    ) -> ResetSemanticInterventionResponse:
        """从服务器已落地的候选中选择 Reset 期间话术。"""


class MLLMResetSemanticInterventionGenerator:
    """通过现有 OpenAI-compatible provider 选择已落地语义候选。"""

    def __init__(self, provider: MLLMProvider) -> None:
        self.provider = provider

    async def generate(
        self,
        request: ResetSemanticInterventionRequest,
        candidates: Sequence[GroundedResetSemanticCandidate],
    ) -> ResetSemanticInterventionResponse:
        available = _available_candidates(request, candidates)
        content = await self.provider.complete_json(
            messages=[
                {"role": "system", "content": RESET_SEMANTIC_SYSTEM_PROMPT},
                {
                    "role": "user",
                    "content": json.dumps(
                        {
                            "variation_seed": request.variation_seed,
                            "current_topic": request.current_topic,
                            "recent_narrations": request.recent_narrations,
                            "grounded_candidates": [
                                {
                                    "content_key": item.content_key,
                                    "narration": item.narration,
                                }
                                for item in available
                            ],
                        },
                        ensure_ascii=False,
                        sort_keys=True,
                    ),
                },
            ],
            json_schema=_response_schema(available),
            temperature=RESET_SEMANTIC_TEMPERATURE,
            max_tokens=RESET_SEMANTIC_MAX_TOKENS,
        )
        try:
            response = ResetSemanticInterventionResponse.model_validate(json.loads(content))
        except (json.JSONDecodeError, ValidationError) as exc:
            raise ValueError(
                f"Reset semantic response schema validation failed: {exc}"
            ) from exc
        return _validate_candidate_response(response, available)


class DeterministicResetSemanticInterventionGenerator:
    """显式 offline mock；按种子选择未展示过的落地语义候选。"""

    async def generate(
        self,
        request: ResetSemanticInterventionRequest,
        candidates: Sequence[GroundedResetSemanticCandidate],
    ) -> ResetSemanticInterventionResponse:
        available = _available_candidates(request, candidates)
        selected = available[request.variation_seed % len(available)]
        return ResetSemanticInterventionResponse(
            narration=selected.narration,
            content_key=selected.content_key,
        )


def create_reset_semantic_generator_from_env(
    env: Mapping[str, str] | None = None,
) -> ResetSemanticInterventionGenerator:
    """显式创建正式 MLLM 或 offline mock。"""

    runtime_env = os.environ if env is None else env
    provider_name = runtime_env.get("MLLM_PROVIDER", "").strip().lower()
    if provider_name in {"mock", "offline_mock"}:
        return DeterministicResetSemanticInterventionGenerator()
    if provider_name in {"qwen", "qwen_openai_compatible"}:
        return MLLMResetSemanticInterventionGenerator(
            QwenOpenAICompatibleProvider(QwenProviderSettings.from_env(runtime_env))
        )
    if provider_name in {"deepseek", "deepseek_openai_compatible"}:
        return MLLMResetSemanticInterventionGenerator(
            DeepSeekOpenAICompatibleProvider(DeepSeekProviderSettings.from_env(runtime_env))
        )
    if provider_name in {"vllm", "vllm_openai_compatible"}:
        return MLLMResetSemanticInterventionGenerator(
            VLLMOpenAICompatibleProvider(VLLMProviderSettings.from_env(runtime_env))
        )
    raise MLLMConfigurationError(
        "MLLM_PROVIDER must explicitly configure a formal provider or offline mock."
    )


class ResetSemanticInterventionPipeline:
    """使用静态语义图生成候选，并把失败隔离在 Reset 语义旁路内。"""

    def __init__(
        self,
        scene_semantics: SceneSemanticsDocument,
        generator: ResetSemanticInterventionGenerator | None = None,
    ) -> None:
        self.grounder = ResetSemanticGrounder(scene_semantics)
        self.generator = generator

    async def handle(
        self,
        request: ResetSemanticInterventionRequest,
    ) -> ResetSemanticInterventionResponse:
        try:
            candidates = self.grounder.candidates_for(request)
            available = _available_candidates(request, candidates)
            if self.generator is None:
                self.generator = create_reset_semantic_generator_from_env()
            response = await self.generator.generate(request, available)
            return _validate_candidate_response(response, available)
        except MLLMConfigurationError as exc:
            raise HTTPException(status_code=503, detail=str(exc)) from exc
        except MLLMTimeoutError as exc:
            raise HTTPException(status_code=504, detail=str(exc)) from exc
        except (MLLMHTTPError, MLLMResponseError, ValueError, ValidationError) as exc:
            raise HTTPException(
                status_code=502,
                detail=f"Reset semantic intervention unavailable: {exc}",
            ) from exc


def _available_candidates(
    request: ResetSemanticInterventionRequest,
    candidates: Sequence[GroundedResetSemanticCandidate],
) -> tuple[GroundedResetSemanticCandidate, ...]:
    recent_keys = set(request.recent_content_keys)
    recent_narrations = set(request.recent_narrations)
    available = tuple(
        item
        for item in candidates
        if item.content_key not in recent_keys and item.narration not in recent_narrations
    )
    resolved = available or tuple(candidates)
    if not resolved:
        raise ValueError("No grounded Reset semantic candidate is available.")
    return resolved


def _response_schema(
    candidates: Sequence[GroundedResetSemanticCandidate],
) -> dict[str, Any]:
    return {
        "type": "object",
        "additionalProperties": False,
        "properties": {
            "narration": {
                "type": "string",
                "enum": [item.narration for item in candidates],
            },
            "content_key": {
                "type": "string",
                "enum": [item.content_key for item in candidates],
            },
        },
        "required": ["narration", "content_key"],
    }


def _validate_candidate_response(
    response: ResetSemanticInterventionResponse,
    candidates: Sequence[GroundedResetSemanticCandidate],
) -> ResetSemanticInterventionResponse:
    expected = {item.content_key: item.narration for item in candidates}
    if expected.get(response.content_key) != response.narration:
        raise ValueError("Reset semantic response is not a matching grounded candidate.")
    lowered = response.narration.lower()
    if any(token in lowered for token in FORBIDDEN_NARRATION_TOKENS):
        raise ValueError("Reset semantic response contains physical-control language.")
    return response


def _normalize_narration(value: str) -> str:
    normalized = " ".join(value.split()).strip()
    if len(normalized) < 20:
        normalized = normalized.rstrip("。") + "，这也是理解当前主题的一条线索。"
    if len(normalized) > 45:
        normalized = normalized[:44].rstrip("，、；：。") + "。"
    return normalized
