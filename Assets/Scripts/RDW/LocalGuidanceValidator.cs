using UnityEngine;

/// <summary>
/// 在 Unity 执行后端 guidance 前校验请求时效、reset 要求和方向风险。
/// 输入来自请求快照、任务与物理风险状态，拒绝原因提供给协调器和研究者 UI。
/// </summary>
public sealed class LocalGuidanceValidator : MonoBehaviour
{
    [SerializeField] private NavigationTargetManager navigationTargets;
    [SerializeField] private PhysicalRiskEvaluator riskEvaluator;

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

        // 后端响应到达前任务或风险可能已经变化，过期 guidance 不允许继续执行。
        if (requestSnapshot != null && !requestSnapshot.Matches(runtimeState, currentRiskState))
        {
            reason = "Guidance rejected locally because task or risk state changed after the request.";
            return false;
        }

        if (currentRiskState == null)
        {
            reason = "Local RDW check has no current physical risk snapshot.";
            return false;
        }

        if (currentRiskState.unsafe_directions == null || currentRiskState.safe_directions == null)
        {
            reason = "Local RDW check received incomplete direction risk data.";
            return false;
        }

        // reset_required 优先于普通移动指令，防止所有方向危险时继续诱导用户移动。
        if (currentRiskState.reset_required)
        {
            reason = "Local RDW check requires pause/reset before executing guidance.";
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

        if (!navigationTargets.TryGetTargetDirection(response.selected_target, out var targetDirection) ||
            !IsNaturalDirection(targetDirection))
        {
            reason = "Local RDW check cannot calculate an executable target direction.";
            return false;
        }

        if (riskEvaluator == null ||
            !riskEvaluator.TryGetDirectionClearance(currentRiskState, targetDirection, out _))
        {
            reason = "Local RDW check cannot query planned direction clearance.";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(movementDirection) && movementDirection != targetDirection)
        {
            reason = $"Local RDW check rejected conflicting movement '{movementDirection}' for target direction '{targetDirection}'.";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(movementDirection) && IsUnsafe(movementDirection, currentRiskState))
        {
            reason = $"Local RDW check rejected unsafe {movementDirection} movement.";
            return false;
        }

        if (IsUnsafe(targetDirection, currentRiskState))
        {
            reason = $"Local RDW check rejected target in unsafe {targetDirection} direction.";
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

    private static bool IsNaturalDirection(string direction)
    {
        return direction == "forward" || direction == "left" || direction == "right";
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
