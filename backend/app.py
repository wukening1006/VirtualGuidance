"""FastAPI 后端入口，负责接收 Unity 状态、运行导览 pipeline 并返回 guidance。"""

from __future__ import annotations

from pathlib import Path

from fastapi import FastAPI

from backend.agents.guide_llm import GuideDraftGenerator
from backend.pipeline import GuidancePipeline
from backend.schemas import (
    GuidanceRequest,
    GuidanceResponse,
    HealthResponse,
)


VERSION = "0.3.0"
PROJECT_ROOT = Path(__file__).resolve().parents[1]
CONFIG_DIR = PROJECT_ROOT / "Assets" / "Configs"
DEFAULT_LOG_DIR = PROJECT_ROOT / "backend" / "logs"


def create_app(
    config_dir: Path = CONFIG_DIR,
    log_dir: Path = DEFAULT_LOG_DIR,
    draft_generator: GuideDraftGenerator | None = None,
) -> FastAPI:
    application = FastAPI(title="RDW-Grounded Virtual Guidance Backend", version=VERSION)
    application.state.pipeline = GuidancePipeline(
        config_dir=config_dir,
        log_dir=log_dir,
        guide=draft_generator,
    )

    @application.get("/health", response_model=HealthResponse)
    def health() -> HealthResponse:
        pipeline: GuidancePipeline = application.state.pipeline
        return HealthResponse(
            status="ok",
            version=VERSION,
            physical_spaces=pipeline.physical_spaces,
            exhibit_count=pipeline.exhibit_count,
        )

    @application.post("/api/guidance", response_model=GuidanceResponse)
    async def guidance(request: GuidanceRequest) -> GuidanceResponse:
        pipeline: GuidancePipeline = application.state.pipeline
        return await pipeline.handle(request)

    return application


app = create_app()
