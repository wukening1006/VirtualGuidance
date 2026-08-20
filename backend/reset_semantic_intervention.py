from __future__ import annotations

import json
import os
from typing import Any, Mapping, Protocol

from fastapi import HTTPException
from pydantic import ValidationError

from backend.schemas import (
    ResetSemanticInterventionRequest,
    ResetSemanticInterventionResponse,
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
RESET_SEMANTIC_MAX_TOKENS = 256
RESET_SEMANTIC_SYSTEM_PROMPT = (
    "你是博物馆导览中的 Reset 语义干预模块。"
    "Reset 的触发、方向、角度和物理执行已经由 Unity 安全系统确定，你只能生成转身期间的叙事文本。"
    "根据当前展品、主题、下一目标、最近话术和 reset_angle 选择 low、medium 或 high。"
    "low 是一句轻量补充，medium 延伸当前展品背景，high 使用背景、悬念或自然过渡。"
    "不得输出方向、目标朝向、Reset 控制、Gain、Safety、Clearance 或任何物理执行建议。"
)
RESET_SEMANTIC_JSON_SCHEMA: dict[str, Any] = {
    "type": "object",
    "additionalProperties": False,
    "properties": {
        "intervention_level": {
            "type": "string",
            "enum": ["low", "medium", "high"],
        },
        "narration": {"type": "string", "minLength": 1, "maxLength": 500},
    },
    "required": ["intervention_level", "narration"],
}


def classify_reset_angle(angle: float) -> str:
    """按默认实验阈值映射语义强度；Unity 本地等级仍是最终控制依据。"""

    if angle >= 120.0:
        return "high"
    if angle >= 60.0:
        return "medium"
    return "low"


class ResetSemanticInterventionGenerator(Protocol):
    """Reset 语义生成器的最小异步接口。"""

    async def generate(
        self, request: ResetSemanticInterventionRequest
    ) -> ResetSemanticInterventionResponse:
        """生成 Reset 期间播放的语义文本。"""


class MLLMResetSemanticInterventionGenerator:
    """通过现有 OpenAI-compatible provider 生成严格两字段响应。"""

    def __init__(self, provider: MLLMProvider) -> None:
        self.provider = provider

    async def generate(
        self, request: ResetSemanticInterventionRequest
    ) -> ResetSemanticInterventionResponse:
        context = request.model_dump(mode="json")
        content = await self.provider.complete_json(
            messages=[
                {"role": "system", "content": RESET_SEMANTIC_SYSTEM_PROMPT},
                {
                    "role": "user",
                    "content": json.dumps(context, ensure_ascii=False, sort_keys=True),
                },
            ],
            json_schema=RESET_SEMANTIC_JSON_SCHEMA,
            temperature=RESET_SEMANTIC_TEMPERATURE,
            max_tokens=RESET_SEMANTIC_MAX_TOKENS,
        )
        try:
            return ResetSemanticInterventionResponse.model_validate(json.loads(content))
        except (json.JSONDecodeError, ValidationError) as exc:
            raise ValueError(f"Reset semantic response schema validation failed: {exc}") from exc


class DeterministicResetSemanticInterventionGenerator:
    """显式 offline mock；只按 Reset 角度选择叙事强度。"""

    async def generate(
        self, request: ResetSemanticInterventionRequest
    ) -> ResetSemanticInterventionResponse:
        level = classify_reset_angle(request.reset_angle)
        if level == "high":
            narration = (
                f"关于{request.current_exhibit}还有一个值得注意的问题："
                f"它与接下来看到的{request.next_target}之间，可能存在一条重要的文化线索。"
            )
        elif level == "medium":
            narration = (
                f"值得注意的是，{request.current_exhibit}的背景与当前主题密切相关，"
                f"这也为接下来了解{request.next_target}提供了线索。"
            )
        else:
            narration = f"{request.current_exhibit}的细节还体现了当前主题中的独特审美。"
        return ResetSemanticInterventionResponse(
            intervention_level=level,
            narration=narration,
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
    """处理旁路生成；所有失败都转为 HTTP 错误，由 Unity 回退普通 Reset。"""

    def __init__(
        self,
        generator: ResetSemanticInterventionGenerator | None = None,
    ) -> None:
        self.generator = generator

    async def handle(
        self, request: ResetSemanticInterventionRequest
    ) -> ResetSemanticInterventionResponse:
        try:
            if self.generator is None:
                self.generator = create_reset_semantic_generator_from_env()
            return await self.generator.generate(request)
        except MLLMConfigurationError as exc:
            raise HTTPException(status_code=503, detail=str(exc)) from exc
        except MLLMTimeoutError as exc:
            raise HTTPException(status_code=504, detail=str(exc)) from exc
        except (MLLMHTTPError, MLLMResponseError, ValueError, ValidationError) as exc:
            raise HTTPException(
                status_code=502,
                detail=f"Reset semantic intervention unavailable: {exc}",
            ) from exc
