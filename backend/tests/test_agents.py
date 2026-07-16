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
    AvatarAction,
    condition_policy_for,
)


CONFIG_DIR = Path(__file__).resolve().parents[2] / "Assets" / "Configs"


def request_for(condition: str = Condition.OURS_FULL.value) -> GuidanceRequest:
    return GuidanceRequest(
        request_id="request-agent-test",
        decision_id="decision-agent-test",
        state_version=1,
        target_role="route_subgoal",
        condition=condition,
        task_id="T2_main_target",
        physical_space_id="narrow_room",
        current_exhibit="E1",
        user_physical_position=[1.8, 4.3],
        user_physical_forward=[0.0, 1.0],
    )


def test_affordance_selects_semantic_redirection_when_forward_unsafe() -> None:
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


def test_verifier_rejects_unsafe_forward_movement() -> None:
    request = request_for()
    state = RDWStateAgent.from_config_dir(CONFIG_DIR).evaluate(
        request.physical_space_id,
        request.physical_position_xz,
        request.physical_forward_xz,
    )
    draft = GuideDraft(
        selected_target="E5",
        route_decision="unsafe_test",
        narration="Go forward.",
        gaze_cue="look_forward",
        gaze_target_id="E5_GazeTarget",
        avatar_action=AvatarAction.POINT_FORWARD,
        movement_instruction=MovementInstruction.MOVE_FORWARD_SLOWLY,
        physical_intent="test",
        intervention_type="test",
    )

    result = SafetyVerifierAgent().verify(Condition.OURS_FULL.value, draft, state)

    assert result.approved is False
    assert "movement_instruction" in result.reason


def test_verifier_rejects_unsafe_selected_target() -> None:
    request = request_for()
    state = RDWStateAgent.from_config_dir(CONFIG_DIR).evaluate(
        request.physical_space_id,
        request.physical_position_xz,
        request.physical_forward_xz,
    )
    draft = GuideDraft(
        selected_target="E4",
        route_decision="unsafe_target_test",
        narration="Look right.",
        gaze_cue="look_left",
        gaze_target_id="E4_GazeTarget",
        avatar_action=AvatarAction.POINT_LEFT,
        movement_instruction=MovementInstruction.TURN_LEFT_SLOWLY,
        physical_intent="test",
        intervention_type="test",
    )

    result = SafetyVerifierAgent().verify(Condition.OURS_FULL.value, draft, state)

    assert result.approved is False
    assert "selected_target" in result.reason


def test_b5_stably_outputs_unsafe_forward_without_verifier() -> None:
    request = GuidanceRequest(
        request_id="request-b5-test",
        decision_id="decision-b5-test",
        state_version=1,
        target_role="route_subgoal",
        condition=Condition.B5_OURS_WITHOUT_VERIFIER,
        task_id="T2_main_target",
        physical_space_id="narrow_room",
        current_exhibit="E1",
        user_physical_position=[1.0, 4.3],
        user_physical_forward=[0.0, 1.0],
    )
    state = RDWStateAgent.from_config_dir(CONFIG_DIR).evaluate(
        request.physical_space_id,
        request.physical_position_xz,
        request.physical_forward_xz,
    )
    affordance = RDWAffordanceAgent().build(request, state)

    draft = GuideMockAgent().build(request, state, affordance)

    assert draft.selected_target == "E5"
    assert draft.movement_instruction == MovementInstruction.MOVE_FORWARD_SLOWLY
