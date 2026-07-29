"""测试六条件 MLLM Context、严格 Draft Parser 与正式模型 HTTP 合同。"""

from __future__ import annotations

import asyncio
import json
from pathlib import Path
from typing import Any

import httpx
import pytest
from fastapi.testclient import TestClient

from backend.agents.guide_llm import (
    MLLM_DRAFT_JSON_SCHEMA,
    MLLM_MAX_TOKENS,
    MLLM_SYSTEM_PROMPT,
    MLLM_TEMPERATURE,
    ConditionContextBuilder,
    MLLMDraftGenerator,
    MLLMGuidanceDraft,
    create_draft_generator_from_env,
)
from backend.agents.guide_mock import GuideMockAgent
from backend.app import create_app
from backend.schemas import (
    Affordance,
    Condition,
    GuidanceRequest,
    MovementInstruction,
)
from backend.services.mllm_provider import (
    MLLMConfigurationError,
    MLLMHTTPError,
    MLLMResponseError,
    MLLMTimeoutError,
    QwenOpenAICompatibleProvider,
    QwenProviderSettings,
    VLLMOpenAICompatibleProvider,
    VLLMProviderSettings,
)


CONFIG_DIR = Path(__file__).resolve().parents[2] / "Assets" / "Configs"
VALID_MODEL_DRAFT = {
    "selected_target": "E3",
    "route_decision": "semantic_redirect",
    "narration": "在前往主要展品前，请先看看左侧展品。",
}


@pytest.fixture
def anyio_backend() -> str:
    return "asyncio"


def request_for(
    condition: str,
    *,
    risk_level: str = "high",
) -> GuidanceRequest:
    candidate_ids = ["E5", "E3", "E4"]
    safe_filter = condition in {
        Condition.B5_OURS_WITHOUT_VERIFIER.value,
        Condition.OURS_FULL.value,
    }
    model_candidate_ids = ["E3", "E4"] if safe_filter else candidate_ids
    allowed_candidate_ids = ["E3", "E4"]
    critical = risk_level == "critical"
    return GuidanceRequest.model_validate(
        {
            "task_definition_version": "t2-main-target-v2",
            "guidance_schema_version": "guidance-schema-v2",
            "candidate_policy_version": "t2-candidate-policy-v1",
            "risk_policy_version": "unity-risk-policy-v1",
            "session_id": "session-mllm-test",
            "calibration_id": "calibration-mllm-test",
            "request_id": f"request-{condition}",
            "decision_id": f"decision-{condition}",
            "candidate_set_id": f"candidates-{condition}",
            "state_version": 4,
            "risk_snapshot_id": "risk-mllm-test",
            "condition": condition,
            "task_id": "T2_main_target",
            "physical_space_id": "narrow_room",
            "current_exhibit": "E1",
            "risk_snapshot": {
                "source": "unity",
                "risk_snapshot_id": "risk-mllm-test",
                "risk_revision": 9,
                "calibration_id": "calibration-mllm-test",
                "sampled_at_utc": "2026-07-23T08:00:00Z",
                "risk_policy_version": "unity-risk-policy-v1",
                "physical_space": "narrow_room",
                "physical_position": [1.8, 4.3],
                "physical_forward": [0.0, 1.0],
                "boundary_context": {
                    "points": [[0.0, 0.0], [3.6, 0.0], [3.6, 5.0], [0.0, 5.0]]
                },
                "obstacle_context": [
                    {"id": "pillar", "center": [1.0, 2.0], "radius": 0.3}
                ],
                "clearance_forward": 0.7,
                "clearance_left": 1.8,
                "clearance_right": 0.8,
                "risk_level": risk_level,
                "safe_directions": ["left"],
                "unsafe_directions": ["forward", "right"],
                "critical": critical,
                "reset_required": critical,
            },
            "user_physical_position": [1.8, 4.3],
            "user_physical_forward": [0.0, 1.0],
            "participant_id": "P001",
            "user_task": "前往重点展品 E5。",
            "final_target": "E5",
            "preferred_task_target": "E5",
            "candidate_ids": candidate_ids,
            "model_candidate_ids": model_candidate_ids,
            "allowed_candidate_ids": allowed_candidate_ids,
            "semantic_candidates": [
                {
                    "id": target_id,
                    "target_role": (
                        "final_target" if target_id == "E5" else "navigable_detour"
                    ),
                    "relative_direction": {
                        "E5": "forward",
                        "E3": "left",
                        "E4": "right",
                    }[target_id],
                    "name": f"展品 {target_id}",
                    "description": "候选展品说明",
                    "semantic_role": "route_candidate",
                    "tags": ["museum"],
                    "related_exhibits": ["E1", "E5"],
                }
                for target_id in model_candidate_ids
            ],
        }
    )


