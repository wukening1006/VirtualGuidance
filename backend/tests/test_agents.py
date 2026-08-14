"""测试 affordance、mock guide 和 SafetyVerifier 的关键实验条件行为。"""

from pathlib import Path

from backend.agents.guide_mock import GuideMockAgent
from backend.agents.rdw_affordance_agent import RDWAffordanceAgent
from backend.agents.rdw_state_agent import RDWStateAgent
from backend.agents.safety_verifier_agent import SafetyVerifierAgent
from backend.schemas import (
    Condition,
    ConditionCapabilityMode,
    GuidanceRequest,
    GuideDraft,
    MovementInstruction,
    NavigationTargetRole,
    condition_policy_for,
)


CONFIG_DIR = Path(__file__).resolve().parents[2] / "Assets" / "Configs"


def risk_snapshot(risk_id: str, calibration_id: str, position: list[float]) -> dict:
    return {
        "source": "unity",
        "risk_snapshot_id": risk_id,
        "risk_revision": 1,
        "calibration_id": calibration_id,
        "sampled_at_utc": "2026-07-23T08:00:00Z",
        "risk_policy_version": "unity-risk-policy-v2",
        "physical_space": "narrow_room",
        "physical_position": position,
        "physical_forward": [0.0, 1.0],
        "boundary_context": {"points": []},
        "obstacle_context": [],
        "risk_level": "high",
        "critical": False,
        "reset_required": False,
    }


def request_for(condition: str = Condition.OURS_FULL.value) -> GuidanceRequest:
    safe_filter = condition in {
        Condition.B5_OURS_WITHOUT_VERIFIER.value,
        Condition.OURS_FULL.value,
    }
    model_candidates = ["E3", "E4"] if safe_filter else ["E5", "E3", "E4"]
    return GuidanceRequest(
        task_definition_version="t2-main-target-v2",
        guidance_schema_version="guidance-schema-v3",
        candidate_policy_version="t2-candidate-policy-v1",
        risk_policy_version="unity-risk-policy-v2",
        session_id="session-agent-test",
        calibration_id="calibration-agent-test",
        request_id="request-agent-test",
        decision_id="decision-agent-test",
        candidate_set_id="candidates-agent-test",
        state_version=1,
        risk_snapshot_id="risk-agent-test",
        condition=condition,
        task_id="T2_main_target",
        physical_space_id="narrow_room",
        current_exhibit="E1",
        risk_snapshot=risk_snapshot("risk-agent-test", "calibration-agent-test", [1.8, 4.3]),
        user_physical_position=[1.8, 4.3],
        user_physical_forward=[0.0, 1.0],
        final_target="E5",
        preferred_task_target="E5",
        candidate_ids=["E5", "E3", "E4"],
        model_candidate_ids=model_candidates,
        allowed_candidate_ids=["E3", "E4"],
        semantic_candidates=[
            {
                "id": target_id,
                "target_role": "final_target" if target_id == "E5" else "navigable_detour",
                "relative_direction": {"E5": "forward", "E3": "left", "E4": "right"}[target_id],
                "relative_yaw_deg": {"E5": 0.0, "E3": -90.0, "E4": 90.0}[target_id],
                "suggested_movement_instruction": {
                    "E5": "move_forward_slowly",
                    "E3": "turn_left_slowly",
                    "E4": "turn_right_slowly",
                }[target_id],
            }
            for target_id in model_candidates
        ],
    )


def test_affordance_consumes_unity_allowed_semantic_candidate() -> None:
    request = request_for()
    state = RDWStateAgent.from_config_dir(CONFIG_DIR).evaluate(
        request.physical_space_id,
        request.physical_position_xz,
        request.physical_forward_xz,
    )

    affordance = RDWAffordanceAgent().build(request, state)

    assert affordance.intervention_type == "semantic_redirection"
    assert affordance.preferred_direction == "left"
    assert affordance.semantic_anchor == "E3"


def test_affordance_does_not_relabel_route_subgoal_as_semantic_redirection() -> None:
    request = request_for()
    candidate = request.candidate_for("E3")
    assert candidate is not None
    candidate.target_role = NavigationTargetRole.ROUTE_SUBGOAL
    state = RDWStateAgent.from_config_dir(CONFIG_DIR).evaluate(
        request.physical_space_id,
        request.physical_position_xz,
        request.physical_forward_xz,
    )

    affordance = RDWAffordanceAgent().build(request, state)

    assert affordance.intervention_type == "direct_visit"
    assert affordance.semantic_anchor == "E3"


