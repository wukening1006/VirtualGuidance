using UnityEngine;

public sealed class MotionDetector : MonoBehaviour
{
    [SerializeField] private UserPoseTracker userPoseTracker;
    [SerializeField] private float turnStartDegreesPerSecond = 12f;
    [SerializeField] private float turnStopDegreesPerSecond = 4f;

    private bool hasPreviousForward;
    private Vector2 previousForward;

    public bool IsTurning { get; private set; }
    public float LastSignedYawDeltaDegrees { get; private set; }
    public float AngularSpeedDegreesPerSecond { get; private set; }

    private void Awake()
    {
        ResolveReferences();
    }

    private void Update()
    {
        if (userPoseTracker == null)
        {
            return;
        }

        Sample(userPoseTracker.PhysicalForwardXZ, Time.deltaTime);
    }

    public void Sample(Vector2 forward, float deltaTimeSeconds)
    {
        forward = forward.sqrMagnitude > 0.0001f ? forward.normalized : Vector2.up;
        if (!hasPreviousForward)
        {
            previousForward = forward;
            hasPreviousForward = true;
            return;
        }

        LastSignedYawDeltaDegrees = Vector2.SignedAngle(previousForward, forward);
        AngularSpeedDegreesPerSecond = Mathf.Abs(LastSignedYawDeltaDegrees) / Mathf.Max(deltaTimeSeconds, 0.0001f);
        previousForward = forward;

        if (!IsTurning && AngularSpeedDegreesPerSecond >= turnStartDegreesPerSecond)
        {
            IsTurning = true;
        }
        else if (IsTurning && AngularSpeedDegreesPerSecond <= turnStopDegreesPerSecond)
        {
            IsTurning = false;
        }
    }

    public void Configure(UserPoseTracker poseTracker)
    {
        userPoseTracker = poseTracker;
        hasPreviousForward = false;
        IsTurning = false;
        LastSignedYawDeltaDegrees = 0f;
        AngularSpeedDegreesPerSecond = 0f;
    }

    private void ResolveReferences()
    {
        if (userPoseTracker == null)
        {
            userPoseTracker = FindObjectOfType<UserPoseTracker>();
        }
    }
}
