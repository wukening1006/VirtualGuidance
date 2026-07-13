using UnityEngine;

public sealed class LocalGuidanceValidator : MonoBehaviour
{
    [SerializeField] private NavigationTargetManager navigationTargets;

    private void Awake()
    {
        ResolveReferences();
    }

    public void Configure(NavigationTargetManager targetManager)
    {
        navigationTargets = targetManager;
    }

    public bool Validate(
        GuidanceResponse response,
        GuidanceRequestSnapshot requestSnapshot,
        GuidanceRuntimeState runtimeState,
        PhysicalRiskState currentRiskState,
        out string reason)
    {
        ResolveReferences();
        reason = string.Empty;

        if (response == null)
        {
            reason = "Guidance response is null.";
            return false;
        }

        if (requestSnapshot != null && !requestSnapshot.Matches(runtimeState, currentRiskState))
        {
            reason = "Guidance rejected locally because task or risk state changed after the request.";
            return false;
        }

        if (currentRiskState == null)
        {
            return true;
        }

        if (currentRiskState.reset_required && !IsPauseOrReset(response.movement_instruction))
        {
            reason = "Local RDW check requires pause/reset before executing guidance.";
            return false;
        }

        var movementDirection = MovementDirection(response.movement_instruction);
        if (IsUnsafe(movementDirection, currentRiskState))
        {
            reason = $"Local RDW check rejected unsafe {movementDirection} movement.";
            return false;
        }

        if (navigationTargets != null && !string.IsNullOrWhiteSpace(response.selected_target))
        {
            var targetDirection = navigationTargets.GetTargetDirection(response.selected_target);
            if (IsUnsafe(targetDirection, currentRiskState))
            {
                reason = $"Local RDW check rejected target in unsafe {targetDirection} direction.";
                return false;
            }
        }

        return true;
    }

    private void ResolveReferences()
    {
        if (navigationTargets == null)
        {
            navigationTargets = FindObjectOfType<NavigationTargetManager>();
        }
    }

    private static bool IsPauseOrReset(string movement)
    {
        return movement == "pause_and_wait" || movement == "reset_required";
    }

    private static string MovementDirection(string movement)
    {
        switch (movement)
        {
            case "move_forward_slowly":
            case "look_forward":
                return "forward";
            case "turn_left_slowly":
            case "look_left":
                return "left";
            case "turn_right_slowly":
            case "look_right":
                return "right";
            default:
                return string.Empty;
        }
    }

    private static bool IsUnsafe(string direction, PhysicalRiskState riskState)
    {
        if (string.IsNullOrWhiteSpace(direction) || riskState == null || riskState.unsafe_directions == null)
        {
            return false;
        }

        foreach (var unsafeDirection in riskState.unsafe_directions)
        {
            if (unsafeDirection == direction)
            {
                return true;
            }
        }

        return false;
    }
}
