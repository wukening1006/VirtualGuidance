using UnityEngine;

/// <summary>Continuous Redirector 的实验模式。</summary>
public enum ContinuousRedirectorMode
{
    /// <summary>关闭连续重定向，仅保留 None 对照基线。</summary>
    None,
    /// <summary>启用 Steer-to-Center 连续重定向。</summary>
    S2C
}

/// <summary>集中判断 Continuous Redirector 是否具备 owner 仲裁资格。</summary>
public static class RdwContinuousAvailability
{
    /// <summary>只有会话、导航、位姿、安全事实、意图与模式全部有效时才允许 Continuous owner。</summary>
    public static bool IsAvailable(
        bool continuousEnabled,
        bool redirectorAvailable,
        bool sessionRunning,
        bool runtimeNavigating,
        bool poseValid,
        bool trackingValid,
        bool calibrationValid,
        bool physicalSpaceValid,
        bool navigationIntentValid)
    {
        return continuousEnabled &&
               redirectorAvailable &&
               sessionRunning &&
               runtimeNavigating &&
               poseValid &&
               trackingValid &&
               calibrationValid &&
               physicalSpaceValid &&
               navigationIntentValid;
    }
}

/// <summary>把 ControllerSimulation 平移与 S2C yaw/curvature 合并为一个 Continuous 命令。</summary>
public sealed class CompositeContinuousRedirector : IRdwRedirector, IRdwRedirectorAvailability, IRdwRedirectorDiagnosticsSource, IRdwRedirectorLifecycle
{
    private readonly IRdwRedirector locomotionRedirector;
    private readonly IRdwRedirector s2cRedirector;

    /// <summary>创建连续命令组合器；两个来源都不能直接操作 Virtual Root。</summary>
    public CompositeContinuousRedirector(IRdwRedirector locomotion, IRdwRedirector s2c)
    {
        locomotionRedirector = locomotion;
        s2cRedirector = s2c;
        LastDiagnostics = RdwRedirectorDiagnostics.Empty;
    }

    /// <summary>最近一次 S2C 诊断。</summary>
    public RdwRedirectorDiagnostics LastDiagnostics { get; private set; }
    /// <summary>任一组合来源当前可用时参与 Continuous owner 仲裁。</summary>
    public bool IsAvailable => IsSourceAvailable(locomotionRedirector) || IsSourceAvailable(s2cRedirector);

    /// <summary>在一个 PoseSequence 内合并平移与旋转 proposal，确保一次提交。</summary>
    public RdwGainCommand Evaluate(in RdwPoseFrame pose, in RdwGeometrySnapshot geometry, in RdwNavigationIntent intent)
    {
        var locomotion = locomotionRedirector != null
            ? locomotionRedirector.Evaluate(pose, geometry, intent)
            : RdwGainCommand.Zero(pose.Sequence, "simulation_idle");
        var s2c = s2cRedirector != null
            ? s2cRedirector.Evaluate(pose, geometry, intent)
            : RdwGainCommand.Zero(pose.Sequence, "s2c_disabled");
        LastDiagnostics = s2cRedirector is IRdwRedirectorDiagnosticsSource diagnostics
            ? diagnostics.LastDiagnostics
            : RdwRedirectorDiagnostics.Empty;

        return new RdwGainCommand(
            s2c.RotationYawDeltaDeg,
            s2c.CurvatureYawDeltaDeg,
            locomotion.TranslationDeltaXZ,
            RdwGainOwner.ContinuousRedirector,
            ComposeReason(locomotion, s2c),
            pose.Sequence);
    }

    /// <summary>清除两个来源的跨帧状态。</summary>
    public void ResetSmoothing()
    {
        if (locomotionRedirector is IRdwRedirectorLifecycle locomotion)
        {
            locomotion.ResetSmoothing();
        }
        if (s2cRedirector is IRdwRedirectorLifecycle s2c)
        {
            s2c.ResetSmoothing();
        }
        LastDiagnostics = RdwRedirectorDiagnostics.Empty;
    }

    private static string ComposeReason(in RdwGainCommand locomotion, in RdwGainCommand s2c)
    {
        if (s2c.HasControl && locomotion.TranslationDeltaXZ.sqrMagnitude > 0.000001f)
        {
            return "controller_simulation_plus_s2c";
        }
        return s2c.HasControl ? s2c.Reason : locomotion.Reason;
    }

    private static bool IsSourceAvailable(IRdwRedirector source)
    {
        return source != null &&
               (!(source is IRdwRedirectorAvailability availability) || availability.IsAvailable);
    }
}
