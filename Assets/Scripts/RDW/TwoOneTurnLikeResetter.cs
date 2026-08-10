/// <summary>
/// 仅用于验证 Physical turn 到 Virtual Root injection 完整管线的 Two-One Turn 类 Resetter。
/// </summary>
public sealed class TwoOneTurnLikeResetter : RdwYawMappingResetterBase
{
    /// <inheritdoc />
    protected override string CommandReason => "two_one_turn_like_reset";

    /// <inheritdoc />
    protected override float CalculateRequestedYaw(float physicalDeltaYaw)
    {
        return Plan == null ? 0f : physicalDeltaYaw * Plan.InjectionRatio;
    }
}
