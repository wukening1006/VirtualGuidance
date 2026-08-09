using UnityEngine;

/// <summary>冻结一次有效 tracking sample 的 RDW 位姿事实。</summary>
public readonly struct RdwPoseFrame
{
    /// <summary>单调递增的 tracking pose 序号。</summary>
    public long Sequence { get; }
    /// <summary>冻结该帧时的单调时间戳，单位：秒。</summary>
    public double Timestamp { get; }
    /// <summary>用户在 Physical XZ 平面中的位置，单位：米。</summary>
    public Vector2 PhysicalPositionXZ { get; }
    /// <summary>用户在 Physical XZ 平面中的单位前向。</summary>
    public Vector2 PhysicalForwardXZ { get; }
    /// <summary>Physical yaw，+Z 为零且右转为正，单位：度。</summary>
    public float PhysicalYaw { get; }
    /// <summary>用户在 Virtual XZ 平面中的位置，单位：米。</summary>
    public Vector2 VirtualPositionXZ { get; }
    /// <summary>用户在 Virtual XZ 平面中的单位前向。</summary>
    public Vector2 VirtualForwardXZ { get; }
    /// <summary>Virtual yaw，+Z 为零且右转为正，单位：度。</summary>
    public float VirtualYaw { get; }
    /// <summary>相对上一有效帧的 Physical XZ 位移，单位：米。</summary>
    public Vector2 DeltaPhysicalPositionXZ { get; }
    /// <summary>相对上一有效帧的最短 Physical yaw 增量，右转为正，单位：度。</summary>
    public float DeltaPhysicalYaw { get; }
    /// <summary>相对上一有效帧的采样时长，单位：秒。</summary>
    public float DeltaTime { get; }
    /// <summary>冻结帧所属的 Calibration ID。</summary>
    public string CalibrationId { get; }
    /// <summary>冻结帧所属的 Physical Space ID。</summary>
    public string PhysicalSpaceId { get; }
    /// <summary>tracking、calibration、identity 与数值检查是否全部通过。</summary>
    public bool IsValid { get; }

    /// <summary>创建一份不可变 RDW 位姿帧。</summary>
    public RdwPoseFrame(long sequence, double timestamp, Vector2 physicalPositionXZ, Vector2 physicalForwardXZ, Vector2 virtualPositionXZ, Vector2 virtualForwardXZ, Vector2 deltaPhysicalPositionXZ, float deltaPhysicalYaw, float deltaTime, string calibrationId, string physicalSpaceId, bool isValid)
    {
        Sequence = sequence;
        Timestamp = timestamp;
        PhysicalPositionXZ = physicalPositionXZ;
        PhysicalForwardXZ = physicalForwardXZ.sqrMagnitude > 0.000001f ? physicalForwardXZ.normalized : Vector2.up;
        PhysicalYaw = RdwYawConvention.ForwardToYaw(PhysicalForwardXZ);
        VirtualPositionXZ = virtualPositionXZ;
        VirtualForwardXZ = virtualForwardXZ.sqrMagnitude > 0.000001f ? virtualForwardXZ.normalized : Vector2.up;
        VirtualYaw = RdwYawConvention.ForwardToYaw(VirtualForwardXZ);
        DeltaPhysicalPositionXZ = deltaPhysicalPositionXZ;
        DeltaPhysicalYaw = deltaPhysicalYaw;
        DeltaTime = Mathf.Max(0f, deltaTime);
        CalibrationId = calibrationId ?? string.Empty;
        PhysicalSpaceId = physicalSpaceId ?? string.Empty;
        IsValid = isValid;
    }

    /// <summary>返回缺省无效帧，供 tracking chain 断开时使用。</summary>
    public static RdwPoseFrame Invalid(long sequence, double timestamp)
    {
        return new RdwPoseFrame(sequence, timestamp, Vector2.zero, Vector2.up, Vector2.zero, Vector2.up, Vector2.zero, 0f, 0f, string.Empty, string.Empty, false);
    }
}
