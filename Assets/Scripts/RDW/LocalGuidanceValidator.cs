using UnityEngine;

/// <summary>
/// 在 Unity 执行后端 guidance 前校验请求时效、reset 要求和方向风险。
/// 输入来自请求快照、任务与物理风险状态，拒绝原因提供给协调器和研究者 UI。
/// </summary>
public sealed class LocalGuidanceValidator : MonoBehaviour
{
    [SerializeField] private NavigationTargetManager navigationTargets;

    private void Awake()
    {
        ResolveReferences();
    }

    /// <summary>注入用于解析目标物理方向的导航目标索引。</summary>
    public void Configure(NavigationTargetManager targetManager)
    {
        navigationTargets = targetManager;
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

        // 风险模块未提供状态时保持现有降级行为，由后端 verifier 结果决定是否执行。
        if (currentRiskState == null)
        {
            return true;
        }

        // reset_required 优先于普通移动指令，防止所有方向危险时继续诱导用户移动。
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