def affordance() -> Affordance:
    return Affordance(
        intervention_type="semantic_redirection",
        preferred_direction="left",
        semantic_anchor="E3",
        physical_goal="avoid_forward_boundary",
        avoided_direction="forward",
    )


def provider_settings(**overrides: Any) -> VLLMProviderSettings:
    values = {
        "base_url": "http://127.0.0.1:8000",
        "model": "local-qwen",
        "api_key": "test-key",
        "timeout_seconds": 2.0,
    }
    values.update(overrides)
    return VLLMProviderSettings(**values)


def qwen_settings(**overrides: Any) -> QwenProviderSettings:
    values = {
        "base_url": "https://dashscope.aliyuncs.com/compatible-mode/v1",
        "model": "qwen3.5-omni-flash",
        "api_key": "test-qwen-key",
        "timeout_seconds": 10.0,
    }
    values.update(overrides)
    return QwenProviderSettings(**values)


def completion_response(content: str) -> httpx.Response:
    return httpx.Response(
        200,
        json={"choices": [{"message": {"role": "assistant", "content": content}}]},
    )


def qwen_stream_response(*content_parts: str) -> httpx.Response:
    events = [
        f"data: {json.dumps({'choices': [{'delta': {'content': part}}]})}"
        for part in content_parts
    ]
    events.extend(
        [
            'data: {"choices": [], "usage": {"total_tokens": 10}}',
            "data: [DONE]",
        ]
    )
    return httpx.Response(
        200,
        headers={"Content-Type": "text/event-stream"},
        content="\n\n".join(events).encode("utf-8"),
    )


def test_condition_context_snapshots_freeze_six_information_boundaries() -> None:
    builder = ConditionContextBuilder()
    contexts = {
        condition.value: builder.build(request_for(condition.value), affordance())
        for condition in Condition
    }
    base_keys = {"task", "navigation", "candidate_set"}

    assert set(contexts[Condition.B1_MLLM_ONLY.value]) == base_keys
    assert set(contexts[Condition.B2_RAW_PHYSICAL_DATA.value]) == base_keys | {
        "raw_physical_data"
    }
    assert set(contexts[Condition.B3_RISK_LABELS.value]) == base_keys | {"risk_labels"}
    assert set(contexts[Condition.B4_SCRIPTED_RESET.value]) == base_keys
    assert set(contexts[Condition.B5_OURS_WITHOUT_VERIFIER.value]) == base_keys | {
        "rdw_affordance",
        "allowed_candidate_ids",
    }
    assert contexts[Condition.B5_OURS_WITHOUT_VERIFIER.value] == contexts[
        Condition.OURS_FULL.value
    ]

    b1_json = json.dumps(contexts[Condition.B1_MLLM_ONLY.value], sort_keys=True)
    assert "physical_position" not in b1_json
    assert "risk_level" not in b1_json
    assert "allowed_candidate_ids" not in b1_json
    assert "related_exhibits" in b1_json

    raw = contexts[Condition.B2_RAW_PHYSICAL_DATA.value]["raw_physical_data"]
    assert raw["position"] == [1.8, 4.3]
    assert raw["clearance"] == {"forward": 0.7, "left": 1.8, "right": 0.8}
    assert "risk_level" not in raw

    labels = contexts[Condition.B3_RISK_LABELS.value]["risk_labels"]
    assert labels == {
        "risk_level": "high",
        "safe_directions": ["left"],
        "unsafe_directions": ["forward", "right"],
    }
    assert "allowed_candidate_ids" not in contexts[Condition.B3_RISK_LABELS.value]


