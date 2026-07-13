using System;
using UnityEngine;

[Serializable]
public sealed class UserStateSnapshot
{
    public Vector3 virtualPosition;
    public Vector3 virtualForward;
    public Vector2 physicalPosition;
    public Vector2 physicalForward;
    public float physicalYawDegrees;

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
