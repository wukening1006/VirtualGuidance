/// <summary>
/// 使用冻结 Recovery Heading 与配置化 injection ratio 执行生产 Mapping Reset。
/// </summary>
public sealed class SafeHeadingMappingResetter : RdwYawMappingResetterBase
{
    /// <inheritdoc />
    protected override string CommandReason => "safe_heading_mapping_reset";

    /// <inheritdoc />
    protected override float CalculateRequestedYaw(float physicalDeltaYaw)
    {
        return Plan == null ? 0f : physicalDeltaYaw * Plan.InjectionRatio;
    }
}