def test_mllm_output_schema_contains_only_three_control_fields() -> None:
    assert set(MLLM_DRAFT_JSON_SCHEMA["properties"]) == {
        "selected_target",
        "route_decision",
        "narration",
    }
    assert set(MLLM_DRAFT_JSON_SCHEMA["required"]) == set(
        MLLM_DRAFT_JSON_SCHEMA["properties"]
    )
    # 方向权威归 Unity：模型控制面不得出现任何方向类字段。
    schema_json = json.dumps(MLLM_DRAFT_JSON_SCHEMA)
    assert "movement_instruction" not in schema_json
    assert "gaze_cue" not in schema_json
    assert "avatar_action" not in schema_json


@pytest.mark.parametrize(
    ("content", "expected_message"),
    [
        ("not-json", "not valid JSON"),
        (json.dumps({"selected_target": "E3"}), "schema validation failed"),
        (
            json.dumps({**VALID_MODEL_DRAFT, "avatar_action": "point_left"}),
            "schema validation failed",
        ),
        (
            json.dumps({**VALID_MODEL_DRAFT, "route_decision": "unknown_route"}),
            "schema validation failed",
        ),
        (
            json.dumps({**VALID_MODEL_DRAFT, "movement_instruction": "turn_right_slowly"}),
            "schema validation failed",
        ),
        (
            json.dumps({**VALID_MODEL_DRAFT, "selected_target": "E2"}),
            "frozen model_candidate_ids",
        ),
    ],
)
def test_guide_draft_parser_rejects_invalid_contracts(
    content: str,
    expected_message: str,
) -> None:
    request = request_for(Condition.OURS_FULL.value)

    with pytest.raises(ValueError, match=expected_message):
        MLLMGuidanceDraft.parse_model_json(content, request)


def test_invalid_json_error_includes_bounded_single_line_preview() -> None:
    request = request_for(Condition.OURS_FULL.value)
    content = "not-json\n" + ("x" * 300)

    with pytest.raises(ValueError) as error:
        MLLMGuidanceDraft.parse_model_json(content, request)

    message = str(error.value)
    assert "content preview='not-json " in message
    assert "\n" not in message
    assert "..." in message
    assert len(message) < 340


class RecordingProvider:
    def __init__(self, content: str | None = None) -> None:
        self.content = content or json.dumps(VALID_MODEL_DRAFT, ensure_ascii=False)
        self.calls: list[dict[str, Any]] = []

    async def complete_json(self, **kwargs: Any) -> str:
        self.calls.append(kwargs)
        return self.content


@pytest.mark.anyio
async def test_all_conditions_share_prompt_schema_and_generation_parameters() -> None:
    provider = RecordingProvider()
    generator = MLLMDraftGenerator(provider)

    for condition in Condition:
        request = request_for(condition.value, risk_level="low")
        if "E3" not in request.model_candidate_ids:
            raise AssertionError("Test request must expose E3 to every condition.")
        await generator.generate(request, _unused_risk_state(request), affordance())

    assert len(provider.calls) == 6
    for call in provider.calls:
        assert call["messages"][0] == {"role": "system", "content": MLLM_SYSTEM_PROMPT}
        assert call["json_schema"] == MLLM_DRAFT_JSON_SCHEMA
        assert call["temperature"] == MLLM_TEMPERATURE
        assert call["max_tokens"] == MLLM_MAX_TOKENS


