"""FastAPI 后端入口，负责接收 Unity 状态、运行导览 pipeline 并返回 guidance。"""

from __future__ import annotations

import json
from pathlib import Path

from fastapi import FastAPI, HTTPException

from backend.agents.guide_mock import GuideMockAgent
from backend.agents.logger import GuidanceLogger
from backend.agents.rdw_affordance import RDWAffordanceAgent, infer_semantic_target
from backend.agents.rdw_state import RDWStateAgent
from backend.agents.safety_verifier import SafetyVerifierAgent
from backend.schemas import (
    GuidanceRequest,
    GuidanceResponse,
    HealthResponse,
    response_from_draft,
)


VERSION = "0.3.0"
PROJECT_ROOT = Path(__file__).resolve().parents[1]
CONFIG_DIR = PROJECT_ROOT / "Assets" / "Configs"
DEFAULT_LOG_DIR = PROJECT_ROOT / "backend" / "logs"


class GuidancePipeline:
    """确定性 MVP agent pipeline 的轻量编排层。"""

    def __init__(self, config_dir: Path, log_dir: Path) -> None:
        self.config_dir = config_dir
        self.rdw_state = RDWStateAgent.from_config_dir(config_dir)
        self.affordance = RDWAffordanceAgent()
        self.guide = GuideMockAgent()
        self.verifier = SafetyVerifierAgent()
        self.logger = GuidanceLogger(log_dir)
        self.scene_semantics = load_scene_semantics(config_dir)

    @property
    def physical_spaces(self) -> list[str]:
        return sorted(self.rdw_state.spaces.keys())

    @property
    def exhibit_count(self) -> int:
        return len(self.scene_semantics.get("exhibits", []))

    def handle(self, request: GuidanceRequest) -> GuidanceResponse:
        try:
            # 先由 RDW 规则模块计算安全状态，避免导览生成器从语言上下文里“猜”安全标签。
            risk_state = self.rdw_state.evaluate(
                request.physical_space_id,
                request.physical_position_xz,
                request.physical_forward_xz,
            )
        except ValueError as exc:
            raise HTTPException(status_code=400, detail=str(exc)) from exc

        # 保持 pipeline 顺序清晰：state -> affordance -> guide -> verifier -> JSONL log。
        # 所有 baseline 和 proposed condition 使用同一日志 schema，方便后续指标比较。
        semantic_next_target = infer_semantic_target(request)
        affordance = self.affordance.build(request, risk_state)
        draft = self.guide.build(request, risk_state, affordance)
        verifier = self.verifier.verify(request.condition, draft, risk_state)
        self.logger.write(request, risk_state, draft, verifier, semantic_next_target)
        return response_from_draft(draft, verifier)


def create_app(
    config_dir: Path = CONFIG_DIR,
    log_dir: Path = DEFAULT_LOG_DIR,
) -> FastAPI:
    application = FastAPI(title="RDW-Grounded Virtual Guidance Backend", version=VERSION)
    application.state.pipeline = GuidancePipeline(config_dir=config_dir, log_dir=log_dir)

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
    def guidance(request: GuidanceRequest) -> GuidanceResponse:
        pipeline: GuidancePipeline = application.state.pipeline
        return pipeline.handle(request)

    return application


def load_scene_semantics(config_dir: Path) -> dict:
    """加载可选展品语义信息，用于健康检查和 demo 自检。"""

    path = config_dir / "scene_semantics.json"
    if not path.exists():
        return {"exhibits": []}
    with path.open("r", encoding="utf-8") as handle:
        return json.load(handle)


app = create_app()
