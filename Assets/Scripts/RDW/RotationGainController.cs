using UnityEngine;

public sealed class RotationGainController : MonoBehaviour
{
    public enum GainState
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
    public bool GainEnabled => gainEnabled;
    public GainState State => state;
    public float AccumulatedPhysicalYawDegrees => accumulatedPhysicalYaw;
    public float AppliedExtraYawDegrees => appliedExtraYaw;

    private void Awake()
    {
        ResolveReferences();
    }

    private void Update()
    {
        if (motionDetector == null)
        {
            return;
        }

        ProcessTurnSample(motionDetector.IsTurning, motionDetector.LastSignedYawDeltaDegrees);
    }

    public void ProcessTurnSample(bool isTurning, float signedYawDeltaDegrees)
    {
        if (!gainEnabled || state == GainState.Idle || state == GainState.Completed)
        {
            return;
        }

        if (!isTurning)
        {
            if (state == GainState.Applying)
            {
                StopGain();
            }

            return;
        }

        if (Mathf.Abs(signedYawDeltaDegrees) <= 0.0001f || Mathf.Sign(signedYawDeltaDegrees) != expectedDirectionSign)
        {
            StopGain();
            return;
        }

        if (state == GainState.WaitingForUserTurn)
        {
            state = GainState.Applying;
        }

        accumulatedPhysicalYaw += Mathf.Abs(signedYawDeltaDegrees);
        var extraYaw = Mathf.Min(Mathf.Abs(signedYawDeltaDegrees) * gainMultiplier, maxExtraYawDegrees - appliedExtraYaw);
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
        if (poseTracker != null && poseTracker.VirtualUserRoot != null)
        {
            virtualUserRoot = poseTracker.VirtualUserRoot;
        }
        else if (virtualUserRoot == null)
        {
            var root = GameObject.Find("XR_Origin_or_UserStart");
            virtualUserRoot = root != null ? root.transform : null;
        }
    }

    public void SetGainEnabled(bool enabled)
    {
        gainEnabled = enabled;
        if (!gainEnabled)
        {
            StopGain();
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
