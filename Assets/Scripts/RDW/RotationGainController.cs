using UnityEngine;

public sealed class RotationGainController : MonoBehaviour
{
    private enum GainState
    {
        Idle,
        WaitingForUserTurn,
        Applying,
        Completed
    }

    [Header("Safety")]
    [SerializeField] private bool gainEnabled;
    [SerializeField] private Transform virtualUserRoot;
    [SerializeField] private MotionDetector motionDetector;

    [Header("Gain Limits")]
    [SerializeField] private float gainMultiplier = 0.15f;
    [SerializeField] private float maxExtraYawDegrees = 8f;
    [SerializeField] private float targetPhysicalYawDegrees = 30f;

    private GainState state;
    private int expectedDirectionSign;
    private float accumulatedPhysicalYaw;
    private float appliedExtraYaw;

    public bool IsActive => state == GainState.WaitingForUserTurn || state == GainState.Applying;

    private void Awake()
    {
        ResolveReferences();
    }

    private void Update()
    {
        if (!gainEnabled || state == GainState.Idle || state == GainState.Completed || motionDetector == null)
        {
            return;
        }

        if (!motionDetector.IsTurning)
        {
            return;
        }

        var signedDelta = motionDetector.LastSignedYawDeltaDegrees;
        if (Mathf.Sign(signedDelta) != expectedDirectionSign)
        {
            StopGain();
            return;
        }

        if (state == GainState.WaitingForUserTurn)
        {
            state = GainState.Applying;
        }

        accumulatedPhysicalYaw += Mathf.Abs(signedDelta);
        var extraYaw = Mathf.Min(Mathf.Abs(signedDelta) * gainMultiplier, maxExtraYawDegrees - appliedExtraYaw);
        if (extraYaw > 0f && virtualUserRoot != null)
        {
            virtualUserRoot.Rotate(Vector3.up, extraYaw * expectedDirectionSign, Space.World);
            appliedExtraYaw += extraYaw;
        }

        if (accumulatedPhysicalYaw >= targetPhysicalYawDegrees || appliedExtraYaw >= maxExtraYawDegrees)
        {
            state = GainState.Completed;
        }
    }

    public void Configure(UserPoseTracker poseTracker, MotionDetector detector)
    {
        motionDetector = detector;
        if (virtualUserRoot == null && poseTracker != null)
        {
            var root = GameObject.Find("XR_Origin_or_UserStart");
            virtualUserRoot = root != null ? root.transform : null;
        }
    }

    public void QueueFromMovementInstruction(string movementInstruction)
    {
        if (movementInstruction == "turn_left_slowly")
        {
            BeginPendingTurn(1);
        }
        else if (movementInstruction == "turn_right_slowly")
        {
            BeginPendingTurn(-1);
        }
    }

    public void BeginPendingTurn(int directionSign)
    {
        expectedDirectionSign = directionSign < 0 ? -1 : 1;
        accumulatedPhysicalYaw = 0f;
        appliedExtraYaw = 0f;
        state = gainEnabled ? GainState.WaitingForUserTurn : GainState.Idle;
    }

    public void StopGain()
    {
        state = GainState.Idle;
        accumulatedPhysicalYaw = 0f;
        appliedExtraYaw = 0f;
    }

    private void ResolveReferences()
    {
        if (motionDetector == null)
        {
            motionDetector = FindObjectOfType<MotionDetector>();
        }

        if (virtualUserRoot == null)
        {
            var root = GameObject.Find("XR_Origin_or_UserStart");
            virtualUserRoot = root != null ? root.transform : null;
        }
    }
}
