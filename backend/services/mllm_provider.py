"""MLLM Provider 抽象及 OpenAI-compatible HTTP 实现。"""

from __future__ import annotations

from dataclasses import dataclass
import json
import re
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
    """模型服务返回非成功 HTTP 状态。"""


class MLLMResponseError(MLLMProviderError):
    """模型服务响应不符合 Chat Completions 协议。"""


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
class OpenAICompatibleProviderSettings:
    """OpenAI-compatible 模型服务的连接参数。"""

    base_url: str
    model: str
    api_key: str | None
    timeout_seconds: float

    @classmethod
    def _from_env(
        cls,
        env: Mapping[str, str],
        *,
        provider_name: str,
        require_api_key: bool,
    ) -> "OpenAICompatibleProviderSettings":
        """读取并严格验证模型地址、名称、密钥与超时。"""

        base_url = env.get("MLLM_BASE_URL", "").strip()
        model = env.get("MLLM_MODEL", "").strip()
        if not base_url:
            raise MLLMConfigurationError(
                f"MLLM_BASE_URL is required for the {provider_name} provider."
            )
        if not model:
            raise MLLMConfigurationError(
                f"MLLM_MODEL is required for the {provider_name} provider."
            )

        raw_timeout = env.get("MLLM_TIMEOUT_SECONDS", "30").strip()
        try:
            timeout_seconds = float(raw_timeout)
        except ValueError as exc:
            raise MLLMConfigurationError("MLLM_TIMEOUT_SECONDS must be numeric.") from exc
        if timeout_seconds <= 0:
            raise MLLMConfigurationError("MLLM_TIMEOUT_SECONDS must be greater than zero.")

        api_key = env.get("MLLM_API_KEY", "").strip() or None
        if require_api_key and api_key is None:
            raise MLLMConfigurationError(
                f"MLLM_API_KEY is required for the {provider_name} provider."
            )
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


@dataclass(frozen=True)
class VLLMProviderSettings(OpenAICompatibleProviderSettings):
    """从运行时环境注入的 vLLM 连接参数。"""

    @classmethod
    def from_env(cls, env: Mapping[str, str]) -> "VLLMProviderSettings":
        """读取 vLLM 连接参数，允许本地服务不配置 API Key。"""

        return cls._from_env(
            env,
            provider_name="vLLM",
            require_api_key=False,
        )


@dataclass(frozen=True)
class QwenProviderSettings(OpenAICompatibleProviderSettings):
    """从运行时环境注入的千问 API 连接参数。"""

    @classmethod
    def from_env(cls, env: Mapping[str, str]) -> "QwenProviderSettings":
        """读取千问连接参数，正式调用必须配置 API Key。"""

        return cls._from_env(
            env,
            provider_name="Qwen",
            require_api_key=True,
        )


@dataclass(frozen=True)
class DeepSeekProviderSettings(OpenAICompatibleProviderSettings):
    """从运行时环境注入的 DeepSeek API 连接参数。"""

    @classmethod
    def from_env(cls, env: Mapping[str, str]) -> "DeepSeekProviderSettings":
        """读取 DeepSeek 连接参数，正式调用必须配置 API Key。"""

        return cls._from_env(
            env,
            provider_name="DeepSeek",
            require_api_key=True,
        )

    @property
    def chat_completions_url(self) -> str:
        """按 DeepSeek 官方端点拼接 Chat Completions URL。"""

        normalized = self.base_url.rstrip("/")
        if normalized.endswith("/chat/completions"):
            return normalized
        return f"{normalized}/chat/completions"


class _ChatMessage(BaseModel):
    model_config = ConfigDict(extra="ignore")

    content: str = Field(min_length=1)


class _ChatChoice(BaseModel):
    model_config = ConfigDict(extra="ignore")

    message: _ChatMessage


class _ChatCompletion(BaseModel):
    model_config = ConfigDict(extra="ignore")

    choices: list[_ChatChoice] = Field(min_length=1)


class _ChatDelta(BaseModel):
    model_config = ConfigDict(extra="ignore")

    content: str | None = None


class _ChatChunkChoice(BaseModel):
    model_config = ConfigDict(extra="ignore")

    delta: _ChatDelta


class _ChatCompletionChunk(BaseModel):
    model_config = ConfigDict(extra="ignore")

    choices: list[_ChatChunkChoice]


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


