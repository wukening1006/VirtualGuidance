using System;
using UnityEngine;
using UnityEngine.Serialization;

/// <summary>
/// 表示 Rotation Gain 生命周期中需要记录的研究事件。
/// </summary>
public enum RotationGainEventType
{
    /// <summary>系统已请求用户向指定方向转动。</summary>
    TurnRequested,
    /// <summary>检测到用户开始按正确方向真实转动。</summary>
    UserTurnStarted,
    /// <summary>系统开始向虚拟根节点施加额外 yaw。</summary>
    GainStarted,
    /// <summary>用户已完成本轮所需物理转角。</summary>
    UserTurnCompleted,
    /// <summary>本轮 Rotation Gain 已完成并准备重算风险。</summary>
    GainCompleted,
    /// <summary>本轮 Gain 因取消或安全条件中止。</summary>
    GainCancelled
}

/// <summary>
/// 保存一条 Rotation Gain 生命周期事件及其发生时的累计角度。
/// </summary>
public sealed class RotationGainEventInfo
{
    /// <summary>事件类型。</summary>
    public RotationGainEventType EventType { get; }
    /// <summary>事件发生时的 Gain 状态。</summary>
    public RotationGainController.GainState State { get; }
    /// <summary>期望转向符号；正值为左转，负值为右转。</summary>
    public int DirectionSign { get; }
    /// <summary>取消、中止或状态说明。</summary>
    public string Reason { get; }
    /// <summary>事件发生时累计的同方向物理转角，单位：度。</summary>
    public float PhysicalYawDegrees { get; }
    /// <summary>事件发生时已施加的额外虚拟 yaw，单位：度。</summary>
    public float ExtraYawDegrees { get; }

    /// <summary>创建一条不可变 Rotation Gain 生命周期事件。</summary>
    public RotationGainEventInfo(
        RotationGainEventType eventType,
        RotationGainController.GainState state,
        int directionSign,
        string reason,
        float physicalYawDegrees,
        float extraYawDegrees)
    {
        EventType = eventType;
        State = state;
        DirectionSign = directionSign;
        Reason = reason ?? string.Empty;
        PhysicalYawDegrees = physicalYawDegrees;
        ExtraYawDegrees = extraYawDegrees;
    }
}

/// <summary>
/// 在用户主动执行指定方向转头时向 Virtual Rotation Root 施加受限旋转增益。
/// Guidance 只会准备转向；检测到正确方向的真实转动后才会进入 GainActive。
/// </summary>
public sealed class RotationGainController : MonoBehaviour
{
    /// <summary>Rotation Gain 从准备、执行到风险重算的完整软件生命周期。</summary>
    public enum GainState
    {
        /// <summary>当前没有待执行或正在执行的 Gain。</summary>
        Inactive,
        /// <summary>已解析转向指令并完成运行前检查。</summary>
        Prepared,
        /// <summary>等待用户开始正确方向的真实转动。</summary>
        WaitingForUserTurn,
        /// <summary>正在根据真实转头增量施加额外虚拟 yaw。</summary>
        GainActive,
        /// <summary>物理转向和 Gain 已完成。</summary>
        GainCompleted,
        /// <summary>等待风险重算和 TurnCue 完成处理。</summary>
        Reevaluating,
        /// <summary>因 Guidance 取消等正常外部原因结束。</summary>
        Cancelled,
        /// <summary>因反向、超时、风险、追踪或增益越界而中止。</summary>
        Aborted
    }

    private const float YawEpsilon = 0.0001f;

