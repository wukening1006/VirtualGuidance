using UnityEngine;

/// <summary>Continuous Redirector 的实验模式。</summary>
public enum ContinuousRedirectorMode
{
    /// <summary>关闭连续重定向，仅保留 None 对照基线。</summary>
    None,
    /// <summary>启用 Steer-to-Center 连续重定向。</summary>
    S2C,
    /// <summary>启用 Steer-to-Orbit 连续重定向。</summary>
    S2O,
    /// <summary>启用 Thomas Artificial Potential Field 连续重定向。</summary>
    ThomasAPF
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

/// <summary>把 ControllerSimulation 平移与所选 steering yaw/curvature 合并为一个 Continuous 命令。</summary>
public sealed class CompositeContinuousRedirector : IRdwRedirector, IRdwRedirectorAvailability, IRdwRedirectorDiagnosticsSource, IRdwRedirectorLifecycle
{
    private readonly IRdwRedirector locomotionRedirector;
    private readonly IRdwRedirector steeringRedirector;

    /// <summary>创建连续命令组合器；两个来源都不能直接操作 Virtual Root。</summary>
    public CompositeContinuousRedirector(IRdwRedirector locomotion, IRdwRedirector steering)
    {
        locomotionRedirector = locomotion;
        steeringRedirector = steering;
        LastDiagnostics = RdwRedirectorDiagnostics.Empty;
    }

    /// <summary>最近一次可选 steering Redirector 的诊断。</summary>
    public RdwRedirectorDiagnostics LastDiagnostics { get; private set; }
    /// <summary>任一组合来源当前可用时参与 Continuous owner 仲裁。</summary>
    public bool IsAvailable => IsSourceAvailable(locomotionRedirector) || IsSourceAvailable(steeringRedirector);

    /// <summary>在一个 PoseSequence 内合并平移与旋转 proposal，确保一次提交。</summary>
    public RdwGainCommand Evaluate(in RdwPoseFrame pose, in RdwGeometrySnapshot geometry, in RdwNavigationIntent intent)
    {
        var locomotion = locomotionRedirector != null
            ? locomotionRedirector.Evaluate(pose, geometry, intent)
            : RdwGainCommand.Zero(pose.Sequence, "simulation_idle");
        var steering = steeringRedirector != null
            ? steeringRedirector.Evaluate(pose, geometry, intent)
            : RdwGainCommand.Zero(pose.Sequence, "redirector_disabled");
        LastDiagnostics = steeringRedirector is IRdwRedirectorDiagnosticsSource diagnostics
            ? diagnostics.LastDiagnostics
            : RdwRedirectorDiagnostics.Empty;

        return new RdwGainCommand(
            steering.RotationYawDeltaDeg,
            steering.CurvatureYawDeltaDeg,
            locomotion.TranslationDeltaXZ,
            RdwGainOwner.ContinuousRedirector,
            ComposeReason(locomotion, steering),
            pose.Sequence);
    }

    /// <summary>清除两个来源的跨帧状态。</summary>
    public void ResetSmoothing()
    {
        if (locomotionRedirector is IRdwRedirectorLifecycle locomotion)
        {
            locomotion.ResetSmoothing();
        }
        if (steeringRedirector is IRdwRedirectorLifecycle steering)
        {
            steering.ResetSmoothing();
        }
        LastDiagnostics = RdwRedirectorDiagnostics.Empty;
    }

    private static string ComposeReason(in RdwGainCommand locomotion, in RdwGainCommand steering)
    {
        if (steering.HasControl && locomotion.TranslationDeltaXZ.sqrMagnitude > 0.000001f)
        {
            if (steering.Reason.StartsWith("s2o", System.StringComparison.OrdinalIgnoreCase))
            {
                return "controller_simulation_plus_s2o";
            }
            if (steering.Reason.StartsWith("thomas_apf", System.StringComparison.OrdinalIgnoreCase))
            {
                return "controller_simulation_plus_thomas_apf";
            }
            return "controller_simulation_plus_s2c";
        }
        return steering.HasControl ? steering.Reason : locomotion.Reason;
    }

    private static bool IsSourceAvailable(IRdwRedirector source)
    {
        return source != null &&
               (!(source is IRdwRedirectorAvailability availability) || availability.IsAvailable);
    }
}