class QwenOpenAICompatibleProvider:
    """通过千问 OpenAI-compatible 流式接口生成结构化文本。"""

    def __init__(
        self,
        settings: QwenProviderSettings,
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
        # 千问 JSON Object 模式只保证合法 JSON，具体 Schema 由上层统一校验。
        del max_tokens
        payload = {
            "model": self.settings.model,
            "messages": self._with_json_schema_instruction(messages, json_schema),
            "temperature": temperature,
            "modalities": ["text"],
            "stream": True,
            "enable_thinking": False,
            "response_format": {"type": "json_object"},
        }
        headers = {
            "Content-Type": "application/json",
            "Authorization": f"Bearer {self.settings.api_key}",
        }

        content_parts: list[str] = []
        try:
            async with httpx.AsyncClient(
                timeout=self.settings.timeout_seconds,
                transport=self.transport,
            ) as client:
                async with client.stream(
                    "POST",
                    self.settings.chat_completions_url,
                    headers=headers,
                    json=payload,
                ) as response:
                    response.raise_for_status()
                    async for line in response.aiter_lines():
                        chunk_content = self._parse_sse_line(line)
                        if chunk_content is not None:
                            content_parts.append(chunk_content)
        except httpx.TimeoutException as exc:
            raise MLLMTimeoutError(
                f"Qwen request exceeded {self.settings.timeout_seconds:g} seconds."
            ) from exc
        except httpx.HTTPStatusError as exc:
            raise MLLMHTTPError(
                f"Qwen returned HTTP {exc.response.status_code}."
            ) from exc
        except httpx.HTTPError as exc:
            raise MLLMHTTPError(f"Qwen transport failed: {exc}.") from exc

        content = "".join(content_parts)
        if not content:
            raise MLLMResponseError(
                "Qwen stream is missing choices[0].delta.content."
            )
        return self._unwrap_json_code_fence(content)

    @staticmethod
    def _with_json_schema_instruction(
        messages: list[dict[str, str]],
        json_schema: dict[str, Any],
    ) -> list[dict[str, str]]:
        """把 JSON Object 模式无法表达的精确 Schema 注入系统指令。"""

        schema_text = json.dumps(json_schema, ensure_ascii=False, sort_keys=True)
        instruction = (
            "\n输出必须是且只能是一个符合下列 JSON Schema 的 JSON 对象；"
            "不要输出 Markdown、代码块、解释或其他文本：\n"
            f"{schema_text}"
        )
        augmented_messages = [dict(message) for message in messages]
        for index, message in enumerate(augmented_messages):
            if message.get("role") == "system":
                augmented_messages[index]["content"] = (
                    message.get("content", "") + instruction
                )
                break
        else:
            augmented_messages.insert(
                0,
                {"role": "system", "content": instruction.lstrip()},
            )
        return augmented_messages

    @staticmethod
    def _unwrap_json_code_fence(content: str) -> str:
        """仅移除边界处的 JSON Markdown 标记，并验证结果是单一对象。"""

        stripped = content.strip().lstrip("\ufeff")
        fenced = re.fullmatch(
            r"```(?:json)?\s*(.*?)\s*```",
            stripped,
            flags=re.IGNORECASE | re.DOTALL,
        )
        candidates = [stripped]
        if fenced:
            candidates.append(fenced.group(1).strip())
        candidates.extend(
            [
                re.sub(
                    r"^```(?:json)?\s*",
                    "",
                    stripped,
                    count=1,
                    flags=re.IGNORECASE,
                ).strip(),
                re.sub(r"\s*```$", "", stripped, count=1).strip(),
            ]
        )

        for candidate in dict.fromkeys(candidates):
            try:
                parsed = json.loads(candidate)
            except json.JSONDecodeError:
                continue
            if isinstance(parsed, dict):
                return candidate
        return stripped

    @staticmethod
    def _parse_sse_line(line: str) -> str | None:
        """解析单行 SSE，忽略心跳、结束标记和仅包含 usage 的帧。"""

        stripped = line.strip()
        if not stripped or not stripped.startswith("data:"):
            return None

        data = stripped.removeprefix("data:").strip()
        if not data or data == "[DONE]":
            return None

        try:
            chunk_data = json.loads(data)
        except json.JSONDecodeError as exc:
            raise MLLMResponseError(
                "Qwen stream contains invalid JSON data."
            ) from exc

        try:
            chunk = _ChatCompletionChunk.model_validate(chunk_data)
        except ValidationError as exc:
            raise MLLMResponseError(
                "Qwen stream chunk is missing choices."
            ) from exc

        if not chunk.choices:
            return None
        return chunk.choices[0].delta.content


class DeepSeekOpenAICompatibleProvider:
    """通过 DeepSeek OpenAI-compatible 接口生成结构化文本。"""

    def __init__(
        self,
        settings: DeepSeekProviderSettings,
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
        # DeepSeek JSON Object 模式只保证合法 JSON，具体 Schema 由上层统一校验。
        payload = {
            "model": self.settings.model,
            "messages": QwenOpenAICompatibleProvider._with_json_schema_instruction(
                messages,
                json_schema,
            ),
            "temperature": temperature,
            "max_tokens": max_tokens,
            "stream": False,
            "response_format": {"type": "json_object"},
        }
        headers = {
            "Content-Type": "application/json",
            "Authorization": f"Bearer {self.settings.api_key}",
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
                f"DeepSeek request exceeded {self.settings.timeout_seconds:g} seconds."
            ) from exc
        except httpx.HTTPStatusError as exc:
            raise MLLMHTTPError(
                f"DeepSeek returned HTTP {exc.response.status_code}."
            ) from exc
        except httpx.HTTPError as exc:
            raise MLLMHTTPError(f"DeepSeek transport failed: {exc}.") from exc

        try:
            response_data = response.json()
        except (json.JSONDecodeError, ValueError) as exc:
            raise MLLMResponseError(
                "DeepSeek response body is not valid JSON."
            ) from exc

        try:
            completion = _ChatCompletion.model_validate(response_data)
        except ValidationError as exc:
            raise MLLMResponseError(
                "DeepSeek response is missing choices[0].message.content."
            ) from exc
        return QwenOpenAICompatibleProvider._unwrap_json_code_fence(
            completion.choices[0].message.content
        )