    [Header("安全依赖")]
    [Tooltip("Inspector 或 Condition Policy 是否允许 Rotation Gain。")]
    [SerializeField] private bool gainEnabled;
    [Tooltip("接收额外虚拟 yaw 的 XR Origin、Locomotion Root 或 Virtual Root；不得绑定 Camera/HMD。")]
    [FormerlySerializedAs("virtualUserRoot")]
    [SerializeField] private Transform virtualRotationRoot;
    [Tooltip("提供 Physical Room 转头增量的检测器。")]
    [SerializeField] private MotionDetector motionDetector;
    [Tooltip("提供 Critical Risk 与 Reset Required 状态；触发时立即中止 Gain。")]
    [SerializeField] private PhysicalRiskEvaluator riskEvaluator;
    [Tooltip("提供 Tracking 与校准有效性；非 Calibrated 状态会中止活动 Gain。")]
    [SerializeField] private PhysicalSpaceCalibration physicalSpaceCalibration;

    [Header("Gain 限制")]
    [Tooltip("真实水平转角转换为额外虚拟 yaw 的比例。")]
    [SerializeField] private float gainMultiplier = 0.15f;
    [Tooltip("单次允许施加的最大额外虚拟 yaw，单位：度。")]
    [SerializeField] private float maxExtraYawDegrees = 8f;
    [Tooltip("完成本轮转向所需的累计物理转角，单位：度。")]
    [SerializeField] private float targetPhysicalYawDegrees = 30f;
    [Tooltip("GainActive 后允许用户停止转动的最长时间，单位：秒。")]
    [SerializeField] private float turnStopTimeoutSeconds = 0.75f;

    private GainState state = GainState.Inactive;
    private int expectedDirectionSign;
    private float accumulatedPhysicalYaw;
    private float appliedExtraYaw;
    private float stationaryDurationSeconds;
    private string lastTerminationReason = string.Empty;

    /// <summary>Gain 状态发生变化时触发。</summary>
    public event Action<GainState> StateChanged;
    /// <summary>TurnRequested 等研究事件发生时触发。</summary>
    public event Action<RotationGainEventInfo> LifecycleEventOccurred;
    /// <summary>Gain 完成并进入 Reevaluating 时触发，由协调器执行风险重算。</summary>
    public event Action ReevaluationRequested;

    /// <summary>是否处于一次尚未结束的 Gain 生命周期。</summary>
    public bool IsActive => state == GainState.Prepared ||
                            state == GainState.WaitingForUserTurn ||
                            state == GainState.GainActive ||
                            state == GainState.Reevaluating;
    /// <summary>是否正在实际向 Virtual Rotation Root 施加额外 yaw。</summary>
    public bool IsApplyingGain => state == GainState.GainActive;
    /// <summary>Inspector 或实验条件是否允许旋转增益。</summary>
    public bool GainEnabled => gainEnabled;
    /// <summary>当前 Rotation Gain 状态。</summary>
    public GainState State => state;
    /// <summary>本次 Gain 期望方向；正值为左转，负值为右转。</summary>
    public int ExpectedDirectionSign => expectedDirectionSign;
    /// <summary>本次 Gain 已观察到的同方向物理转角，单位：度。</summary>
    public float AccumulatedPhysicalYawDegrees => accumulatedPhysicalYaw;
    /// <summary>本次 Gain 已施加到虚拟根节点的额外 yaw，单位：度。</summary>
    public float AppliedExtraYawDegrees => appliedExtraYaw;
    /// <summary>最近一次取消或中止原因。</summary>
    public string LastTerminationReason => lastTerminationReason;
    /// <summary>当前通过作用对象校验的 Rotation Gain 根节点。</summary>
    public Transform RotationRoot => IsValidRotationRoot(virtualRotationRoot) ? virtualRotationRoot : null;

    private void Awake()
    {
        ResolveReferences();
    }

    private void OnEnable()
    {
        BindSafetySources();
    }

    private void OnDisable()
    {
        UnbindSafetySources();
    }

    private void Update()
    {
        if (motionDetector == null)
        {
            return;
        }

        ProcessTurnSample(
            motionDetector.IsTurning,
            motionDetector.LastSignedYawDeltaDegrees,
            Time.unscaledDeltaTime);
    }

