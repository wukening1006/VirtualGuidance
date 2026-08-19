"""RDW Route Selection：模型只能从 Unity Top-K 中选择 route_id。"""

from __future__ import annotations

import json
import os
from typing import Any, Mapping, Protocol

from fastapi import HTTPException
from pydantic import BaseModel, ConfigDict, Field, ValidationError

from backend.schemas import (
    RouteSelectionRequest,
    RouteSelectionResponse,
    VerifierResult,
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


ROUTE_SELECTION_TEMPERATURE = 0.0
ROUTE_SELECTION_MAX_TOKENS = 256
ROUTE_SELECTION_SYSTEM_PROMPT = (
    "你是博物馆路线候选选择与讲解模块。"
    "必须遵守 task 中不可变的 final_goal 和 intermediate_policy，并依据 task_state 判断当前阶段。"
    "只能从给定 route_candidates 中选择一个 route_id，并返回 route_id 与 narration。"
    "narration 应使用自然语言说明当前展品与最终目标的联系。"
    "不得创建节点或路线，不得输出 gain、reset、安全结论、clearance 或执行方向。"
)
ROUTE_SELECTION_JSON_SCHEMA: dict[str, Any] = {
    "type": "object",
    "additionalProperties": False,
    "properties": {
        "route_id": {"type": "string", "minLength": 1},
        "narration": {"type": "string", "minLength": 1, "maxLength": 200},
    },
    "required": ["route_id", "narration"],
}


class RouteSelectionDraft(BaseModel):
    """模型唯一允许控制的两个字段。"""

    model_config = ConfigDict(extra="forbid")

    route_id: str = Field(min_length=1)
    narration: str = Field(min_length=1, max_length=200)

    @classmethod
    def parse_model_json(
        cls,
        content: str,
        request: RouteSelectionRequest,
    ) -> "RouteSelectionDraft":
        """解析严格 JSON 并执行不可关闭的 route membership 校验。"""

        try:
            payload = json.loads(content)
        except json.JSONDecodeError as exc:
            raise ValueError("Route selector output is not valid JSON.") from exc
        try:
            draft = cls.model_validate(payload)
        except ValidationError as exc:
            raise ValueError(f"Route selector schema validation failed: {exc}") from exc
        if request.route_for(draft.route_id) is None:
            raise ValueError("route_id must belong to frozen route_candidates.")
        return draft


class RouteSelectionGenerator(Protocol):
    """路线选择 Pipeline 依赖的最小异步接口。"""

    async def generate(self, request: RouteSelectionRequest) -> RouteSelectionDraft:
        """从冻结 Top-K 中选择 route_id。"""


class MLLMRouteSelectionGenerator:
    """通过现有 OpenAI-compatible provider 执行严格路线选择。"""

    def __init__(self, provider: MLLMProvider) -> None:
        self.provider = provider

    async def generate(self, request: RouteSelectionRequest) -> RouteSelectionDraft:
        context = {
            "task": request.task.model_dump(mode="json"),
            "task_state": request.task_state.model_dump(mode="json"),
            "route_candidates": [
                candidate.model_dump(mode="json")
                for candidate in request.route_candidates
            ],
        }
        content = await self.provider.complete_json(
            messages=[
                {"role": "system", "content": ROUTE_SELECTION_SYSTEM_PROMPT},
                {
                    "role": "user",
                    "content": json.dumps(context, ensure_ascii=False, sort_keys=True),
                },
            ],
            json_schema=ROUTE_SELECTION_JSON_SCHEMA,
            temperature=ROUTE_SELECTION_TEMPERATURE,
            max_tokens=ROUTE_SELECTION_MAX_TOKENS,
        )
        return RouteSelectionDraft.parse_model_json(content, request)


class DeterministicRouteSelectionGenerator:
    """仅用于显式 offline mock，按 Utility 和 route_id 稳定选择。"""

    async def generate(self, request: RouteSelectionRequest) -> RouteSelectionDraft:
        selected = sorted(
            request.route_candidates,
            key=lambda candidate: (-candidate.route_utility, candidate.route_id),
        )[0]
        narration = {
            "low": "我们沿这条路线继续参观。",
            "medium": "请留意前方展品，我们自然地转向下一站。",
            "high": "让我们先关注这个线索，再前往下一件重要展品。",
        }[selected.intervention_level]
        return RouteSelectionDraft(route_id=selected.route_id, narration=narration)


def create_route_selector_from_env(
    env: Mapping[str, str] | None = None,
) -> RouteSelectionGenerator:
    """显式创建正式 MLLM 或 offline mock 路线选择器。"""

    runtime_env = os.environ if env is None else env
    provider_name = runtime_env.get("MLLM_PROVIDER", "").strip().lower()
    if provider_name in {"mock", "offline_mock"}:
        return DeterministicRouteSelectionGenerator()
    if provider_name in {"qwen", "qwen_openai_compatible"}:
        return MLLMRouteSelectionGenerator(
            QwenOpenAICompatibleProvider(QwenProviderSettings.from_env(runtime_env))
        )
    if provider_name in {"vllm", "vllm_openai_compatible"}:
        return MLLMRouteSelectionGenerator(
            VLLMOpenAICompatibleProvider(VLLMProviderSettings.from_env(runtime_env))
        )
    raise MLLMConfigurationError(
        "MLLM_PROVIDER must explicitly configure a formal provider or offline mock."
    )


class RouteSelectionPipeline:
    """执行 route-only 模型调用和独立 membership verifier。"""

    def __init__(self, selector: RouteSelectionGenerator | None = None) -> None:
        self.selector = selector

    async def handle(self, request: RouteSelectionRequest) -> RouteSelectionResponse:
        try:
            if self.selector is None:
                self.selector = create_route_selector_from_env()
            draft = await self.selector.generate(request)
        except MLLMConfigurationError as exc:
            raise HTTPException(status_code=503, detail=str(exc)) from exc
        except MLLMTimeoutError as exc:
            raise HTTPException(status_code=504, detail=str(exc)) from exc
        except (MLLMHTTPError, MLLMResponseError) as exc:
            raise HTTPException(status_code=502, detail=str(exc)) from exc
        except ValueError as exc:
            raise HTTPException(
                status_code=502,
                detail=f"Route selection contract violation: {exc}",
            ) from exc

        if request.route_for(draft.route_id) is None:
            raise HTTPException(
                status_code=502,
                detail="Route selection verifier rejected route_id outside candidates.",
            )
        return RouteSelectionResponse(
            planning_schema_version=request.planning_schema_version,
            session_id=request.session_id,
            request_id=request.request_id,
            plan_id=request.plan_id,
            snapshot_id=request.snapshot_id,
            generation=request.generation,
            route_id=draft.route_id,
            narration=draft.narration,
            verifier=VerifierResult(
                approved=True,
                reason="route_id belongs to frozen route_candidates; physical safety remains Unity-owned.",
            ),
        )
