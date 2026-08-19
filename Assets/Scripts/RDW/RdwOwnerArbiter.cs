/// <summary>按固定优先级为每个 RDW 帧选择唯一 Root owner。</summary>
public sealed class RdwOwnerArbiter
{
    /// <summary>根据安全与运行状态解析当前帧的唯一 owner。</summary>
    public RdwGainOwner Resolve(bool hardStop, bool resetActive, bool guidanceTurnActive, bool redirectorAvailable)
    {
        if (hardStop) return RdwGainOwner.HardStop;
        if (resetActive) return RdwGainOwner.Resetter;
        if (guidanceTurnActive) return RdwGainOwner.GuidanceTurnGain;
        return redirectorAvailable ? RdwGainOwner.ContinuousRedirector : RdwGainOwner.None;
    }
}