    /// <summary>
    /// 处理一帧物理转头采样；只有正确方向的真实转动才能启动 Gain。
    /// </summary>
    /// <param name="isTurning">MotionDetector 当前是否判定用户正在转动。</param>
    /// <param name="signedYawDeltaDegrees">本帧水平转角；正值为左转，负值为右转，单位：度。</param>
    /// <param name="deltaTimeSeconds">本帧持续时间，单位：秒；负值时使用 unscaledDeltaTime。</param>
    public void ProcessTurnSample(bool isTurning, float signedYawDeltaDegrees, float deltaTimeSeconds = -1f)
    {
        if (state != GainState.WaitingForUserTurn && state != GainState.GainActive)
        {
            return;
        }

        if (!gainEnabled)
        {
            CancelGain("gain_disabled");
            return;
        }

        if (!IsValidRotationRoot(virtualRotationRoot))
        {
            AbortGain("rotation_root_invalid");
            return;
        }

        if (physicalSpaceCalibration != null && !physicalSpaceCalibration.IsCalibrationValid)
        {
            AbortGain("tracking_invalid");
            return;
        }

        var sampleDuration = deltaTimeSeconds >= 0f ? deltaTimeSeconds : Time.unscaledDeltaTime;
        if (!isTurning || Mathf.Abs(signedYawDeltaDegrees) <= YawEpsilon)
        {
            if (state == GainState.GainActive)
            {
                stationaryDurationSeconds += Mathf.Max(0f, sampleDuration);
                if (stationaryDurationSeconds >= Mathf.Max(0.01f, turnStopTimeoutSeconds))
                {
                    AbortGain("turn_stopped_timeout");
                }
            }

            return;
        }

        stationaryDurationSeconds = 0f;
        if (Mathf.Sign(signedYawDeltaDegrees) != expectedDirectionSign)
        {
            AbortGain("reverse_turn");
            return;
        }

        if (state == GainState.WaitingForUserTurn)
        {
            EmitLifecycleEvent(RotationGainEventType.UserTurnStarted);
            TransitionTo(GainState.GainActive);
            EmitLifecycleEvent(RotationGainEventType.GainStarted);
        }

        var physicalYaw = Mathf.Abs(signedYawDeltaDegrees);
        var requestedExtraYaw = physicalYaw * Mathf.Max(0f, gainMultiplier);
        if (appliedExtraYaw + requestedExtraYaw > Mathf.Max(0f, maxExtraYawDegrees) + YawEpsilon)
        {
            AbortGain("gain_limit_exceeded");
            return;
        }

        accumulatedPhysicalYaw += physicalYaw;
        if (requestedExtraYaw > 0f)
        {
            virtualRotationRoot.Rotate(Vector3.up, requestedExtraYaw * expectedDirectionSign, Space.World);
            appliedExtraYaw += requestedExtraYaw;
        }

        if (accumulatedPhysicalYaw >= Mathf.Max(YawEpsilon, targetPhysicalYawDegrees))
        {
            CompleteUserTurn();
        }
    }

    /// <summary>
    /// 注入 Gain 运行依赖，并迁移风险与校准状态订阅。
    /// </summary>
    public void Configure(
        UserPoseTracker poseTracker,
        MotionDetector detector,
        PhysicalRiskEvaluator evaluator = null,
        PhysicalSpaceCalibration calibration = null)
    {
        UnbindSafetySources();
        if (detector != null) motionDetector = detector;
        if (evaluator != null) riskEvaluator = evaluator;
        if (calibration != null) physicalSpaceCalibration = calibration;
        if (poseTracker != null && poseTracker.VirtualRotationRoot != null)
        {
            virtualRotationRoot = poseTracker.VirtualRotationRoot;
        }
        else if (!IsValidRotationRoot(virtualRotationRoot))
        {
            var root = GameObject.Find("XR_Origin_or_UserStart");
            virtualRotationRoot = root != null && IsValidRotationRoot(root.transform) ? root.transform : null;
        }
        BindSafetySources();
    }

