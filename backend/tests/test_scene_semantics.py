"""scene_semantics.json v4 strict schema and graph validation tests."""

from __future__ import annotations

from copy import deepcopy
import json
from pathlib import Path

import pytest
from pydantic import ValidationError

from backend.app import SCENE_SEMANTICS_FILE_ENV, create_app
from backend.pipeline import DEFAULT_SCENE_SEMANTICS_FILENAME, load_scene_semantics


CONFIG_PATH = Path(__file__).resolve().parents[2] / "Assets" / "Configs" / "scene_semantics.json"


@pytest.fixture
def valid_payload() -> dict:
    return json.loads(CONFIG_PATH.read_text(encoding="utf-8-sig"))


def write_payload(
    directory: Path,
    payload: dict,
    filename: str = DEFAULT_SCENE_SEMANTICS_FILENAME,
) -> None:
    (directory / filename).write_text(
        json.dumps(payload, ensure_ascii=False),
        encoding="utf-8",
    )


def test_valid_v4_loads_without_spatial_edges(tmp_path: Path, valid_payload: dict) -> None:
    write_payload(tmp_path, valid_payload)

    document = load_scene_semantics(tmp_path)

    assert document.schema_version == 4
    assert document.scene_id == "sanxingdui_gallery"
    assert len(document.themes) == 7
    assert len(document.exhibits) == 8
    assert len(document.semantic_edges) == 14
    assert {edge.evidence_status for edge in document.semantic_edges} == {
        "direct", "supported", "contextual", "interpretive"
    }


def test_missing_config_fails(tmp_path: Path) -> None:
    with pytest.raises(FileNotFoundError):
        load_scene_semantics(tmp_path)


def test_requested_semantics_filename_is_loaded(tmp_path: Path, valid_payload: dict) -> None:
    payload = deepcopy(valid_payload)
    payload["scene_name"] = "布局 01"
    write_payload(tmp_path, payload, "scene_semantics_layout_01.json")

    document = load_scene_semantics(tmp_path, "scene_semantics_layout_01.json")

    assert document.scene_name == "布局 01"


def test_app_uses_semantics_filename_from_environment(
    tmp_path: Path,
    valid_payload: dict,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    payload = deepcopy(valid_payload)
    payload["scene_name"] = "环境变量布局"
    write_payload(tmp_path, payload, "scene_semantics_layout_01.json")
    monkeypatch.setenv(SCENE_SEMANTICS_FILE_ENV, "scene_semantics_layout_01.json")

    application = create_app(config_dir=tmp_path, log_dir=tmp_path)

    assert application.state.pipeline.scene_semantics.scene_name == "环境变量布局"
    assert application.state.reset_semantic_pipeline.grounder.exhibits["E1"].name == payload["exhibits"][0]["name"]


@pytest.mark.parametrize(
    "filename",
    ["", " scene_semantics.json", "../scene_semantics.json", "..\\scene_semantics.json", "configs/scene_semantics.json", "scene_semantics.txt"],
)
def test_semantics_filename_rejects_paths_and_non_json_names(
    tmp_path: Path,
    filename: str,
) -> None:
    with pytest.raises(ValueError, match="must be a .json basename"):
        load_scene_semantics(tmp_path, filename)


@pytest.mark.parametrize(
    "mutate",
    [
        lambda data: data.update(schema_version=2),
        lambda data: data.update(unknown_top_level=True),
        lambda data: data["exhibits"].append(deepcopy(data["exhibits"][0])),
        lambda data: data["themes"].append(deepcopy(data["themes"][0])),
        lambda data: data["exhibits"][0]["knowledge"]["themes"].append("TH_UNKNOWN"),
        lambda data: data["semantic_edges"][0].update(target="E99"),
        lambda data: data["semantic_edges"][0].update(target="E1"),
        lambda data: data["semantic_edges"][0].update(relation_type="related_to"),
        lambda data: data["semantic_edges"][0].pop("evidence_status"),
        lambda data: data["semantic_edges"][0].update(evidence_status="probable"),
        lambda data: data["semantic_edges"].append(deepcopy(data["semantic_edges"][0])),
        lambda data: data.update(spatial_edges=[]),
    ],
)
def test_invalid_v4_graph_fails(
    tmp_path: Path,
    valid_payload: dict,
    mutate,
) -> None:
    payload = deepcopy(valid_payload)
    mutate(payload)
    write_payload(tmp_path, payload)

    with pytest.raises((ValidationError, ValueError)):
        load_scene_semantics(tmp_path)
