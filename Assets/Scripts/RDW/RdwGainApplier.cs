using System.Collections.Generic;
using UnityEngine;

/// <summary>GainApplier 对单个命令的处理状态。</summary>
public enum RdwGainApplyStatus
{
    /// <summary>命令已成功修改 Virtual Rotation Root。</summary>
    Applied,
    /// <summary>命令合法但没有请求 Root 修改。</summary>
    ZeroCommand,
    /// <summary>同一 sequence 已被处理。</summary>
    DuplicateSequence,
    /// <summary>sequence 早于最近已关闭帧。</summary>
    StaleSequence,
    /// <summary>Root 或 HMD pivot 层级不满足契约。</summary>
    InvalidRoot,
    /// <summary>命令序号或数值无效。</summary>
    InvalidCommand
}

/// <summary>记录 requested、limited 与最终 applied gain。</summary>
public readonly struct RdwGainApplyResult
{
    /// <summary>命令处理状态。</summary>
    public RdwGainApplyStatus Status { get; }
    /// <summary>提交给应用器的受限命令。</summary>
    public RdwGainCommand Command { get; }
    /// <summary>最终注入到 Root 的 yaw，单位：度。</summary>
    public float AppliedYawDeltaDeg { get; }
    /// <summary>最终注入的 Virtual XZ 平移，单位：米；Step 2A 固定为零。</summary>
    public Vector2 AppliedTranslationDeltaXZ { get; }
    /// <summary>命令是否正常关闭该 sequence。</summary>
    public bool WasAccepted => Status == RdwGainApplyStatus.Applied || Status == RdwGainApplyStatus.ZeroCommand;

    /// <summary>创建不可变 Gain 应用结果。</summary>
    public RdwGainApplyResult(RdwGainApplyStatus status, in RdwGainCommand command, float appliedYawDeltaDeg, Vector2 appliedTranslationDeltaXZ)
    {
        Status = status;
        Command = command;
        AppliedYawDeltaDeg = appliedYawDeltaDeg;
        AppliedTranslationDeltaXZ = appliedTranslationDeltaXZ;
    }
}

/// <summary>项目中唯一允许修改 Virtual Rotation Root 的入口。</summary>
public sealed class RdwGainApplier
{
    private readonly HashSet<long> standaloneSequences = new HashSet<long>();
    private Transform virtualRotationRoot;
    private Transform virtualPoseSource;
    private long lastPoseSequence;
    private long nextStandaloneSequence;

    /// <summary>当前配置的合法 Virtual Rotation Root。</summary>
    public Transform RotationRoot => IsValidRotationRoot(virtualRotationRoot) ? virtualRotationRoot : null;
    /// <summary>最近一次处理结果。</summary>
    public RdwGainApplyResult LastResult { get; private set; }

    /// <summary>创建统一 Root 应用器。</summary>
    public RdwGainApplier(Transform rotationRoot = null, Transform poseSource = null)
    {
        Configure(rotationRoot, poseSource);
    }

    /// <summary>配置 Root 与 HMD/Camera pivot 来源。</summary>
    public bool Configure(Transform rotationRoot, Transform poseSource)
    {
        if (!IsValidRotationRoot(rotationRoot) ||
            (poseSource != null && poseSource != rotationRoot && !poseSource.IsChildOf(rotationRoot)))
        {
            return false;
        }

        virtualRotationRoot = rotationRoot;
        virtualPoseSource = poseSource;
        return true;
    }

    /// <summary>为不属于 tracking frame 的兼容调用分配唯一负序号。</summary>
    public long AllocateStandaloneSequence()
    {
        nextStandaloneSequence--;
        return nextStandaloneSequence;
    }

    /// <summary>检查一个 sequence 是否已经由该应用器关闭。</summary>
    public bool HasProcessed(long sequence)
    {
        return sequence > 0 ? sequence <= lastPoseSequence : standaloneSequences.Contains(sequence);
    }

    /// <summary>围绕当前 HMD pivot 应用一次受限 yaw；同一 sequence 至多接受一次。</summary>
    public RdwGainApplyResult Apply(in RdwGainCommand command)
    {
        if (!IsValidRotationRoot(virtualRotationRoot) ||
            (virtualPoseSource != null && virtualPoseSource != virtualRotationRoot && !virtualPoseSource.IsChildOf(virtualRotationRoot)))
        {
            return SetResult(RdwGainApplyStatus.InvalidRoot, command, 0f);
        }

        if (command.PoseSequence == 0 || !RdwYawConvention.IsFinite(command.RotationYawDeltaDeg) ||
            !RdwYawConvention.IsFinite(command.CurvatureYawDeltaDeg) ||
            !RdwYawConvention.IsFinite(command.TranslationDeltaXZ.x) ||
            !RdwYawConvention.IsFinite(command.TranslationDeltaXZ.y))
        {
            return SetResult(RdwGainApplyStatus.InvalidCommand, command, 0f);
        }

        if (command.PoseSequence > 0)
        {
            if (command.PoseSequence == lastPoseSequence) return SetResult(RdwGainApplyStatus.DuplicateSequence, command, 0f);
            if (command.PoseSequence < lastPoseSequence) return SetResult(RdwGainApplyStatus.StaleSequence, command, 0f);
            lastPoseSequence = command.PoseSequence;
        }
        else if (!standaloneSequences.Add(command.PoseSequence))
        {
            return SetResult(RdwGainApplyStatus.DuplicateSequence, command, 0f);
        }

        var yaw = command.RotationYawDeltaDeg + command.CurvatureYawDeltaDeg;
        if (Mathf.Abs(yaw) <= 0.0001f)
        {
            return SetResult(RdwGainApplyStatus.ZeroCommand, command, 0f);
        }

        var pivot = virtualPoseSource != null ? virtualPoseSource.position : virtualRotationRoot.position;
        virtualRotationRoot.RotateAround(pivot, Vector3.up, yaw);
        return SetResult(RdwGainApplyStatus.Applied, command, yaw);
    }

    private RdwGainApplyResult SetResult(RdwGainApplyStatus status, in RdwGainCommand command, float appliedYaw)
    {
        LastResult = new RdwGainApplyResult(status, command, appliedYaw, Vector2.zero);
        return LastResult;
    }

    private static bool IsValidRotationRoot(Transform root)
    {
        return root != null && root.GetComponent<Camera>() == null;
    }
}