    /// <summary>
    /// 显式设置 Rotation Gain 根节点；Camera/HMD 引用会被拒绝且不会覆盖有效配置。
    /// </summary>
    /// <param name="root">XR Origin、Locomotion Root 或 Virtual Root。</param>
    /// <returns>引用满足 Gain 作用对象契约时返回 true。</returns>
    public bool ConfigureRotationRoot(Transform root)
    {
        if (!IsValidRotationRoot(root))
        {
            return false;
        }

        virtualRotationRoot = root;
        return true;
    }

    /// <summary>
    /// 设置软件验证使用的 Gain 限制。
    /// </summary>
    /// <param name="multiplier">真实转角转换为额外 yaw 的比例。</param>
    /// <param name="maxExtraYaw">单次最大额外 yaw，单位：度。</param>
    /// <param name="targetPhysicalYaw">完成所需物理转角，单位：度。</param>
    /// <param name="stopTimeout">停止转动超时，单位：秒。</param>
    public void ConfigureLimits(float multiplier, float maxExtraYaw, float targetPhysicalYaw, float stopTimeout)
    {
        gainMultiplier = Mathf.Max(0f, multiplier);
        maxExtraYawDegrees = Mathf.Max(0f, maxExtraYaw);
        targetPhysicalYawDegrees = Mathf.Max(YawEpsilon, targetPhysicalYaw);
        turnStopTimeoutSeconds = Mathf.Max(0.01f, stopTimeout);
    }

    /// <summary>启用或禁用 Rotation Gain；禁用会取消尚未结束的生命周期。</summary>
    public void SetGainEnabled(bool enabled)
    {
        gainEnabled = enabled;
        if (!gainEnabled)
        {
            CancelGain("gain_disabled");
        }
        else if (state == GainState.Cancelled || state == GainState.Aborted)
        {
            TransitionTo(GainState.Inactive);
        }
    }

    /// <summary>
    /// 将后端左右转 movement_instruction 转换为待执行 Gain；展示 Guidance 本身不会启动 Gain。
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
    /// 准备指定方向的真实转头，并依次进入 Prepared 与 WaitingForUserTurn。
    /// </summary>
    /// <param name="directionSign">方向符号；负值表示右转，零或正值表示左转。</param>
    public void BeginPendingTurn(int directionSign)
    {
        ResetMetrics(directionSign);
        if (!gainEnabled)
        {
            TransitionTo(GainState.Inactive);
            return;
        }

        TransitionTo(GainState.Prepared);
        if (!IsValidRotationRoot(virtualRotationRoot))
        {
            AbortGain("rotation_root_invalid");
            return;
        }

        if (physicalSpaceCalibration != null && !physicalSpaceCalibration.IsCalibrationValid)
        {
            AbortGain("tracking_invalid");
            return;
        }

        EmitLifecycleEvent(RotationGainEventType.TurnRequested);
        TransitionTo(GainState.WaitingForUserTurn);
    }

    /// <summary>因 Guidance 取消等正常外部原因取消当前 Gain。</summary>
    public void CancelGain(string reason)
    {
        if (!IsActive && state != GainState.GainCompleted)
        {
            return;
        }

        lastTerminationReason = string.IsNullOrWhiteSpace(reason) ? "cancelled" : reason;
        TransitionTo(GainState.Cancelled);
        EmitLifecycleEvent(RotationGainEventType.GainCancelled, lastTerminationReason);
        stationaryDurationSeconds = 0f;
    }

    /// <summary>因安全条件异常中止当前 Gain，并保留已施加 yaw 供日志还原。</summary>
    public void AbortGain(string reason)
    {
        if (!IsActive && state != GainState.GainCompleted && state != GainState.Prepared)
        {
            return;
        }

        lastTerminationReason = string.IsNullOrWhiteSpace(reason) ? "aborted" : reason;
        TransitionTo(GainState.Aborted);
        EmitLifecycleEvent(RotationGainEventType.GainCancelled, lastTerminationReason);
        stationaryDurationSeconds = 0f;
    }