def test_condition_policy_matrix_freezes_all_six_capability_boundaries() -> None:
    policies = {condition: condition_policy_for(condition) for condition in Condition}

    assert len(policies) == 6
    assert policies[Condition.B1_MLLM_ONLY].use_safe_candidate_filter == ConditionCapabilityMode.DISABLED
    assert policies[Condition.B2_RAW_PHYSICAL_DATA].use_raw_physical_data == ConditionCapabilityMode.ENABLED
    assert policies[Condition.B3_RISK_LABELS].use_risk_labels == ConditionCapabilityMode.ENABLED
    assert policies[Condition.B4_SCRIPTED_RESET].use_rdw_affordance == ConditionCapabilityMode.SCRIPTED
    assert policies[Condition.B5_OURS_WITHOUT_VERIFIER].use_model_verifier == ConditionCapabilityMode.DISABLED
    assert policies[Condition.B5_OURS_WITHOUT_VERIFIER].use_rotation_gain == ConditionCapabilityMode.ENABLED
    assert policies[Condition.OURS_FULL].use_model_verifier == ConditionCapabilityMode.ENABLED
    assert policies[Condition.OURS_FULL].policy_version == "condition-policy-v1"


def verify_candidate_movement(
    relative_direction: str,
    relative_yaw_deg: float,
    suggested_movement: MovementInstruction,
    movement: MovementInstruction,
):
    request = request_for()
    candidate = request.candidate_for("E3")
    assert candidate is not None
    candidate.relative_direction = relative_direction
    candidate.relative_yaw_deg = relative_yaw_deg
    candidate.suggested_movement_instruction = suggested_movement.value
    state = RDWStateAgent.from_config_dir(CONFIG_DIR).evaluate(
        request.physical_space_id,
        request.physical_position_xz,
        request.physical_forward_xz,
    )
    state.reset_required = False
    draft = GuideDraft(
        selected_target="E3",
        route_decision="direct_to_target",
        narration="Proceed.",
        gaze_cue="look_forward",
        gaze_target_id="E3_GazeTarget",
        movement_instruction=movement,
        physical_intent="test",
        intervention_type="test",
    )
    return SafetyVerifierAgent().verify(Condition.OURS_FULL.value, draft, state, request)


def test_verifier_allows_negative_behind_turn_when_coarse_left_is_unsafe() -> None:
    result = verify_candidate_movement(
        "behind",
        -150.0,
        MovementInstruction.TURN_LEFT_SLOWLY,
        MovementInstruction.TURN_LEFT_SLOWLY,
    )

    assert result.approved is True
    assert "delegated to Unity" in result.reason


def test_verifier_allows_positive_behind_turn_when_coarse_right_is_unsafe() -> None:
    result = verify_candidate_movement(
        "behind",
        150.0,
        MovementInstruction.TURN_RIGHT_SLOWLY,
        MovementInstruction.TURN_RIGHT_SLOWLY,
    )

    assert result.approved is True


def test_verifier_rejects_left_turn_for_positive_yaw() -> None:
    result = verify_candidate_movement(
        "right",
        80.0,
        MovementInstruction.TURN_RIGHT_SLOWLY,
        MovementInstruction.TURN_LEFT_SLOWLY,
    )

    assert result.approved is False
    assert "expected turn_right_slowly" in result.reason


def test_verifier_rejects_right_turn_for_negative_yaw() -> None:
    result = verify_candidate_movement(
        "left",
        -80.0,
        MovementInstruction.TURN_LEFT_SLOWLY,
        MovementInstruction.TURN_RIGHT_SLOWLY,
    )

    assert result.approved is False
    assert "expected turn_left_slowly" in result.reason


def test_verifier_allows_forward_movement_in_existing_forward_deadzone() -> None:
    result = verify_candidate_movement(
        "forward",
        10.0,
        MovementInstruction.MOVE_FORWARD_SLOWLY,
        MovementInstruction.MOVE_FORWARD_SLOWLY,
    )

    assert result.approved is True


def test_verifier_rejects_forward_movement_when_existing_rule_requires_turn() -> None:
    result = verify_candidate_movement(
        "right",
        80.0,
        MovementInstruction.TURN_RIGHT_SLOWLY,
        MovementInstruction.MOVE_FORWARD_SLOWLY,
    )

    assert result.approved is False
    assert "expected turn_right_slowly" in result.reason


def test_b5_stably_outputs_safe_candidate_with_unsafe_forward_without_verifier() -> None:
    request = request_for(Condition.B5_OURS_WITHOUT_VERIFIER.value)
    state = RDWStateAgent.from_config_dir(CONFIG_DIR).evaluate(
        request.physical_space_id,
        request.physical_position_xz,
        request.physical_forward_xz,
    )
    affordance = RDWAffordanceAgent().build(request, state)

    draft = GuideMockAgent().build(request, state, affordance)

    assert draft.selected_target == "E3"
    assert draft.movement_instruction == MovementInstruction.MOVE_FORWARD_SLOWLY
