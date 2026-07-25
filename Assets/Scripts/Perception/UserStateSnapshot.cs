using System;
using UnityEngine;

[Serializable]
/// <summary>
/// 保存同一采样时刻的虚拟世界位姿与 physical tracking space 位姿。
/// 快照由 XR 或请求模块捕获，用于跨模块传递且不会随追踪器后续更新。
/// </summary>
public sealed class UserStateSnapshot
{
    /// <summary>用户在虚拟世界中的位置，单位：米。</summary>
    public Vector3 virtualPosition;
    /// <summary>用户在虚拟世界中的三维前向向量。</summary>
    public Vector3 virtualForward;
    /// <summary>用户在 physical XZ 平面的位置，x/y 分别对应 Unity X/Z，单位：米。</summary>
    public Vector2 physicalPosition;
    /// <summary>用户在 physical XZ 平面的归一化朝向，x/y 分别对应 Unity X/Z。</summary>
    public Vector2 physicalForward;
    /// <summary>physical 朝向相对 +Z 的水平角，正值为左转，单位：度。</summary>
    public float physicalYawDegrees;

    /// <summary>
    /// 从位姿追踪器复制当前虚拟与物理状态；追踪器缺失时返回默认零值快照。
    /// </summary>
    /// <returns>与追踪器后续变化相互独立的用户状态快照。</returns>
    public static UserStateSnapshot Capture(UserPoseTracker poseTracker)
    {
        if (poseTracker == null)
        {
            return new UserStateSnapshot();
        }

        var forward = poseTracker.PhysicalForwardXZ;
        return new UserStateSnapshot
        {
            virtualPosition = poseTracker.UserVirtualPosition,
            virtualForward = poseTracker.UserVirtualForward,
            physicalPosition = poseTracker.PhysicalPositionXZ,
            physicalForward = forward,
            physicalYawDegrees = Mathf.Atan2(forward.x, forward.y) * Mathf.Rad2Deg
        };
    }
}
