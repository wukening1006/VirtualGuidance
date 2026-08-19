"""scene_semantics.json v4 strict schema and graph validation tests."""

from __future__ import annotations

from copy import deepcopy
import json
from pathlib import Path

import pytest
from pydantic import ValidationError

from backend.pipeline import load_scene_semantics


CONFIG_PATH = Path(__file__).resolve().parents[2] / "Assets" / "Configs" / "scene_semantics.json"


@pytest.fixture
def valid_payload() -> dict:
    return json.loads(CONFIG_PATH.read_text(encoding="utf-8-sig"))


def write_payload(directory: Path, payload: dict) -> None:
    (directory / "scene_semantics.json").write_text(
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
