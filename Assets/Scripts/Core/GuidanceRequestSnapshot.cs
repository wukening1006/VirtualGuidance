using System;

public sealed class GuidanceRequestSnapshot
{
    public int stateVersion { get; private set; }
    public string currentExhibitId { get; private set; }
    public string semanticNextTargetId { get; private set; }
    public string physicalSpaceId { get; private set; }
    public string riskSignature { get; private set; }

    public static GuidanceRequestSnapshot Capture(
        GuidanceRequest request,
        PhysicalRiskState riskState,
        GuidanceRuntimeState runtimeState)
    {
        return new GuidanceRequestSnapshot
        {
            stateVersion = runtimeState != null ? runtimeState.stateVersion : 0,
            currentExhibitId = request != null ? request.current_exhibit : string.Empty,
            semanticNextTargetId = request != null ? request.semantic_next_target : string.Empty,
            physicalSpaceId = request != null ? request.physical_space_id : string.Empty,
            riskSignature = BuildRiskSignature(riskState)
        };
    }

    public bool Matches(GuidanceRuntimeState runtimeState, PhysicalRiskState riskState)
    {
        if (runtimeState != null && runtimeState.stateVersion != stateVersion)
        {
            return false;
        }

        return string.Equals(riskSignature, BuildRiskSignature(riskState), StringComparison.Ordinal);
    }

    private static string BuildRiskSignature(PhysicalRiskState riskState)
    {
        if (riskState == null)
        {
            return "risk:unknown";
        }

        return string.Join(
            "|",
            riskState.physical_space,
            riskState.reset_required ? "reset" : "no_reset",
            Join(riskState.unsafe_directions),
            Join(riskState.safe_directions));
    }

    private static string Join(string[] values)
    {
        return values == null || values.Length == 0 ? "none" : string.Join(",", values);
    }
}
