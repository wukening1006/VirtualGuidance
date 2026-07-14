using UnityEngine;
using UnityEngine.XR;

public sealed class XRTrackingAdapter : MonoBehaviour
{
    [SerializeField] private UserPoseTracker userPoseTracker;
    [SerializeField] private bool readHeadsetTrackingWhenAvailable = true;
    [SerializeField] private Transform headsetTransform;

    public bool IsHeadsetTrackingActive { get; private set; }
    public bool IsHeadsetTrackingEnabled => readHeadsetTrackingWhenAvailable;
    public Vector3 LastHeadLocalPosition { get; private set; }
    public Quaternion LastHeadLocalRotation { get; private set; } = Quaternion.identity;

    private void Awake()
    {
        ResolveReferences();
    }

    public void Configure(UserPoseTracker poseTracker)
    {
        userPoseTracker = poseTracker;
        ResolveHeadsetTransform();
    }

    public void SetHeadsetTrackingEnabled(bool enabled)
    {
        readHeadsetTrackingWhenAvailable = enabled;
        if (!enabled)
        {
            IsHeadsetTrackingActive = false;
        }
    }

    private void Update()
    {
        if (!readHeadsetTrackingWhenAvailable || userPoseTracker == null)
        {
            IsHeadsetTrackingActive = false;
            return;
        }

        var headDevice = InputDevices.GetDeviceAtXRNode(XRNode.Head);
        if (!headDevice.isValid ||
            !headDevice.TryGetFeatureValue(CommonUsages.devicePosition, out var localPosition) ||
            !headDevice.TryGetFeatureValue(CommonUsages.deviceRotation, out var localRotation))
        {
            IsHeadsetTrackingActive = false;
            return;
        }

        ApplyTrackedPose(localPosition, localRotation, headsetTransform);
    }

    public void ApplyTrackedPose(Vector3 localPosition, Quaternion localRotation, Transform virtualHead = null)
    {
        if (userPoseTracker == null)
        {
            IsHeadsetTrackingActive = false;
            return;
        }

        LastHeadLocalPosition = localPosition;
        LastHeadLocalRotation = localRotation;
        userPoseTracker.SetTrackedPhysicalPose(
            new Vector2(localPosition.x, localPosition.z),
            HorizontalYawDegrees(localRotation));

        if (virtualHead != null)
        {
            headsetTransform = virtualHead;
            userPoseTracker.ConfigureVirtualUserRoot(virtualHead);
        }

        IsHeadsetTrackingActive = true;
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

        ResolveHeadsetTransform();
    }

    private void ResolveHeadsetTransform()
    {
        if (headsetTransform == null && Camera.main != null)
        {
            headsetTransform = Camera.main.transform;
        }
    }

    private static float HorizontalYawDegrees(Quaternion rotation)
    {
        var forward = rotation * Vector3.forward;
        var horizontal = new Vector2(forward.x, forward.z);
        return horizontal.sqrMagnitude > 0.0001f
            ? Mathf.Atan2(horizontal.x, horizontal.y) * Mathf.Rad2Deg
            : 0f;
    }
}
