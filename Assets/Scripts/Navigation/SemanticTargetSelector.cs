using UnityEngine;

/// <summary>
/// 将任务的语义下一目标与当前物理安全方向结合，选择本次 guidance 的可执行展品目标。
/// 候选来自当前展品的 related exhibits，结果提供给 GuidanceCoordinator。
/// </summary>
public sealed class SemanticTargetSelector : MonoBehaviour
{
    [SerializeField] private NavigationTargetManager navigationTargets;

    private void Awake()
    {
        ResolveReferences();
    }

    /// <summary>注入用于查询目标、关联展品及方向的导航索引。</summary>
    public void Configure(NavigationTargetManager targetManager)
    {
        if (targetManager != null)
        {
            navigationTargets = targetManager;
        }
    }

    /// <summary>
    /// 优先保留路线目标；其方向危险时依次选择明确安全、再选择未标危险的相关展品。
    /// </summary>
    /// <returns>可用于请求的展品 ID；无可接受候选时返回空字符串。</returns>
    public string SelectNextTarget(GuidanceRuntimeState runtimeState, PhysicalRiskState riskState)
    {
        ResolveReferences();
        if (runtimeState == null)
        {
            return string.Empty;
        }

        var preferred = runtimeState.nextTargetId;
        if (navigationTargets == null || riskState == null || string.IsNullOrWhiteSpace(preferred))
        {
            return preferred;
        }

        if (!IsDirectionUnsafe(navigationTargets.GetTargetDirection(preferred), riskState))
        {
            return preferred;
        }

        var related = navigationTargets.GetRelatedTargetIds(runtimeState.currentExhibitId);
        // 明确列入 safe_directions 的候选优先于仅未被标记为危险的候选。
        foreach (var candidate in related)
        {
            if (navigationTargets.HasTarget(candidate) &&
                IsDirectionSafe(navigationTargets.GetTargetDirection(candidate), riskState))
            {
                return candidate;
            }
        }

        foreach (var candidate in related)
        {
            if (navigationTargets.HasTarget(candidate) &&
                !IsDirectionUnsafe(navigationTargets.GetTargetDirection(candidate), riskState))
            {
                return candidate;
            }
        }

        return string.Empty;
    }

    private void ResolveReferences()
    {
        if (navigationTargets == null)
        {
            navigationTargets = FindObjectOfType<NavigationTargetManager>();
        }
    }

    private static bool IsDirectionUnsafe(string direction, PhysicalRiskState riskState)
    {
        return Contains(riskState != null ? riskState.unsafe_directions : null, direction);
    }

    private static bool IsDirectionSafe(string direction, PhysicalRiskState riskState)
    {
        return Contains(riskState != null ? riskState.safe_directions : null, direction);
    }

    private static bool Contains(string[] values, string value)
    {
        if (values == null || string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        foreach (var item in values)
        {
            if (item == value)
            {
                return true;
            }
        }

        return false;
    }
}
