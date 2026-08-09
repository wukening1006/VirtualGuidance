using UnityEngine;

/// <summary>RDW 根节点控制的唯一 owner 类型。</summary>
public enum RdwGainOwner
{
    /// <summary>当前帧没有系统请求 Root 控制权。</summary>
    None,
    /// <summary>Step 1 的显式 Guidance Rotation Gain。</summary>
    GuidanceTurnGain,
    /// <summary>连续 Redirector 的 subtle redirection。</summary>
    ContinuousRedirector,
    /// <summary>Resetter 的独占 mapping redirection；Step 2A 仅保留 owner。</summary>
    Resetter,
    /// <summary>安全条件不满足，禁止任何 Root 注入。</summary>
    HardStop
}

/// <summary>当前 Guidance Target 到 RDW 底层的最小导航意图。</summary>
public readonly struct RdwNavigationIntent
{
    /// <summary>当前 Guidance Target ID。</summary>
    public string TargetId { get; }
    /// <summary>目标在 Virtual XZ 平面中的位置。</summary>
    public Vector2 VirtualTargetPositionXZ { get; }
    /// <summary>用户当前希望在虚拟空间移动的单位方向。</summary>
    public Vector2 DesiredVirtualDirectionXZ { get; }
    /// <summary>目标在现有导航系统中的语义角色。</summary>
    public NavigationTargetRole TargetRole { get; }
    /// <summary>导航意图是否包含有效目标和方向。</summary>
    public bool IsValid => !string.IsNullOrWhiteSpace(TargetId) && DesiredVirtualDirectionXZ.sqrMagnitude > 0.000001f;

    /// <summary>创建一份不可变导航意图。</summary>
    public RdwNavigationIntent(string targetId, Vector2 targetPositionXZ, Vector2 desiredDirectionXZ, NavigationTargetRole targetRole)
    {
        TargetId = targetId ?? string.Empty;
        VirtualTargetPositionXZ = targetPositionXZ;
        DesiredVirtualDirectionXZ = desiredDirectionXZ.sqrMagnitude > 0.000001f ? desiredDirectionXZ.normalized : Vector2.zero;
        TargetRole = targetRole;
    }
}

/// <summary>RDW 算法输出的统一 gain 命令；算法不得直接操作 Transform。</summary>
public readonly struct RdwGainCommand
{
    /// <summary>由真实转头产生的额外虚拟 yaw，单位：度。</summary>
    public float RotationYawDeltaDeg { get; }
    /// <summary>由真实位移产生的曲率 yaw，单位：度。</summary>
    public float CurvatureYawDeltaDeg { get; }
    /// <summary>虚拟 XZ 平移增量，单位：米；Step 2A 强制为零。</summary>
    public Vector2 TranslationDeltaXZ { get; }
    /// <summary>提交该命令的 RDW owner。</summary>
    public RdwGainOwner Source { get; }
    /// <summary>命令来源或安全处理原因。</summary>
    public string Reason { get; }
    /// <summary>命令所属的冻结 pose sequence。</summary>
    public long PoseSequence { get; }
    /// <summary>命令是否请求了非零 Root 操作。</summary>
    public bool HasControl => Mathf.Abs(RotationYawDeltaDeg) > 0.0001f || Mathf.Abs(CurvatureYawDeltaDeg) > 0.0001f || TranslationDeltaXZ.sqrMagnitude > 0.000001f;

    /// <summary>创建一份 gain 命令。</summary>
    public RdwGainCommand(float rotationYawDeltaDeg, float curvatureYawDeltaDeg, Vector2 translationDeltaXZ, RdwGainOwner source, string reason, long poseSequence)
    {
        RotationYawDeltaDeg = rotationYawDeltaDeg;
        CurvatureYawDeltaDeg = curvatureYawDeltaDeg;
        TranslationDeltaXZ = translationDeltaXZ;
        Source = source;
        Reason = reason ?? string.Empty;
        PoseSequence = poseSequence;
    }

    /// <summary>创建指定帧的零命令。</summary>
    public static RdwGainCommand Zero(long poseSequence, string reason = "none")
    {
        return new RdwGainCommand(0f, 0f, Vector2.zero, RdwGainOwner.None, reason, poseSequence);
    }
}

/// <summary>连续 Redirector 的纯计算接口。</summary>
public interface IRdwRedirector
{
    /// <summary>基于同一帧冻结事实计算命令；实现不得直接操作 Transform。</summary>
    RdwGainCommand Evaluate(
        in RdwPoseFrame pose,
        in RdwGeometrySnapshot geometry,
        in RdwNavigationIntent intent);
}

/// <summary>接收逻辑 Gain 来源提交的命令，并统一完成限幅与 Root 应用。</summary>
public interface IRdwGainCommandSink
{
    /// <summary>提交一份属于当前 RDW 帧的命令。</summary>
    RdwGainApplyResult Submit(in RdwGainCommand command);
}

/// <summary>Step 2A 的零行为 Redirector，确保 Step 1 行为不变。</summary>
public sealed class NullRedirector : IRdwRedirector
{
    /// <summary>返回同一 pose sequence 的零命令，不改变 Step 1 行为。</summary>
    public RdwGainCommand Evaluate(
        in RdwPoseFrame pose,
        in RdwGeometrySnapshot geometry,
        in RdwNavigationIntent intent)
    {
        return RdwGainCommand.Zero(pose.Sequence, "null_redirector");
    }
}
