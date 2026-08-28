"""T2 Guidance Pipeline：编排权威风险、模型 Draft、验证与日志。"""

from __future__ import annotations

import json
from pathlib import Path

from fastapi import HTTPException

from backend.agents.guide_llm import GuideDraftGenerator, create_draft_generator_from_env
from backend.agents.rdw_affordance_agent import RDWAffordanceAgent
from backend.agents.safety_verifier_agent import SafetyVerifierAgent
from backend.schemas import (
    Condition,
    GuidanceRequest,
    GuidanceResponse,
    GuideDraft,
    MovementInstruction,
    PhysicalRiskState,
    RouteDecision,
    SceneSemanticsDocument,
    response_from_draft,
)
from backend.services.guidance_logger import GuidanceLogger
from backend.services.mllm_provider import (
    MLLMConfigurationError,
    MLLMHTTPError,
    MLLMResponseError,
    MLLMTimeoutError,
)


DEFAULT_SCENE_SEMANTICS_FILENAME = "scene_semantics.json"


class GuidancePipeline:
    """统一六条件运行链，在线物理风险只消费 Unity RiskSnapshot。"""

    def __init__(
        self,
        config_dir: Path,
        log_dir: Path,
        guide: GuideDraftGenerator | None = None,
        scene_semantics_filename: str = DEFAULT_SCENE_SEMANTICS_FILENAME,
    ) -> None:
        self.config_dir = config_dir
        self.affordance = RDWAffordanceAgent()
        self.guide = guide
        self.verifier = SafetyVerifierAgent()
        self.logger = GuidanceLogger(log_dir)
        self.scene_semantics = load_scene_semantics(config_dir, scene_semantics_filename)

    @property
    def physical_spaces(self) -> list[str]:
        return sorted(
            path.stem.removeprefix("physical_")
            for path in self.config_dir.glob("physical_*.json")
        )

    @property
    def exhibit_count(self) -> int:
        return len(self.scene_semantics.exhibits)

    @property
    def scene_id(self) -> str:
        return self.scene_semantics.scene_id

    @property
    def schema_version(self) -> int:
        return self.scene_semantics.schema_version

    @property
    def semantic_edge_count(self) -> int:
        return len(self.scene_semantics.semantic_edges)

    @property
    def semantic_ready(self) -> bool:
        return True

    async def handle(self, request: GuidanceRequest) -> GuidanceResponse:
        # 在线风险事实完全来自 Unity；Backend 不根据 boundary/obstacle 重新计算或覆盖结论。
        risk_state = risk_state_from_unity(request)

        affordance = self.affordance.build(request, risk_state)
        if should_script_reset(request):
            # B4 风险阶段必须在模型调用前确定性接管，禁止 Semantic Redirect。
            draft = scripted_reset_draft(request)
        else:
            try:
                if self.guide is None:
                    self.guide = create_draft_generator_from_env()
                draft = await self.guide.generate(request, risk_state, affordance)
            except MLLMConfigurationError as exc:
                raise HTTPException(status_code=503, detail=str(exc)) from exc
            except MLLMTimeoutError as exc:
                raise HTTPException(status_code=504, detail=str(exc)) from exc
            except (MLLMHTTPError, MLLMResponseError) as exc:
                raise HTTPException(status_code=502, detail=str(exc)) from exc
            except ValueError as exc:
                raise HTTPException(
                    status_code=502,
                    detail=f"MLLM draft contract violation: {exc}",
                ) from exc

        if draft.selected_target not in request.model_candidate_ids:
            raise HTTPException(
                status_code=502,
                detail=(
                    "Guidance draft contract violation: "
                    "selected_target must belong to model_candidate_ids."
                ),
            )
        verifier = self.verifier.verify(request.condition, draft, risk_state, request)
        self.logger.write(request, risk_state, draft, verifier)
        return response_from_draft(request, draft, verifier)


def risk_state_from_unity(request: GuidanceRequest) -> PhysicalRiskState:
    """把已校验 Unity RiskSnapshot 映射为只读 Pipeline 风险视图。"""

    snapshot = request.risk_snapshot
    return PhysicalRiskState(
        physical_space=snapshot.physical_space,
        user_physical_position=request.physical_position_xz,
        user_physical_forward=request.physical_forward_xz,
        boundary_risk=snapshot.risk_level,
        reset_risk="critical" if snapshot.reset_required else snapshot.risk_level,
        reset_required=snapshot.reset_required,
        risk_snapshot_id=snapshot.risk_snapshot_id,
        risk_revision=snapshot.risk_revision,
        calibration_id=snapshot.calibration_id,
        sampled_at_utc=snapshot.sampled_at_utc,
        risk_policy_version=snapshot.risk_policy_version,
        source=snapshot.source,
        risk_level=snapshot.risk_level,
        critical=snapshot.critical,
    )


def should_script_reset(request: GuidanceRequest) -> bool:
    """B4 在高危、Critical 或 Reset 状态下禁止调用 MLLM。"""

    snapshot = request.risk_snapshot
    return request.condition == Condition.B4_SCRIPTED_RESET.value and (
        snapshot.risk_level in {"high", "critical"}
        or snapshot.critical
        or snapshot.reset_required
    )


def scripted_reset_draft(request: GuidanceRequest) -> GuideDraft:
    """生成 B4 风险阶段唯一允许的确定性 Reset Draft。"""

    target = (
        request.preferred_task_target
        if request.preferred_task_target in request.model_candidate_ids
        else request.model_candidate_ids[0]
    )
    return GuideDraft(
        selected_target=target,
        route_decision=RouteDecision.REGUIDANCE_SAME_TARGET,
        narration="请在此停下，继续参观前需要重新校准活动空间。",
        gaze_cue="look_forward",
        gaze_target_id=f"{target}_GazeTarget",
        movement_instruction=MovementInstruction.RESET_REQUIRED,
        physical_intent="scripted_reset_on_high_or_critical_risk",
        intervention_type="scripted_reset",
        explicit_reset_triggered=True,
    )


def load_scene_semantics(
    config_dir: Path,
    filename: str = DEFAULT_SCENE_SEMANTICS_FILENAME,
) -> SceneSemanticsDocument:
    """从配置目录严格加载指定的 v4 场景语义文件。"""

    if (
        not isinstance(filename, str)
        or not filename
        or filename != filename.strip()
        or "/" in filename
        or "\\" in filename
        or Path(filename).name != filename
        or Path(filename).suffix.lower() != ".json"
    ):
        raise ValueError("scene semantics filename must be a .json basename")

    path = config_dir / filename
    if not path.is_file():
        raise FileNotFoundError(f"scene semantics file not found: {path}")
    # utf-8-sig 同时兼容带 BOM 和不带 BOM 的 UTF-8 配置文件。
    with path.open("r", encoding="utf-8-sig") as handle:
        try:
            return SceneSemanticsDocument.model_validate(json.load(handle))
        except ValueError as exc:
            raise ValueError(f"{filename} failed strict v4 validation: {exc}") from exc
