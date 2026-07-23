"""MLLM Provider 抽象与 vLLM OpenAI-compatible HTTP 实现。"""

from __future__ import annotations

from dataclasses import dataclass
import json
from typing import Any, Mapping, Protocol

import httpx
from pydantic import BaseModel, ConfigDict, Field, ValidationError


class MLLMProviderError(RuntimeError):
    """所有可预期 MLLM Provider 失败的基类。"""


class MLLMConfigurationError(MLLMProviderError):
    """正式模型配置缺失或非法。"""


class MLLMTimeoutError(MLLMProviderError):
    """模型请求超过统一超时。"""


class MLLMHTTPError(MLLMProviderError):
    """vLLM Server 返回非成功 HTTP 状态。"""


class MLLMResponseError(MLLMProviderError):
    """vLLM Server 响应不符合 Chat Completions 协议。"""


class MLLMProvider(Protocol):
    """Provider-neutral 的最小文本结构化生成接口。"""

    async def complete_json(
        self,
        *,
        messages: list[dict[str, str]],
        json_schema: dict[str, Any],
        temperature: float,
        max_tokens: int,
    ) -> str:
        """返回模型 message.content 中的原始 JSON 文本。"""


@dataclass(frozen=True)
class VLLMProviderSettings:
    """从运行时环境注入的 vLLM 连接参数。"""

    base_url: str
    model: str
    api_key: str | None
    timeout_seconds: float

    @classmethod
    def from_env(cls, env: Mapping[str, str]) -> "VLLMProviderSettings":
        """读取并严格验证 MLLM_BASE_URL、MODEL、API_KEY 与 TIMEOUT。"""

        base_url = env.get("MLLM_BASE_URL", "").strip()
        model = env.get("MLLM_MODEL", "").strip()
        if not base_url:
            raise MLLMConfigurationError("MLLM_BASE_URL is required for the vLLM provider.")
        if not model:
            raise MLLMConfigurationError("MLLM_MODEL is required for the vLLM provider.")

        raw_timeout = env.get("MLLM_TIMEOUT_SECONDS", "30").strip()
        try:
            timeout_seconds = float(raw_timeout)
        except ValueError as exc:
            raise MLLMConfigurationError("MLLM_TIMEOUT_SECONDS must be numeric.") from exc
        if timeout_seconds <= 0:
            raise MLLMConfigurationError("MLLM_TIMEOUT_SECONDS must be greater than zero.")

        api_key = env.get("MLLM_API_KEY", "").strip() or None
        return cls(
            base_url=base_url,
            model=model,
            api_key=api_key,
            timeout_seconds=timeout_seconds,
        )

    @property
    def chat_completions_url(self) -> str:
        """把 server root、/v1 或完整 endpoint 统一为 Chat Completions URL。"""

        normalized = self.base_url.rstrip("/")
        if normalized.endswith("/v1/chat/completions"):
            return normalized
        if normalized.endswith("/v1"):
            return f"{normalized}/chat/completions"
        return f"{normalized}/v1/chat/completions"


class _ChatMessage(BaseModel):
    model_config = ConfigDict(extra="ignore")

    content: str = Field(min_length=1)


class _ChatChoice(BaseModel):
    model_config = ConfigDict(extra="ignore")

    message: _ChatMessage


class _ChatCompletion(BaseModel):
    model_config = ConfigDict(extra="ignore")

    choices: list[_ChatChoice] = Field(min_length=1)


class VLLMOpenAICompatibleProvider:
    """通过 OpenAI-compatible Chat Completions 调用 vLLM。"""

    def __init__(
        self,
        settings: VLLMProviderSettings,
        transport: httpx.AsyncBaseTransport | None = None,
    ) -> None:
        self.settings = settings
        self.transport = transport

    async def complete_json(
        self,
        *,
        messages: list[dict[str, str]],
        json_schema: dict[str, Any],
        temperature: float,
        max_tokens: int,
    ) -> str:
        headers = {"Content-Type": "application/json"}
        if self.settings.api_key:
            headers["Authorization"] = f"Bearer {self.settings.api_key}"

        payload = {
            "model": self.settings.model,
            "messages": messages,
            "temperature": temperature,
            "max_tokens": max_tokens,
            "response_format": {
                "type": "json_schema",
                "json_schema": {
                    "name": "guidance_draft",
                    "strict": True,
                    "schema": json_schema,
                },
            },
        }

        try:
            async with httpx.AsyncClient(
                timeout=self.settings.timeout_seconds,
                transport=self.transport,
            ) as client:
                response = await client.post(
                    self.settings.chat_completions_url,
                    headers=headers,
                    json=payload,
                )
                response.raise_for_status()
        except httpx.TimeoutException as exc:
            raise MLLMTimeoutError(
                f"vLLM request exceeded {self.settings.timeout_seconds:g} seconds."
            ) from exc
        except httpx.HTTPStatusError as exc:
            raise MLLMHTTPError(
                f"vLLM returned HTTP {exc.response.status_code}."
            ) from exc
        except httpx.HTTPError as exc:
            raise MLLMHTTPError(f"vLLM transport failed: {exc}.") from exc

        try:
            response_data = response.json()
        except (json.JSONDecodeError, ValueError) as exc:
            raise MLLMResponseError("vLLM response body is not valid JSON.") from exc

        try:
            completion = _ChatCompletion.model_validate(response_data)
        except ValidationError as exc:
            raise MLLMResponseError(
                "vLLM response is missing choices[0].message.content."
            ) from exc
        return completion.choices[0].message.content
