using UnityEngine;

public sealed class SemanticTargetSelector : MonoBehaviour
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