@pytest.mark.anyio
async def test_generator_deterministically_enriches_model_three_field_draft() -> None:
    request = request_for(Condition.OURS_FULL.value)
    draft = await MLLMDraftGenerator(RecordingProvider()).generate(
        request,
        _unused_risk_state(request),
        affordance(),
    )

    assert draft.selected_target == "E3"
    assert draft.gaze_cue == "look_left"
    assert draft.gaze_target_id == "E3_GazeTarget"
    # E3 的 relative_direction 是 left，movement 必须由该方向唯一推导。
    assert draft.movement_instruction == MovementInstruction.TURN_LEFT_SLOWLY
    assert draft.physical_intent == "avoid_forward_boundary"
    assert draft.intervention_type == "semantic_redirection"


@pytest.mark.anyio
async def test_movement_follows_selected_target_direction_not_model_text() -> None:
    """回归：模型选中前方目标时不得再产生与目标方向冲突的转向指令。"""

    request = request_for(Condition.B1_MLLM_ONLY.value, risk_level="low")
    provider = RecordingProvider(
        json.dumps(
            {
                "selected_target": "E5",
                "route_decision": "direct_to_target",
                "narration": "我们现在将直接前往展厅中最重要展品。",
            },
            ensure_ascii=False,
        )
    )

    draft = await MLLMDraftGenerator(provider).generate(
        request,
        _unused_risk_state(request),
        affordance(),
    )

    assert request.candidate_for("E5").relative_direction == "forward"
    assert draft.movement_instruction == MovementInstruction.MOVE_FORWARD_SLOWLY
    assert draft.gaze_cue == "look_forward"


@pytest.mark.anyio
async def test_reset_required_forces_pause_instead_of_geometric_movement() -> None:
    """reset 阶段优先于几何推导，否则 ours_full 会被自身 Verifier 全量拒绝。"""

    request = request_for(Condition.OURS_FULL.value, risk_level="critical")
    risk_state = _unused_risk_state(request)
    assert risk_state.reset_required is True

    draft = await MLLMDraftGenerator(RecordingProvider()).generate(
        request,
        risk_state,
        affordance(),
    )

    assert draft.movement_instruction == MovementInstruction.PAUSE_AND_WAIT


def _unused_risk_state(request: GuidanceRequest):
    from backend.pipeline import risk_state_from_unity

    return risk_state_from_unity(request)


@pytest.mark.anyio
async def test_vllm_provider_sends_openai_compatible_structured_payload() -> None:
    captured: dict[str, Any] = {}

    def handler(request: httpx.Request) -> httpx.Response:
        captured["url"] = str(request.url)
        captured["authorization"] = request.headers.get("Authorization")
        captured["payload"] = json.loads(request.content)
        return completion_response(json.dumps(VALID_MODEL_DRAFT))

    provider = VLLMOpenAICompatibleProvider(
        provider_settings(base_url="http://model-host:9000/v1"),
        transport=httpx.MockTransport(handler),
    )
    content = await provider.complete_json(
        messages=[{"role": "system", "content": "same prompt"}],
        json_schema=MLLM_DRAFT_JSON_SCHEMA,
        temperature=0.0,
        max_tokens=512,
    )

    assert json.loads(content) == VALID_MODEL_DRAFT
    assert captured["url"] == "http://model-host:9000/v1/chat/completions"
    assert captured["authorization"] == "Bearer test-key"
    assert captured["payload"]["model"] == "local-qwen"
    assert captured["payload"]["temperature"] == 0.0
    assert captured["payload"]["max_tokens"] == 512
    assert captured["payload"]["response_format"]["json_schema"]["strict"] is True
    assert captured["payload"]["response_format"]["json_schema"]["schema"] == (
        MLLM_DRAFT_JSON_SCHEMA
    )


