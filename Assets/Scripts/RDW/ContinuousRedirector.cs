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

/// <summary>集中判断基础移动或 Continuous Steering 是否具备 Root owner 仲裁资格。</summary>
public static class RdwContinuousAvailability
{
    /// <summary>
    /// 基础移动不依赖 Continuous RDW 或导航意图；Steering 仍需显式启用且持有有效导航意图。
    /// 两条路径共同服从会话、Tracking、Calibration 与物理空间安全门。
    /// </summary>
    public static bool IsAvailable(
        bool locomotionAvailable,
        bool continuousEnabled,
        bool steeringAvailable,
        bool sessionRunning,
        bool runtimeNavigating,
        bool poseValid,
        bool trackingValid,
        bool calibrationValid,
        bool physicalSpaceValid,
        bool navigationIntentValid)
    {
        var runtimeSafe = sessionRunning &&
               runtimeNavigating &&
               poseValid &&
               trackingValid &&
               calibrationValid &&
               physicalSpaceValid;
        return runtimeSafe &&
               (locomotionAvailable ||
                (continuousEnabled && steeringAvailable && navigationIntentValid));
    }
}

/// <summary>公开组合 Redirector 中基础移动与可选 Steering 的独立能力。</summary>
public interface IRdwRedirectorChannels
{
    /// <summary>当前是否有基础虚拟平移请求。</summary>
    bool LocomotionAvailable { get; }
    /// <summary>当前是否允许并具备 Continuous Steering 能力。</summary>
    bool SteeringAvailable { get; }
    /// <summary>切换可选 Steering，不影响基础移动来源。</summary>
    void SetSteeringEnabled(bool enabled);
}

/// <summary>把基础 ControllerSimulation 平移与可选 steering yaw/curvature 合并为一个 Root 命令。</summary>
public sealed class CompositeContinuousRedirector : IRdwRedirector, IRdwRedirectorAvailability, IRdwRedirectorChannels, IRdwRedirectorDiagnosticsSource, IRdwRedirectorLifecycle
{
    private readonly IRdwRedirector locomotionRedirector;
    private readonly IRdwRedirector steeringRedirector;
    private bool steeringEnabled = true;

    /// <summary>创建连续命令组合器；两个来源都不能直接操作 Virtual Root。</summary>
    public CompositeContinuousRedirector(IRdwRedirector locomotion, IRdwRedirector steering)
    {
        locomotionRedirector = locomotion;
        steeringRedirector = steering;
        LastDiagnostics = RdwRedirectorDiagnostics.Empty;
    }

    /// <summary>最近一次可选 steering Redirector 的诊断。</summary>
    public RdwRedirectorDiagnostics LastDiagnostics { get; private set; }
    /// <summary>基础移动来源当前是否请求虚拟平移。</summary>
    public bool LocomotionAvailable => IsSourceAvailable(locomotionRedirector);
    /// <summary>实验条件允许且 Steering 来源可用时返回 true。</summary>
    public bool SteeringAvailable => steeringEnabled && IsSourceAvailable(steeringRedirector);
    /// <summary>任一合法来源当前可用时参与 Root owner 仲裁。</summary>
    public bool IsAvailable => LocomotionAvailable || SteeringAvailable;

    /// <summary>按实验条件切换 Steering；关闭时只清理 Steering 历史，保留基础移动。</summary>
    public void SetSteeringEnabled(bool enabled)
    {
        if (steeringEnabled == enabled)
        {
            return;
        }

        steeringEnabled = enabled;
        if (!enabled)
        {
            ResetSteeringSmoothing();
            LastDiagnostics = RdwRedirectorDiagnostics.Empty;
        }
    }

    /// <summary>在一个 PoseSequence 内合并平移与旋转 proposal，确保一次提交。</summary>
    public RdwGainCommand Evaluate(in RdwPoseFrame pose, in RdwGeometrySnapshot geometry, in RdwNavigationIntent intent)
    {
        var locomotion = locomotionRedirector != null
            ? locomotionRedirector.Evaluate(pose, geometry, intent)
            : RdwGainCommand.Zero(pose.Sequence, "simulation_idle");
        var steering = SteeringAvailable && intent.IsValid
            ? steeringRedirector.Evaluate(pose, geometry, intent)
            : RdwGainCommand.Zero(pose.Sequence, "redirector_disabled");
        LastDiagnostics = SteeringAvailable && intent.IsValid &&
                          steeringRedirector is IRdwRedirectorDiagnosticsSource diagnostics
            ? diagnostics.LastDiagnostics
            : RdwRedirectorDiagnostics.Empty;

        if (steeringEnabled && !intent.IsValid)
        {
            ResetSteeringSmoothing();
        }

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
        ResetSteeringSmoothing();
        LastDiagnostics = RdwRedirectorDiagnostics.Empty;
    }

    private void ResetSteeringSmoothing()
    {
        if (steeringRedirector is IRdwRedirectorLifecycle steering)
        {
            steering.ResetSmoothing();
        }
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
