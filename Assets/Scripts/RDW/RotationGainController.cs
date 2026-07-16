using UnityEngine;

/// <summary>
/// 在用户主动执行指定方向转头时施加受限旋转增益，输入来自 MotionDetector。
/// 额外 yaw 作用于虚拟用户根节点，并受累计物理转角和最大增益角双重限制。
/// </summary>
public sealed class RotationGainController : MonoBehaviour
{
    /// <summary>旋转增益从等待用户动作到完成的运行状态。</summary>
    public enum GainState
    {
        /// <summary>没有待执行增益，或增益已被停止。</summary>
        Idle,
        /// <summary>已收到转向指令，等待检测到同方向的用户真实转头。</summary>
        WaitingForUserTurn,
        /// <summary>正在根据真实转头增量施加额外虚拟 yaw。</summary>
        Applying,
        /// <summary>达到物理转角或额外 yaw 上限，本次增益结束。</summary>
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

    /// <summary>是否正在等待或施加一次旋转增益。</summary>
    public bool IsActive => state == GainState.WaitingForUserTurn || state == GainState.Applying;
    /// <summary>Inspector 或实验条件是否允许旋转增益。</summary>
    public bool GainEnabled => gainEnabled;
    /// <summary>当前旋转增益状态。</summary>
    public GainState State => state;
    /// <summary>本次增益已观察到的同方向物理转角，单位：度。</summary>
    public float AccumulatedPhysicalYawDegrees => accumulatedPhysicalYaw;
    /// <summary>本次增益已施加到虚拟用户根节点的额外 yaw，单位：度。</summary>
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

    /// <summary>
    /// 处理一帧物理转头采样；用户停止或转向与指令不符时立即停止本次增益。
    /// </summary>
    /// <param name="signedYawDeltaDegrees">本帧水平转角；正值为左转，负值为右转，单位：度。</param>
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

        // 只增强用户主动执行的预期方向，反向或无效采样必须终止以避免替用户强制转向。
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

    /// <summary>注入物理转头检测器，并绑定接收额外 yaw 的虚拟用户根节点。</summary>
    public void Configure(UserPoseTracker poseTracker, MotionDetector detector)
    {
        if (detector != null) motionDetector = detector;
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

    /// <summary>启用或禁用旋转增益；禁用会立即清除当前待执行或正在执行的增益。</summary>
    public void SetGainEnabled(bool enabled)
    {
        gainEnabled = enabled;
        if (!gainEnabled)
        {
            StopGain();
        }
    }

    /// <summary>
    /// 将后端左右转 movement_instruction 转换为待执行增益；其他指令不改变当前状态。
    /// </summary>
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

    /// <summary>
    /// 开始等待指定方向的真实转头，并清零本次累计量。
    /// </summary>
    /// <param name="directionSign">方向符号；负值表示右转，零或正值表示左转。</param>
    public void BeginPendingTurn(int directionSign)
    {
        expectedDirectionSign = directionSign < 0 ? -1 : 1;
        accumulatedPhysicalYaw = 0f;
        appliedExtraYaw = 0f;
        state = gainEnabled ? GainState.WaitingForUserTurn : GainState.Idle;
    }

    /// <summary>停止当前旋转增益并清除累计角度，不回滚已经施加的虚拟 yaw。</summary>
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
