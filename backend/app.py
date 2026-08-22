"""FastAPI 后端入口，负责接收 Unity 状态、运行导览 pipeline 并返回 guidance。"""

from __future__ import annotations

from pathlib import Path

from fastapi import FastAPI

from backend.agents.guide_llm import GuideDraftGenerator
from backend.pipeline import GuidancePipeline
from backend.route_selection import RouteSelectionGenerator, RouteSelectionPipeline
from backend.reset_semantic_intervention import (
    ResetSemanticInterventionGenerator,
    ResetSemanticInterventionPipeline,
)
from backend.semantic_planning import SemanticPlanGenerator, SemanticPlanPipeline
from backend.schemas import (
    GuidanceRequest,
    GuidanceResponse,
    HealthResponse,
    SemanticPlanRequest,
    SemanticPlanResponse,
    RouteSelectionRequest,
    RouteSelectionResponse,
    ResetSemanticInterventionRequest,
    ResetSemanticInterventionResponse,
)


VERSION = "0.3.0"
PROJECT_ROOT = Path(__file__).resolve().parents[1]
CONFIG_DIR = PROJECT_ROOT / "Assets" / "Configs"
DEFAULT_LOG_DIR = PROJECT_ROOT / "backend" / "logs"


def create_app(
    config_dir: Path = CONFIG_DIR,
    log_dir: Path = DEFAULT_LOG_DIR,
    draft_generator: GuideDraftGenerator | None = None,
    route_selector: RouteSelectionGenerator | None = None,
    semantic_planner: SemanticPlanGenerator | None = None,
    reset_semantic_generator: ResetSemanticInterventionGenerator | None = None,
) -> FastAPI:
    application = FastAPI(title="RDW-Grounded Virtual Guidance Backend", version=VERSION)
    guidance_pipeline = GuidancePipeline(
        config_dir=config_dir,
        log_dir=log_dir,
        guide=draft_generator,
    )
    application.state.pipeline = guidance_pipeline
    application.state.route_selection_pipeline = RouteSelectionPipeline(route_selector)
    application.state.semantic_plan_pipeline = SemanticPlanPipeline(semantic_planner)
    application.state.reset_semantic_pipeline = ResetSemanticInterventionPipeline(
        guidance_pipeline.scene_semantics,
        reset_semantic_generator,
    )

    @application.get("/health", response_model=HealthResponse)
    def health() -> HealthResponse:
        pipeline: GuidancePipeline = application.state.pipeline
        return HealthResponse(
            status="ok",
            version=VERSION,
            physical_spaces=pipeline.physical_spaces,
            scene_id=pipeline.scene_id,
            schema_version=pipeline.schema_version,
            exhibit_count=pipeline.exhibit_count,
            semantic_edge_count=pipeline.semantic_edge_count,
            semantic_ready=pipeline.semantic_ready,
        )

    @application.post("/api/guidance", response_model=GuidanceResponse)
    async def guidance(request: GuidanceRequest) -> GuidanceResponse:
        pipeline: GuidancePipeline = application.state.pipeline
        return await pipeline.handle(request)

    @application.post(
        "/api/planning/route-selection",
        response_model=RouteSelectionResponse,
    )
    async def route_selection(
        request: RouteSelectionRequest,
    ) -> RouteSelectionResponse:
        pipeline: RouteSelectionPipeline = application.state.route_selection_pipeline
        return await pipeline.handle(request)

    @application.post(
        "/api/planning/semantic-local",
        response_model=SemanticPlanResponse,
    )
    async def semantic_local_planning(
        request: SemanticPlanRequest,
    ) -> SemanticPlanResponse:
        pipeline: SemanticPlanPipeline = application.state.semantic_plan_pipeline
        return await pipeline.handle(request)

    @application.post(
        "/api/planning/reset-semantic-intervention",
        response_model=ResetSemanticInterventionResponse,
    )
    async def reset_semantic_intervention(
        request: ResetSemanticInterventionRequest,
    ) -> ResetSemanticInterventionResponse:
        pipeline: ResetSemanticInterventionPipeline = application.state.reset_semantic_pipeline
        return await pipeline.handle(request)

    return application


app = create_app()