@pytest.mark.anyio
async def test_qwen_provider_sends_streaming_json_object_payload() -> None:
    captured: dict[str, Any] = {}
    expected_content = json.dumps(VALID_MODEL_DRAFT, ensure_ascii=False)

    def handler(request: httpx.Request) -> httpx.Response:
        captured["url"] = str(request.url)
        captured["authorization"] = request.headers.get("Authorization")
        captured["payload"] = json.loads(request.content)
        midpoint = len(expected_content) // 2
        return qwen_stream_response(
            expected_content[:midpoint],
            expected_content[midpoint:],
        )

    provider = QwenOpenAICompatibleProvider(
        qwen_settings(),
        transport=httpx.MockTransport(handler),
    )
    content = await provider.complete_json(
        messages=[{"role": "system", "content": "Return JSON."}],
        json_schema=MLLM_DRAFT_JSON_SCHEMA,
        temperature=0.0,
        max_tokens=MLLM_MAX_TOKENS,
    )

    assert json.loads(content) == VALID_MODEL_DRAFT
    assert captured["url"] == (
        "https://dashscope.aliyuncs.com/compatible-mode/v1/chat/completions"
    )
    assert captured["authorization"] == "Bearer test-qwen-key"
    payload = captured["payload"]
    assert payload["model"] == "qwen3.5-omni-flash"
    assert payload["temperature"] == 0.0
    assert payload["modalities"] == ["text"]
    assert payload["stream"] is True
    assert payload["enable_thinking"] is False
    assert payload["response_format"] == {"type": "json_object"}
    assert "max_tokens" not in payload
    assert payload["messages"][0]["content"].startswith("Return JSON.")
    assert "不要输出 Markdown" in payload["messages"][0]["content"]
    assert json.dumps(
        MLLM_DRAFT_JSON_SCHEMA,
        ensure_ascii=False,
        sort_keys=True,
    ) in payload["messages"][0]["content"]


@pytest.mark.anyio
async def test_qwen_provider_unwraps_complete_json_code_fence() -> None:
    expected_content = json.dumps(VALID_MODEL_DRAFT, ensure_ascii=False)
    provider = QwenOpenAICompatibleProvider(
        qwen_settings(),
        transport=httpx.MockTransport(
            lambda request: qwen_stream_response(
                "```json\n",
                expected_content,
                "\n```",
            )
        ),
    )

    content = await provider.complete_json(
        messages=[],
        json_schema=MLLM_DRAFT_JSON_SCHEMA,
        temperature=0.0,
        max_tokens=MLLM_MAX_TOKENS,
    )

    assert json.loads(content) == VALID_MODEL_DRAFT


@pytest.mark.parametrize(
    "wrapped_content",
    [
        "```json\n{json}",
        "{json}\n```",
    ],
)
def test_qwen_provider_unwraps_single_boundary_fence_when_json_is_valid(
    wrapped_content: str,
) -> None:
    expected_content = json.dumps(VALID_MODEL_DRAFT, ensure_ascii=False)

    content = QwenOpenAICompatibleProvider._unwrap_json_code_fence(
        wrapped_content.format(json=expected_content)
    )

    assert json.loads(content) == VALID_MODEL_DRAFT


def test_qwen_provider_does_not_extract_json_from_explanatory_text() -> None:
    content = '模型输出如下： {"selected_target": "E3"}'

    assert QwenOpenAICompatibleProvider._unwrap_json_code_fence(content) == content


@pytest.mark.anyio
async def test_qwen_provider_rejects_invalid_sse_json() -> None:
    provider = QwenOpenAICompatibleProvider(
        qwen_settings(),
        transport=httpx.MockTransport(
            lambda request: httpx.Response(
                200,
                headers={"Content-Type": "text/event-stream"},
                content=b"data: not-json\n\n",
            )
        ),
    )

    with pytest.raises(MLLMResponseError, match="invalid JSON"):
        await provider.complete_json(
            messages=[], json_schema={}, temperature=0.0, max_tokens=1
        )


@pytest.mark.anyio
async def test_qwen_provider_rejects_stream_without_content() -> None:
    provider = QwenOpenAICompatibleProvider(
        qwen_settings(),
        transport=httpx.MockTransport(
            lambda request: qwen_stream_response()
        ),
    )

    with pytest.raises(MLLMResponseError, match="missing choices"):
        await provider.complete_json(
            messages=[], json_schema={}, temperature=0.0, max_tokens=1
        )


