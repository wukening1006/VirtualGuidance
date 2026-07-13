using UnityEngine;

public sealed class XRTrackingAdapter : MonoBehaviour
{
    [SerializeField] private UserPoseTracker userPoseTracker;

    private void Awake()
    {
        ResolveReferences();
    }

    public void Configure(UserPoseTracker poseTracker)
    {
        userPoseTracker = poseTracker;
    }

    public UserStateSnapshot CaptureSnapshot()
    {
        return UserStateSnapshot.Capture(userPoseTracker);
    }

    public float PhysicalYawDegrees
    {
        get
        {
            var forward = userPoseTracker != null ? userPoseTracker.PhysicalForwardXZ : Vector2.up;
            return Mathf.Atan2(forward.x, forward.y) * Mathf.Rad2Deg;
        }
    }

    private void ResolveReferences()
    {
        if (userPoseTracker == null)
        {
            userPoseTracker = FindObjectOfType<UserPoseTracker>();
        }
    }
}
