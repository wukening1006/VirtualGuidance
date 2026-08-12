using UnityEngine;

/// <summary>
/// 在 Unity 执行后端 guidance 前校验请求时效、reset 要求和方向风险。
/// 输入来自请求快照、任务与物理风险状态，拒绝原因提供给协调器和研究者 UI。
/// </summary>
public sealed class LocalGuidanceValidator : MonoBehaviour
{
    [SerializeField] private NavigationTargetManager navigationTargets;
    [SerializeField] private PhysicalRiskEvaluator riskEvaluator;
    private readonly GuidanceActionPlanner actionPlanner = new GuidanceActionPlanner();

    private void Awake()
    {
        ResolveReferences();
    }

    /// <summary>注入目标方向索引与唯一的物理 clearance 查询来源。</summary>
    public void Configure(NavigationTargetManager targetManager, PhysicalRiskEvaluator evaluator = null)
    {
        if (targetManager != null)
        {
            navigationTargets = targetManager;
        }
        if (evaluator != null)
        {
            riskEvaluator = evaluator;
        }
    }

    /// <summary>
    /// 执行响应落地前的最后一道本地安全检查，不修改任务或表现状态。
    /// </summary>
    /// <param name="reason">校验失败时返回可记录和展示的英文原因；成功时为空。</param>
    /// <returns>响应仍与当前状态匹配且不会引导至已知危险方向时返回 true。</returns>
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

        // 风险修订可在网络等待期间变化；请求身份保持稳定后，具体方向由最新风险继续校验。
        if (requestSnapshot != null && !requestSnapshot.MatchesTaskContext(runtimeState, currentRiskState))
        {
            reason = "Guidance rejected locally because task, calibration, or physical space changed after the request.";
            return false;
        }

        if (currentRiskState == null)
        {
            reason = "Local RDW check has no current physical risk snapshot.";
            return false;
        }

        if (!TryGetMovementDirection(response.movement_instruction, out var movementDirection))
        {
            reason = $"Local RDW check rejected unknown movement '{response.movement_instruction}'.";
            return false;
        }

        if (navigationTargets == null ||
            string.IsNullOrWhiteSpace(response.selected_target) ||
            !navigationTargets.HasTarget(response.selected_target))
        {
            reason = "Local RDW check cannot resolve the selected target.";
            return false;
        }

        var targetRole = runtimeState != null && runtimeState.CurrentGuidanceTarget != null
            ? runtimeState.CurrentGuidanceTarget.TargetRole
            : navigationTargets.GetSafetyTargetRole(response.selected_target);
        if (targetRole == NavigationTargetRole.TurnCueAnchor &&
            string.IsNullOrWhiteSpace(movementDirection))
        {
            reason = "Local RDW check rejected a stationary movement for TurnCue target.";
            return false;
        }

        if (!navigationTargets.TryGetTargetBearing(response.selected_target, out var bearing))
        {
            reason = "Local RDW check cannot calculate the selected target bearing.";
            return false;
        }

        var plannedAction = actionPlanner.Plan(bearing);
        var expectedMovement = GuidanceActionProtocolMapper.ToMovementInstruction(plannedAction);
        if (!string.Equals(
                response.movement_instruction,
                expectedMovement,
                System.StringComparison.Ordinal))
        {
            reason =
                $"Local RDW check rejected movement '{response.movement_instruction}' because local planning requires '{expectedMovement}'.";
            return false;
        }

        if (riskEvaluator == null ||
            !riskEvaluator.IsRiskIdentityCurrent(currentRiskState))
        {
            reason = "Local RDW check has no current exact-direction evaluator.";
            return false;
        }

        var exactPhysicalDirection = RotatePhysicalForward(
            currentRiskState.user_physical_forward,
            bearing.RelativeYawDegrees);
        if (!riskEvaluator.TryEvaluateAbsoluteHeading(
                currentRiskState,
                exactPhysicalDirection,
                out var exactHeading) ||
            !PhysicalRiskEvaluator.IsMovementSafe(exactHeading))
        {
            reason = "Local RDW check rejected the selected target exact physical direction.";
            return false;
        }

        return true;
    }

    private void ResolveReferences()
    {
        if (navigationTargets == null)
        {
            navigationTargets = FindObjectOfType<NavigationTargetManager>();
        }
        if (riskEvaluator == null)
        {
            riskEvaluator = FindObjectOfType<PhysicalRiskEvaluator>();
        }
    }

    private static bool TryGetMovementDirection(string movement, out string direction)
    {
        direction = string.Empty;
        switch (movement)
        {
            case "move_forward_slowly":
            case "look_forward":
                direction = "forward";
                return true;
            case "turn_left_slowly":
            case "look_left":
                direction = "left";
                return true;
            case "turn_right_slowly":
            case "look_right":
                direction = "right";
                return true;
            case "pause_and_wait":
            case "reset_required":
                return true;
            default:
                return false;
        }
    }

    private static Vector2 RotatePhysicalForward(Vector2 forward, float yawDegrees)
    {
        forward = forward.sqrMagnitude > 0.0001f ? forward.normalized : Vector2.up;
        var radians = yawDegrees * Mathf.Deg2Rad;
        var sin = Mathf.Sin(radians);
        var cos = Mathf.Cos(radians);
        return new Vector2(
            forward.x * cos + forward.y * sin,
            -forward.x * sin + forward.y * cos).normalized;
    }

}