@pytest.mark.anyio
async def test_qwen_provider_maps_http_error() -> None:
    provider = QwenOpenAICompatibleProvider(
        qwen_settings(),
        transport=httpx.MockTransport(
            lambda request: httpx.Response(429, json={"error": "rate limited"})
        ),
    )

    with pytest.raises(MLLMHTTPError, match="HTTP 429"):
        await provider.complete_json(
            messages=[], json_schema={}, temperature=0.0, max_tokens=1
        )


@pytest.mark.anyio
async def test_qwen_provider_maps_timeout_without_retry() -> None:
    call_count = 0

    def handler(request: httpx.Request) -> httpx.Response:
        nonlocal call_count
        call_count += 1
        raise httpx.ReadTimeout("slow Qwen API", request=request)

    provider = QwenOpenAICompatibleProvider(
        qwen_settings(timeout_seconds=0.5),
        transport=httpx.MockTransport(handler),
    )

    with pytest.raises(MLLMTimeoutError, match="0.5 seconds"):
        await provider.complete_json(
            messages=[], json_schema={}, temperature=0.0, max_tokens=1
        )
    assert call_count == 1


@pytest.mark.anyio
async def test_vllm_provider_rejects_invalid_http_json() -> None:
    provider = VLLMOpenAICompatibleProvider(
        provider_settings(),
        transport=httpx.MockTransport(
            lambda request: httpx.Response(200, content=b"not-json")
        ),
    )

    with pytest.raises(MLLMResponseError, match="not valid JSON"):
        await provider.complete_json(
            messages=[], json_schema={}, temperature=0.0, max_tokens=1
        )


@pytest.mark.anyio
async def test_vllm_provider_rejects_missing_chat_completion_fields() -> None:
    provider = VLLMOpenAICompatibleProvider(
        provider_settings(),
        transport=httpx.MockTransport(
            lambda request: httpx.Response(200, json={"choices": []})
        ),
    )

    with pytest.raises(MLLMResponseError, match="missing choices"):
        await provider.complete_json(
            messages=[], json_schema={}, temperature=0.0, max_tokens=1
        )


@pytest.mark.anyio
async def test_vllm_provider_maps_timeout_without_retry() -> None:
    call_count = 0

    def handler(request: httpx.Request) -> httpx.Response:
        nonlocal call_count
        call_count += 1
        raise httpx.ReadTimeout("slow model", request=request)

    provider = VLLMOpenAICompatibleProvider(
        provider_settings(timeout_seconds=0.25),
        transport=httpx.MockTransport(handler),
    )

    with pytest.raises(MLLMTimeoutError, match="0.25 seconds"):
        await provider.complete_json(
            messages=[], json_schema={}, temperature=0.0, max_tokens=1
        )
    assert call_count == 1


@pytest.mark.anyio
async def test_vllm_provider_maps_http_error() -> None:
    provider = VLLMOpenAICompatibleProvider(
        provider_settings(),
        transport=httpx.MockTransport(
            lambda request: httpx.Response(429, json={"error": "too many requests"})
        ),
    )

    with pytest.raises(MLLMHTTPError, match="HTTP 429"):
        await provider.complete_json(
            messages=[], json_schema={}, temperature=0.0, max_tokens=1
        )


class BlockingTransport(httpx.AsyncBaseTransport):
    def __init__(self) -> None:
        self.started = asyncio.Event()

    async def handle_async_request(self, request: httpx.Request) -> httpx.Response:
        self.started.set()
        await asyncio.Event().wait()
        raise AssertionError("Unreachable after cancellation.")


@pytest.mark.anyio
async def test_vllm_provider_preserves_task_cancellation() -> None:
    transport = BlockingTransport()
    provider = VLLMOpenAICompatibleProvider(provider_settings(), transport=transport)
    task = asyncio.create_task(
        provider.complete_json(
            messages=[], json_schema={}, temperature=0.0, max_tokens=1
        )
    )
    await transport.started.wait()

    task.cancel()

    with pytest.raises(asyncio.CancelledError):
        await task