    /// <summary>
    /// 完成风险重算；安全时回到 Inactive，不安全时进入 Aborted。
    /// </summary>
    /// <param name="safeToContinue">最新 Physical Risk 是否允许继续任务。</param>
    /// <param name="reason">不安全时的中止原因。</param>
    public void CompleteReevaluation(bool safeToContinue, string reason = "")
    {
        if (state != GainState.Reevaluating)
        {
            return;
        }

        if (!safeToContinue)
        {
            AbortGain(string.IsNullOrWhiteSpace(reason) ? "reevaluation_unsafe" : reason);
            return;
        }

        TransitionTo(GainState.Inactive);
    }

    /// <summary>兼容既有调用的停止入口；活动 Gain 会按正常取消记录。</summary>
    public void StopGain()
    {
        CancelGain("stopped");
    }

    private void CompleteUserTurn()
    {
        EmitLifecycleEvent(RotationGainEventType.UserTurnCompleted);
        TransitionTo(GainState.GainCompleted);
        EmitLifecycleEvent(RotationGainEventType.GainCompleted);
        TransitionTo(GainState.Reevaluating);
        ReevaluationRequested?.Invoke();
    }

    private void HandleRiskStateChanged(PhysicalRiskState riskState)
    {
        if (!IsActive || riskState == null)
        {
            return;
        }

        if (riskState.reset_required)
        {
            AbortGain("reset_required");
        }
        else if (string.Equals(riskState.boundary_risk, "critical", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(riskState.reset_risk, "critical", StringComparison.OrdinalIgnoreCase))
        {
            AbortGain("risk_critical");
        }
    }

    private void HandleCalibrationStateChanged(PhysicalCalibrationState calibrationState)
    {
        if (IsActive && calibrationState != PhysicalCalibrationState.Calibrated)
        {
            AbortGain("tracking_invalid");
        }
    }

    private void ResolveReferences()
    {
        if (motionDetector == null) motionDetector = FindObjectOfType<MotionDetector>();

        if (!IsValidRotationRoot(virtualRotationRoot))
        {
            var root = GameObject.Find("XR_Origin_or_UserStart");
            virtualRotationRoot = root != null && IsValidRotationRoot(root.transform) ? root.transform : null;
        }
    }

    private void BindSafetySources()
    {
        if (!isActiveAndEnabled)
        {
            return;
        }

        if (riskEvaluator != null)
        {
            riskEvaluator.RiskStateChanged -= HandleRiskStateChanged;
            riskEvaluator.RiskStateChanged += HandleRiskStateChanged;
        }

        if (physicalSpaceCalibration != null)
        {
            physicalSpaceCalibration.StateChanged -= HandleCalibrationStateChanged;
            physicalSpaceCalibration.StateChanged += HandleCalibrationStateChanged;
        }
    }

    private void UnbindSafetySources()
    {
        if (riskEvaluator != null) riskEvaluator.RiskStateChanged -= HandleRiskStateChanged;
        if (physicalSpaceCalibration != null) physicalSpaceCalibration.StateChanged -= HandleCalibrationStateChanged;
    }

    private void ResetMetrics(int directionSign)
    {
        expectedDirectionSign = directionSign < 0 ? -1 : 1;
        accumulatedPhysicalYaw = 0f;
        appliedExtraYaw = 0f;
        stationaryDurationSeconds = 0f;
        lastTerminationReason = string.Empty;
        if (state != GainState.Inactive)
        {
            TransitionTo(GainState.Inactive);
        }
    }

    private void TransitionTo(GainState nextState)
    {
        if (state == nextState)
        {
            return;
        }

        state = nextState;
        StateChanged?.Invoke(state);
    }

    private void EmitLifecycleEvent(RotationGainEventType eventType, string reason = "")
    {
        LifecycleEventOccurred?.Invoke(new RotationGainEventInfo(
            eventType,
            state,
            expectedDirectionSign,
            reason,
            accumulatedPhysicalYaw,
            appliedExtraYaw));
    }

    private static bool IsValidRotationRoot(Transform root)
    {
        return root != null && root.GetComponent<Camera>() == null;
    }
}
