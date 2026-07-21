using System;

/// <summary>
/// 表示 TurnCue 物理转向追踪的生命周期状态。
/// </summary>
public enum TurnCueTrackingState
{
    /// <summary>当前没有活动 TurnCue。</summary>
    Inactive,
    /// <summary>正在累计用户的真实物理转角。</summary>
    Tracking,
    /// <summary>用户已进入目标朝向并保持稳定。</summary>
    Completed,
    /// <summary>用户累计反向转动超过限制。</summary>
    Reversed,
    /// <summary>用户未在最长等待时间内完成转向。</summary>
    TimedOut
}

/// <summary>
/// 表示处理一次物理转向样本后产生的终态结果。
/// </summary>
public enum TurnCueTrackingResult
{
    /// <summary>尚未产生终态。</summary>
    None,
    /// <summary>TurnCue 已完成。</summary>
    Completed,
    /// <summary>TurnCue 因反向累计而中止。</summary>
    Reversed,
    /// <summary>TurnCue 因等待超时而中止。</summary>
    TimedOut
}

/// <summary>
/// 以纯 C# 累计真实物理转角并判定 TurnCue 完成、反向或超时，不访问场景或执行 Gain。
/// </summary>
public sealed class TurnCueCompletionTracker
{
    private const float Epsilon = 0.0001f;

    private float noCueToleranceDegrees = 12f;
    private float reverseAbortDegrees = 5f;
    private float stableCompletionSeconds = 0.25f;
    private float timeoutSeconds = 8f;
    private float stableDurationSeconds;
    private float elapsedSeconds;

    /// <summary>当前 TurnCue 追踪状态。</summary>
    public TurnCueTrackingState State { get; private set; } = TurnCueTrackingState.Inactive;
    /// <summary>期望转向符号；正值表示左转，负值表示右转。</summary>
    public int ExpectedDirectionSign { get; private set; }
    /// <summary>目标相对偏航绝对值，单位：度。</summary>
    public float TargetYawDegrees { get; private set; }
    /// <summary>进入完成容差前实际需要累计的物理转角，单位：度。</summary>
    public float RequiredTurnYawDegrees => Math.Max(0f, TargetYawDegrees - noCueToleranceDegrees);
    /// <summary>已累计的正确方向物理转角，单位：度。</summary>
    public float AccumulatedYawDegrees { get; private set; }
    /// <summary>已累计的反向物理转角，单位：度。</summary>
    public float ReverseYawDegrees { get; private set; }
    /// <summary>当前是否仍在等待 TurnCue 完成。</summary>
    public bool IsTracking => State == TurnCueTrackingState.Tracking;

    /// <summary>
    /// 集中设置 TurnCue 完成、反向和超时参数。
    /// </summary>
    public void Configure(
        float noCueTolerance,
        float reverseAbort,
        float stableCompletionDuration,
        float maximumWaitDuration)
    {
        noCueToleranceDegrees = Math.Max(0f, noCueTolerance);
        reverseAbortDegrees = Math.Max(Epsilon, reverseAbort);
        stableCompletionSeconds = Math.Max(0f, stableCompletionDuration);
        timeoutSeconds = Math.Max(Epsilon, maximumWaitDuration);
    }

    /// <summary>
    /// 开始追踪一次指定方向和目标相对偏航的物理转向。
    /// </summary>
    /// <returns>目标已处于免提示容差内时立即返回 Completed，否则返回 None。</returns>
    public TurnCueTrackingResult Begin(int directionSign, float targetYawDegrees)
    {
        ExpectedDirectionSign = directionSign < 0 ? -1 : 1;
        TargetYawDegrees = Math.Max(0f, Math.Abs(targetYawDegrees));
        AccumulatedYawDegrees = 0f;
        ReverseYawDegrees = 0f;
        stableDurationSeconds = 0f;
        elapsedSeconds = 0f;

        if (TargetYawDegrees <= noCueToleranceDegrees)
        {
            State = TurnCueTrackingState.Completed;
            return TurnCueTrackingResult.Completed;
        }

        State = TurnCueTrackingState.Tracking;
        return TurnCueTrackingResult.None;
    }

    /// <summary>
    /// 处理一份真实物理转向样本，并返回新产生的完成、反向或超时结果。
    /// </summary>
    public TurnCueTrackingResult ProcessSample(
        bool isTurning,
        float signedYawDeltaDegrees,
        float deltaTimeSeconds)
    {
        if (State != TurnCueTrackingState.Tracking)
        {
            return TurnCueTrackingResult.None;
        }

        var sampleDuration = Math.Max(0f, deltaTimeSeconds);
        elapsedSeconds += sampleDuration;
        if (elapsedSeconds >= timeoutSeconds)
        {
            State = TurnCueTrackingState.TimedOut;
            return TurnCueTrackingResult.TimedOut;
        }

        if (Math.Abs(signedYawDeltaDegrees) > Epsilon)
        {
            if (Math.Sign(signedYawDeltaDegrees) == ExpectedDirectionSign)
            {
                AccumulatedYawDegrees += Math.Abs(signedYawDeltaDegrees);
            }
            else
            {
                ReverseYawDegrees += Math.Abs(signedYawDeltaDegrees);
                stableDurationSeconds = 0f;
                if (ReverseYawDegrees >= reverseAbortDegrees)
                {
                    State = TurnCueTrackingState.Reversed;
                    return TurnCueTrackingResult.Reversed;
                }
            }
        }

        if (AccumulatedYawDegrees + Epsilon < RequiredTurnYawDegrees)
        {
            stableDurationSeconds = 0f;
            return TurnCueTrackingResult.None;
        }

        stableDurationSeconds = isTurning ? 0f : stableDurationSeconds + sampleDuration;
        if (stableDurationSeconds + Epsilon < stableCompletionSeconds)
        {
            return TurnCueTrackingResult.None;
        }

        State = TurnCueTrackingState.Completed;
        return TurnCueTrackingResult.Completed;
    }

    /// <summary>取消当前 TurnCue 并清空全部累计状态。</summary>
    public void Cancel()
    {
        State = TurnCueTrackingState.Inactive;
        ExpectedDirectionSign = 0;
        TargetYawDegrees = 0f;
        AccumulatedYawDegrees = 0f;
        ReverseYawDegrees = 0f;
        stableDurationSeconds = 0f;
        elapsedSeconds = 0f;
    }
}