def test_vllm_settings_read_only_runtime_environment() -> None:
    settings = VLLMProviderSettings.from_env(
        {
            "MLLM_BASE_URL": "http://model-host:8000/v1/chat/completions/",
            "MLLM_MODEL": "Qwen2.5-VL-runtime-id",
            "MLLM_API_KEY": "",
            "MLLM_TIMEOUT_SECONDS": "12.5",
        }
    )

    assert settings.chat_completions_url == "http://model-host:8000/v1/chat/completions"
    assert settings.model == "Qwen2.5-VL-runtime-id"
    assert settings.api_key is None
    assert settings.timeout_seconds == 12.5


@pytest.mark.parametrize(
    "env",
    [
        {"MLLM_MODEL": "model"},
        {"MLLM_BASE_URL": "http://model"},
        {
            "MLLM_BASE_URL": "http://model",
            "MLLM_MODEL": "model",
            "MLLM_TIMEOUT_SECONDS": "zero",
        },
    ],
)
def test_vllm_settings_reject_missing_or_invalid_values(env: dict[str, str]) -> None:
    with pytest.raises(MLLMConfigurationError):
        VLLMProviderSettings.from_env(env)


def test_qwen_settings_require_api_key() -> None:
    with pytest.raises(MLLMConfigurationError, match="MLLM_API_KEY"):
        QwenProviderSettings.from_env(
            {
                "MLLM_BASE_URL": (
                    "https://dashscope.aliyuncs.com/compatible-mode/v1"
                ),
                "MLLM_MODEL": "qwen3.5-omni-flash",
            }
        )


def test_factory_selects_qwen_openai_compatible_provider() -> None:
    generator = create_draft_generator_from_env(
        {
            "MLLM_PROVIDER": "qwen_openai_compatible",
            "MLLM_BASE_URL": "https://dashscope.aliyuncs.com/compatible-mode/v1",
            "MLLM_MODEL": "qwen3.5-omni-flash",
            "MLLM_API_KEY": "test-qwen-key",
        }
    )

    assert isinstance(generator, MLLMDraftGenerator)
    assert isinstance(generator.provider, QwenOpenAICompatibleProvider)


def test_factory_allows_only_explicit_offline_mock() -> None:
    generator = create_draft_generator_from_env({"MLLM_PROVIDER": "mock"})

    assert isinstance(generator, GuideMockAgent)
    with pytest.raises(MLLMConfigurationError, match="MLLM_PROVIDER"):
        create_draft_generator_from_env({})


class NeverCalledGenerator:
    def __init__(self) -> None:
        self.called = False

    async def generate(self, request, risk_state, current_affordance):
        self.called = True
        raise AssertionError("B4 risk phase must not call the MLLM generator.")


def test_b4_risk_phase_uses_scripted_reset_without_mllm(tmp_path: Path) -> None:
    generator = NeverCalledGenerator()
    app = create_app(
        config_dir=CONFIG_DIR,
        log_dir=tmp_path,
        draft_generator=generator,
    )
    request = request_for(Condition.B4_SCRIPTED_RESET.value)

    response = TestClient(app).post(
        "/api/guidance",
        json=request.model_dump(mode="json"),
    )

    assert response.status_code == 200
    assert response.json()["movement_instruction"] == "reset_required"
    assert generator.called is False


def test_formal_request_does_not_silently_fallback_to_mock(
    monkeypatch: pytest.MonkeyPatch,
    tmp_path: Path,
) -> None:
    for name in (
        "MLLM_PROVIDER",
        "MLLM_BASE_URL",
        "MLLM_MODEL",
        "MLLM_API_KEY",
        "MLLM_TIMEOUT_SECONDS",
    ):
        monkeypatch.delenv(name, raising=False)
    app = create_app(config_dir=CONFIG_DIR, log_dir=tmp_path)
    request = request_for(Condition.OURS_FULL.value)

    response = TestClient(app).post(
        "/api/guidance",
        json=request.model_dump(mode="json"),
    )

    assert response.status_code == 503
    assert "MLLM_PROVIDER" in response.json()["detail"]
    assert list(tmp_path.glob("guidance_*.jsonl")) == []
