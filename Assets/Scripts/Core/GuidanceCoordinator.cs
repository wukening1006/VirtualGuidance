using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 表示单次 Guidance 请求从规划到执行结束的生命周期。
/// </summary>
public enum GuidanceRequestPhase
{
    /// <summary>当前没有 Guidance 请求。</summary>
    None,
    /// <summary>请求已发出，等待响应。</summary>
    Requested,
    /// <summary>响应已到达，尚未完成校验。</summary>
    Received,
    /// <summary>响应已通过后端与本地校验。</summary>
    Verified,
    /// <summary>Guidance 正在展示并等待用户完成动作。</summary>
    Executing,
    /// <summary>用户完成当前 Guidance 对应动作。</summary>
    Completed,
    /// <summary>请求或执行因暂停、到达或新决策被取消。</summary>
    Cancelled,
    /// <summary>响应不再对应当前决策或状态。</summary>
    Expired,
    /// <summary>请求、解析或验证发生失败。</summary>
    Failed
}

/// <summary>
/// 描述一次运行期安全干预，保留原始响应、判定与原因供日志还原。
/// </summary>
public sealed class RuntimeInterventionInfo
{
    /// <summary>被运行期安全策略中断的原始 Guidance。</summary>
    public GuidanceResponse Response { get; }
    /// <summary>运行时拒绝原因。</summary>
    public string Reason { get; }
    /// <summary>本次运行期安全判定的动作类型。</summary>
    public RuntimeGuidanceSafetyDecision Decision { get; }
    /// <summary>本次决策是否真正启动了自动 Reset。</summary>
    public bool StartReset { get; }
    /// <summary>本次自动 Reset 是会话内第几次尝试；非 Reset 决策为 0。</summary>
    public int ResetAttempt { get; }
    /// <summary>作出决策时是否存在本地验证通过的安全语义候选。</summary>
    public bool SafeCandidateAvailable { get; }

    /// <summary>创建一条不可变 Runtime Intervention 事件。</summary>
    public RuntimeInterventionInfo(
        GuidanceResponse response,
        string reason,
        RuntimeGuidanceSafetyDecision decision = RuntimeGuidanceSafetyDecision.HardStop,
        bool startReset = false,
        int resetAttempt = 0,
        bool safeCandidateAvailable = false)
    {
        Response = response;
        Reason = reason;
        Decision = decision;
        StartReset = startReset;
        ResetAttempt = Math.Max(0, resetAttempt);
        SafeCandidateAvailable = safeCandidateAvailable;
    }
}

/// <summary>
/// 表示自动物理朝向恢复的显式生命周期。
/// </summary>
public enum GuidanceRecoveryState
{
    /// <summary>当前没有自动恢复。</summary>
    Inactive,
    /// <summary>正在停止 Guidance、Gain 与在途请求。</summary>
    StoppingGuidance,
    /// <summary>正在全角域连续搜索安全航向。</summary>
    SelectingHeading,
    /// <summary>当前没有明确 Safe 航向，任务保持暂停并等待最新可信物理事实。</summary>
    HoldingForSafeHeading,
    /// <summary>等待参与者原地转向冻结朝向。</summary>
    WaitingForUserTurn,
    /// <summary>参与者已到达目标朝向，正在等待稳定。</summary>
    Stabilizing,
    /// <summary>正在使用最新物理事实复验冻结朝向。</summary>
    Revalidating,
    /// <summary>物理恢复已完成，正在恢复任务并重新规划。</summary>
    Replanning,
    /// <summary>Reset 后没有可执行候选，正在主动寻找导航恢复路径。</summary>
    NavigationRecovery,
    /// <summary>导航恢复暂时没有结果，等待事实变化后再次主动恢复。</summary>
    RecoveryHold,
    /// <summary>物理状态已明确不可恢复，流程终止。</summary>
    Terminated
}

/// <summary>
/// 保存一次自动恢复状态转换的前后状态、原因与当时冻结的恢复计划。
/// </summary>
public sealed class GuidanceRecoveryTransitionInfo
{
    /// <summary>转换前状态。</summary>
    public GuidanceRecoveryState PreviousState { get; }
    /// <summary>转换后状态。</summary>
    public GuidanceRecoveryState NextState { get; }
    /// <summary>触发转换的稳定诊断原因。</summary>
    public string Reason { get; }
    /// <summary>转换发生时冻结的恢复计划；尚未选向时可为 null。</summary>
    public RecoveryHeadingPlan RecoveryPlan { get; }

    /// <summary>创建不可变恢复状态转换事件。</summary>
    public GuidanceRecoveryTransitionInfo(
        GuidanceRecoveryState previousState,
        GuidanceRecoveryState nextState,
        string reason,
        RecoveryHeadingPlan recoveryPlan)
    {
        PreviousState = previousState;
        NextState = nextState;
        Reason = reason ?? string.Empty;
        RecoveryPlan = recoveryPlan;
    }
}

/// <summary>
/// 描述 Guidance 因暂无可执行候选进入或退出重规划等待的诊断信息。
/// </summary>
public sealed class ReplanHoldInfo
{
    /// <summary>当前是否处于重规划等待。</summary>
    public bool IsHolding { get; }
    /// <summary>进入或更新等待状态的稳定原因。</summary>
    public string Reason { get; }
    /// <summary>最近一次实际评估的规划指纹。</summary>
    public string PlanningFingerprint { get; }
    /// <summary>最近一次实际评估使用的风险修订号。</summary>
    public int RiskRevision { get; }
    /// <summary>当前连续无候选规划次数。</summary>
    public int RetryCount { get; }
    /// <summary>下一次允许评估新修订的退避时间，单位：秒。</summary>
    public float RetryDelaySeconds { get; }

    /// <summary>创建不可变的重规划等待诊断信息。</summary>
    public ReplanHoldInfo(
        bool isHolding,
        string reason,
        string planningFingerprint,
        int riskRevision,
        int retryCount,
        float retryDelaySeconds)
    {
        IsHolding = isHolding;
        Reason = reason ?? string.Empty;
        PlanningFingerprint = planningFingerprint ?? string.Empty;
        RiskRevision = Math.Max(0, riskRevision);
        RetryCount = Math.Max(0, retryCount);
        RetryDelaySeconds = Mathf.Max(0f, retryDelaySeconds);
    }
}

/// <summary>
/// 标识一次语义重规划的业务触发原因。
/// </summary>
public enum SemanticReplanReason
{
    /// <summary>任务刚开始。</summary>
    TaskStarted,
    /// <summary>当前目标已到达。</summary>
    TargetArrived,
    /// <summary>当前目标或规划方向已不可达。</summary>
    TargetUnreachable,
    /// <summary>当前 Guidance 已过期。</summary>
    GuidanceExpired,
    /// <summary>用户或研究员意图发生变化。</summary>
    UserIntentChanged,
    /// <summary>Reset 已完成并通过复验。</summary>
    ResetCompleted,
    /// <summary>用户未按当前转向动作继续执行。</summary>
    UserDeviated,
    /// <summary>后端服务已恢复。</summary>
    BackendRecovered,
    /// <summary>位姿、风险或其他运行期事实已更新。</summary>
    RuntimeFactsChanged
}

/// <summary>
/// 保存语义重规划触发时的原因与目标锁快照。
/// </summary>
public sealed class SemanticReplanInfo
{
    /// <summary>本次重规划的业务原因。</summary>
    public SemanticReplanReason Reason { get; }
    /// <summary>触发时的任务状态版本。</summary>
    public int StateVersion { get; }
    /// <summary>触发时仍锁定的语义目标 ID；没有锁时为空。</summary>
    public string LockedTargetId { get; }
    /// <summary>触发时的任务运行状态。</summary>
    public GuidanceRuntimeState State { get; }

    /// <summary>创建不可变的语义重规划诊断信息。</summary>
    public SemanticReplanInfo(SemanticReplanReason reason, GuidanceRuntimeState state)
    {
        Reason = reason;
        State = state;
        StateVersion = state != null ? state.stateVersion : 0;
        LockedTargetId = state != null && state.HasLockedSemanticGoal
            ? state.guidanceTargetId ?? string.Empty
            : string.Empty;
    }
}

/// <summary>
/// 协调任务状态、物理风险与后端 guidance 请求，并将通过本地安全校验的响应交给表现层执行。
/// 输入来自任务、导航和 RDW 模块，执行结果提供给导览表现、到达检测与旋转增益控制器。
/// </summary>
public sealed class GuidanceCoordinator : MonoBehaviour
{
    [SerializeField] private AgentClient agentClient;
    [SerializeField] private GuidanceExecutor guidanceExecutor;
    [SerializeField] private GuidanceTaskController taskController;
    [SerializeField] private NavigationTargetManager navigationTargets;
    [SerializeField] private SemanticTargetSelector semanticTargetSelector;
    [SerializeField] private ArrivalDetector arrivalDetector;
    [SerializeField] private PhysicalRiskEvaluator riskEvaluator;
    [SerializeField] private LocalGuidanceValidator localValidator;
    [SerializeField] private PauseResetController pauseResetController;
    [SerializeField] private RotationGainController rotationGainController;
    [SerializeField] private MotionDetector motionDetector;
    [SerializeField] private RejectionPanel rejectionPanel;

    [Header("运行期安全策略")]
    [Tooltip("是否允许在当前目标不安全但存在安全语义候选时执行 SoftReroute；默认关闭，关闭后回退到 Reset 或 HardStop。")]
    [SerializeField] private bool enableSoftRerouteSafetyPolicy = false;

    [Header("TurnCue 物理完成")]
    [Tooltip("目标相对偏航不超过该角度时不创建 TurnCue，单位：度。")]
    [SerializeField] private float turnCueNoCueToleranceDegrees = 12f;
    [Tooltip("累计反向转动达到该角度时中止 TurnCue，单位：度。")]
    [SerializeField] private float turnCueReverseAbortDegrees = 5f;
    [Tooltip("进入目标朝向后需要保持稳定的时间，单位：秒。")]
    [SerializeField] private float turnCueStableCompletionSeconds = 0.25f;
    [Tooltip("等待 TurnCue 完成的最长时间，单位：秒。")]
    [SerializeField] private float turnCueTimeoutSeconds = 8f;

    [Header("Reset 恢复")]
    [Tooltip("用户完成安全转向后，主动复验前需要保持稳定的时间，单位：秒。")]
    [SerializeField, Min(0f)] private float resetStabilizationSeconds = 0.25f;
    [Tooltip("没有明确 Safe 恢复航向时允许等待的最长时间，单位：秒。")]
    [SerializeField, Min(0.1f)] private float recoveryHoldBudgetSeconds = 8f;
    [Tooltip("Recovery Hold 期间两次主动重试的最短间隔，单位：秒。")]
    [SerializeField, Min(0.05f)] private float recoveryHoldRetryIntervalSeconds = 0.5f;
    [Tooltip("单次 Recovery Hold 允许执行的最大选向次数。")]
    [SerializeField, Min(1)] private int recoveryHoldMaxAttempts = 16;

    [Header("Recovery Search")]
    [Tooltip("连续恢复航向搜索的全角域分层参数与 Safe 候选评分。")]
    [SerializeField] private RecoveryHeadingSearchSettings recoveryHeadingSearchSettings =
        new RecoveryHeadingSearchSettings();

    [Header("重规划等待")]
    [Tooltip("暂无可执行候选后首次重试的最短间隔，单位：秒。")]
    [SerializeField, Min(0.1f)] private float replanInitialRetryDelaySeconds = 0.5f;
    [Tooltip("连续暂无候选时允许使用的最大重试间隔，单位：秒。")]
    [SerializeField, Min(0.1f)] private float replanMaxRetryDelaySeconds = 2f;
    [Tooltip("Hold 期间触发重新检查的最小物理位移，单位：米。")]
    [SerializeField, Min(0.01f)] private float replanWakePositionMeters = 0.25f;
    [Tooltip("Hold 期间触发重新检查的最小物理转角，单位：度。")]
    [SerializeField, Range(1f, 180f)] private float replanWakeHeadingDegrees = 12f;

    [Header("Reset 后导航恢复")]
    [Tooltip("一次 Initial Reset 后允许执行的 Recovery Reset 次数；最小恢复策略限制为 0 或 1。")]
    [SerializeField, Range(0, 1)] private int navigationRecoveryResetBudget = 1;
    [Tooltip("Navigation Recovery Hold 的最长等待时间，单位：秒。")]
    [SerializeField, Min(0.1f)] private float navigationRecoveryHoldBudgetSeconds = 8f;

    private GuidanceRequestSnapshot activeSnapshot;
    private GuidanceResponse activeResponse;
    private IGuidanceRequestClient requestClient;
    private GuidanceRequestPhase requestPhase;
    private readonly RuntimeGuidanceSafetyPolicy runtimeSafetyPolicy = new RuntimeGuidanceSafetyPolicy();
    private int consecutiveUnsafeSamples;
    private string sampledUnsafeTargetId = string.Empty;
    private string lastRuntimeRiskSnapshotId = string.Empty;
    private bool runtimeReplanPending;
    private readonly TurnCueCompletionTracker turnCueCompletionTracker = new TurnCueCompletionTracker();
    private bool handlingGainTermination;
    private bool recoveryDecisionPending;
    private bool navigationRecoveryDeferred;
    private PhysicalRiskState deferredNavigationRecoveryRiskState;
    private string deferredNavigationRecoveryPlanningFingerprint = string.Empty;
    private int automaticResetAttempts;
    private string resetAbandonedTargetId = string.Empty;
    private readonly RecoveryHeadingPlanner recoveryHeadingPlanner = new RecoveryHeadingPlanner();
    private readonly PostResetContinuationEvaluator postResetContinuationEvaluator =
        new PostResetContinuationEvaluator();
    private RecoveryHeadingSearchResult latestRecoverySearchResult;
    private string latestRecoverySearchFingerprint = string.Empty;
    private RecoveryHeadingPlan frozenRecoveryPlan;
    private GuidanceRecoveryState recoveryState = GuidanceRecoveryState.Inactive;
    private float recoveryRevalidationAt = float.PositiveInfinity;
    private float recoveryHoldStartedAt = float.PositiveInfinity;
    private float nextRecoveryHoldAttemptAt = float.PositiveInfinity;
    private int recoveryHoldAttemptCount;
    private int recoveryHoldRiskRevision = -1;
    private Vector2 recoveryHoldPhysicalPosition;
    private Vector2 recoveryHoldPhysicalForward = Vector2.up;
    private bool recoveryHoldPoseCaptured;
    private bool holdForReplan;
    private int holdRiskRevision = -1;
    private int holdTaskStateVersion;
    private string holdPlanningFingerprint = string.Empty;
    private string holdReason = string.Empty;
    private Vector2 holdPhysicalPosition;
    private Vector2 holdPhysicalForward = Vector2.up;
    private bool holdPhysicalPoseCaptured;
    private int replanRetryCount;
    private float nextReplanAttemptTime;
    private float currentReplanRetryDelaySeconds;
    private bool planningRequestInProgress;
    private string lastDeterministicRejectionFingerprint = string.Empty;
    private bool replanHoldRequiresNewFacts;
    private bool postResetNavigationRecoveryPending;
    private int navigationRecoveryResetAttempts;
    private float navigationRecoveryHoldStartedAt = float.PositiveInfinity;
    private Vector2 lastCompletedRecoveryForward = Vector2.up;
    private bool hasLastCompletedRecoveryForward;
    private PostResetContinuationAssessment latestPostResetContinuation;
    private RdwPlanningSettings rdwPlanningSettings;
    private ExperimentProfile experimentProfile;
    private RdwPlanningPipeline rdwPlanningPipeline;
    private RdwFrameCoordinator rdwPlanningFrameCoordinator;
    private IRdwRouteSelectionClient routeSelectionClient;
    private ISemanticPlanClient semanticPlanClient;
    private StaticSceneSemanticGraph semanticPlanningGraph;
    private readonly SemanticLocalGraphBuilder semanticLocalGraphBuilder = new SemanticLocalGraphBuilder();
    private GuidanceExecutionSafetyEvaluator executionSafetyEvaluator;
    private SemanticPlanRequest pendingSemanticPlanRequest;
    private string pendingSemanticPlanId = string.Empty;
    private ExperimentLogger rdwPlanningLogger;
    private readonly PlanCache rdwPlanCache = new PlanCache();
    private readonly PlanningCallGate rdwPlanningCallGate = new PlanningCallGate();
    private RdwRoutePlanningResult pendingRdwPlanningResult;
    private RdwRouteSelectionRequest pendingRdwRouteSelectionRequest;
    private GuidanceRequest pendingRdwGuidanceRequest;
    private PlanningCallIdentity pendingRdwCallIdentity;
    private RdwRoutePlanningResult lastShadowPlanningResult;
    private RdwRouteSelectionResponse lastShadowSelectionResponse;

    public event Action<string> GuidanceRejected;
    /// <summary>到达中间路线目标并准备请求下一段 Guidance 时触发。</summary>
    public event Action<GuidanceRuntimeState> Replanning;
    /// <summary>语义重规划实际开始时发布原因与目标锁快照。</summary>
    public event Action<SemanticReplanInfo> SemanticReplanning;
    /// <summary>到达最终目标、任务进入 Completed 时触发。</summary>
    public event Action<GuidanceRuntimeState> TaskCompleted;
    /// <summary>Guidance 请求生命周期发生变化时触发。</summary>
    public event Action<GuidanceRequestPhase> RequestPhaseChanged;
    /// <summary>Hard Runtime Safety 阻止危险 Guidance 时触发。</summary>
    public event Action<RuntimeInterventionInfo> RuntimeIntervention;
    /// <summary>语义规划请求失败时触发；失败不会改变任务完成状态。</summary>
    public event Action<string> PlanningFailed;
    /// <summary>通过全部验证并开始执行 Guidance 时触发。</summary>
    public event Action<GuidanceResponse> GuidanceExecuted;
    /// <summary>响应因请求身份或状态变化过期时触发。</summary>
    public event Action<string> GuidanceExpired;
    /// <summary>新 Guidance Target 已选定并已发起对应请求时触发。</summary>
    public event Action<GuidanceTarget> GuidanceTargetSelected;
    /// <summary>用户到达并确认完成当前可导航目标时触发。</summary>
    public event Action<GuidanceTarget> TargetReached;
    /// <summary>NavigableDetour 完成且准备返回原路线时触发。</summary>
    public event Action<GuidanceTarget> DetourCompleted;
    /// <summary>自动物理恢复状态发生变化时触发。</summary>
    public event Action<GuidanceRecoveryState> RecoveryStateChanged;
    /// <summary>自动物理恢复状态发生变化时发布完整诊断上下文。</summary>
    public event Action<GuidanceRecoveryTransitionInfo> RecoveryTransitioned;
    /// <summary>完成一次 Continuous Recovery Heading Search 时发布只读诊断结果。</summary>
    public event Action<RecoveryHeadingSearchResult> RecoverySearchCompleted;
    /// <summary>Navigation Recovery 状态机或 Heading 选择产生诊断记录时触发。</summary>
    public event Action<NavigationRecoveryDiagnosticInfo> NavigationRecoveryDiagnosed;
    /// <summary>进入、更新或退出重规划等待时触发。</summary>
    public event Action<ReplanHoldInfo> ReplanHoldChanged;

    /// <summary>
    /// 当前任务运行状态；任务控制器未配置时返回 null。
    /// </summary>
    public GuidanceRuntimeState RuntimeState => taskController != null ? taskController.RuntimeState : null;
    /// <summary>当前 Guidance 请求生命周期状态。</summary>
    public GuidanceRequestPhase RequestPhase => requestPhase;
    /// <summary>当前活动请求 ID；尚未构建请求时为空。</summary>
    public string ActiveRequestId => activeSnapshot != null ? activeSnapshot.requestId : string.Empty;
    /// <summary>当前活动决策 ID；尚未构建请求时为空。</summary>
    public string ActiveDecisionId => activeSnapshot != null ? activeSnapshot.decisionId : string.Empty;
    /// <summary>当前由任务状态持有的 Guidance Target。</summary>
    public GuidanceTarget CurrentGuidanceTarget => taskController != null
        ? taskController.RuntimeState.CurrentGuidanceTarget
        : null;
    /// <summary>当前会话已真正启动的自动 Reset 次数。</summary>
    public int AutomaticResetAttempts => automaticResetAttempts;
    /// <summary>当前自动物理恢复状态。</summary>
    public GuidanceRecoveryState RecoveryState => recoveryState;
    /// <summary>当前恢复流程冻结的具体物理朝向计划。</summary>
    public RecoveryHeadingPlan FrozenRecoveryPlan => frozenRecoveryPlan;
    /// <summary>最近一次 Continuous Recovery Heading Search 的完整诊断结果。</summary>
    public RecoveryHeadingSearchResult LatestRecoverySearchResult => latestRecoverySearchResult;
    /// <summary>当前是否正在执行自动物理朝向恢复。</summary>
    public bool IsAutomaticRecoveryActive =>
        recoveryState != GuidanceRecoveryState.Inactive &&
        recoveryState != GuidanceRecoveryState.Terminated;
    /// <summary>当前是否因没有明确 Safe 航向而保持恢复等待。</summary>
    public bool IsHoldingForRecovery =>
        recoveryState == GuidanceRecoveryState.HoldingForSafeHeading ||
        recoveryState == GuidanceRecoveryState.RecoveryHold;
    /// <summary>当前是否因 Reset 后导航不可行而短暂等待新事实。</summary>
    public bool IsHoldingForNavigationRecovery => recoveryState == GuidanceRecoveryState.RecoveryHold;
    /// <summary>本轮 Initial Reset 后已经执行的 Recovery Reset 次数。</summary>
    public int NavigationRecoveryResetAttempts => navigationRecoveryResetAttempts;
    /// <summary>当前 Recovery 决策结束后是否需要消费一次 Navigation Recovery 请求。</summary>
    public bool IsNavigationRecoveryDeferred => navigationRecoveryDeferred;
    /// <summary>最近一次 Safe Heading 的 Reset 后导航延续评估。</summary>
    public PostResetContinuationAssessment LatestPostResetContinuation => latestPostResetContinuation;
    /// <summary>当前 Recovery Hold 已执行的选向次数。</summary>
    public int RecoveryHoldAttemptCount => recoveryHoldAttemptCount;
    /// <summary>当前是否因没有可执行语义候选而等待下一份事实重规划。</summary>
    public bool IsHoldingForReplan => holdForReplan;
    /// <summary>当前重规划等待使用的规划指纹。</summary>
    public string ReplanPlanningFingerprint => holdPlanningFingerprint;
    /// <summary>当前连续无候选规划次数。</summary>
    public int ReplanRetryCount => replanRetryCount;
    /// <summary>当前重规划退避间隔，单位：秒。</summary>
    public float CurrentReplanRetryDelaySeconds => currentReplanRetryDelaySeconds;
    /// <summary>当前 RDW 规划模式；未配置时保持既有链。</summary>
    public RdwPlanningMode CurrentRdwPlanningMode => rdwPlanningSettings != null
        ? rdwPlanningSettings.Mode
        : RdwPlanningMode.Legacy;
    /// <summary>当前正式实验 Profile；未配置时为空。</summary>
    public ExperimentProfile CurrentExperimentProfile => experimentProfile;
    /// <summary>当前已选择路线缓存；缓存本身不代表安全许可。</summary>
    public PlanCache RdwPlanCache => rdwPlanCache;
    /// <summary>当前路线选择调用门。</summary>
    public PlanningCallGate RdwPlanningCallGate => rdwPlanningCallGate;
    /// <summary>当前是否允许运行期 SoftReroute 安全策略。</summary>
    public bool SoftRerouteSafetyPolicyEnabled => enableSoftRerouteSafetyPolicy;

    private bool IsResetRecoveryActive => pauseResetController != null && pauseResetController.IsActive;

    private void Update()
    {
        if (recoveryState == GuidanceRecoveryState.Stabilizing &&
            Time.realtimeSinceStartup >= recoveryRevalidationAt)
        {
            ReevaluateAfterReset();
        }

        if (recoveryState == GuidanceRecoveryState.HoldingForSafeHeading)
        {
            UpdateRecoveryHold(riskEvaluator != null ? riskEvaluator.CurrentRiskState : null);
        }

        if (recoveryState == GuidanceRecoveryState.RecoveryHold &&
            IsNavigationRecoveryHoldBudgetExhausted())
        {
            TriggerRecoveryHardStop(
                "Navigation Recovery budget was exhausted without an executable continuation.");
            return;
        }

        if (holdForReplan && !IsResetRecoveryActive &&
            !string.IsNullOrEmpty(GetReplanHoldWakeReason(riskEvaluator?.CurrentRiskState)))
        {
            if (recoveryState == GuidanceRecoveryState.RecoveryHold)
            {
                TransitionRecoveryTo(
                    GuidanceRecoveryState.NavigationRecovery,
                    "recovery_hold_woken");
            }
            RequestNextGuidance(SemanticReplanReason.RuntimeFactsChanged);
        }
    }

    private void OnEnable()
    {
        if (requestClient == null)
        {
            requestClient = agentClient;
        }
        BindRequestCapture();
        BindArrival();
        BindRotationGain();
        BindRuntimeSafety();
        BindRecoveryController();
        BindMotionDetector();
        BindRdwPlanningClient();
        BindSemanticPlanningClient();
        ConfigureTurnCueTracker();
    }

    private void OnDisable()
    {
        UnbindRequestCapture();
        UnbindArrival();
        UnbindRotationGain();
        UnbindRuntimeSafety();
        UnbindRecoveryController();
        UnbindMotionDetector();
        UnbindRdwPlanningClient();
        UnbindSemanticPlanningClient();
    }

    /// <summary>
    /// 注入完整导览链依赖，并重新绑定请求快照与到达事件。
    /// </summary>
    public void Configure(
        AgentClient client,
        GuidanceExecutor executor,
        GuidanceTaskController task,
        NavigationTargetManager targetManager,
        SemanticTargetSelector selector,
        ArrivalDetector detector,
        PhysicalRiskEvaluator evaluator,
        LocalGuidanceValidator validator,
        PauseResetController resetController,
        RotationGainController gainController,
        RejectionPanel rejection,
        MotionDetector turnDetector = null)
    {
        UnbindRequestCapture();
        UnbindArrival();
        UnbindRotationGain();
        UnbindRuntimeSafety();
        UnbindRecoveryController();
        UnbindMotionDetector();

        if (client != null)
        {
            agentClient = client;
            requestClient = client;
        }
        if (executor != null) guidanceExecutor = executor;
        if (task != null) taskController = task;
        if (targetManager != null) navigationTargets = targetManager;
        if (selector != null) semanticTargetSelector = selector;
        if (detector != null) arrivalDetector = detector;
        if (evaluator != null) riskEvaluator = evaluator;
        if (validator != null) localValidator = validator;
        if (resetController != null) pauseResetController = resetController;
        if (gainController != null) rotationGainController = gainController;
        if (turnDetector != null) motionDetector = turnDetector;
        if (rejection != null) rejectionPanel = rejection;

        BindRequestCapture();
        BindArrival();
        BindRotationGain();
        BindRuntimeSafety();
        BindRecoveryController();
        BindMotionDetector();
        BindSemanticPlanningClient();
        ConfigureTurnCueTracker();
    }

    /// <summary>
    /// 替换请求传输实现；正式运行使用 AgentClient，流程测试可使用内存客户端。
    /// </summary>
    public void ConfigureRequestClient(IGuidanceRequestClient client)
    {
        requestClient = client;
    }

    /// <summary>
    /// 注入独立 RDW 规划层；Coordinator 仍是唯一跨模块编排和正式提交入口。
    /// </summary>
    public void ConfigureRdwPlanning(
        RdwPlanningSettings settings,
        RdwPlanningPipeline pipeline,
        RdwFrameCoordinator frameCoordinator,
        IRdwRouteSelectionClient selectionClient,
        ExperimentLogger logger)
    {
        UnbindRdwPlanningClient();
        rdwPlanningSettings = settings ?? new RdwPlanningSettings();
        rdwPlanningPipeline = pipeline;
        rdwPlanningFrameCoordinator = frameCoordinator;
        routeSelectionClient = selectionClient;
        rdwPlanningLogger = logger;
        BindRdwPlanningClient();
    }

    /// <summary>注入 Semantic-only 的静态图和独立局部规划客户端。</summary>
    public void ConfigureSemanticPlanning(
        StaticSceneSemanticGraph graph,
        ISemanticPlanClient planClient)
    {
        UnbindSemanticPlanningClient();
        semanticPlanningGraph = graph;
        semanticPlanClient = planClient;
        executionSafetyEvaluator = new GuidanceExecutionSafetyEvaluator(
            navigationTargets,
            riskEvaluator);
        BindSemanticPlanningClient();
    }

    /// <summary>
    /// 应用正式三条件 Profile；Planner 类型由上层请求链读取，RDW 模式在此统一切换。
    /// </summary>
    public void ApplyExperimentProfile(ExperimentProfile profile)
    {
        experimentProfile = profile?.Clone();
        if (rdwPlanningSettings != null && profile != null)
        {
            rdwPlanningSettings.SetMode(profile.rdwPlanningMode);
        }
    }

    /// <summary>
    /// 根据任务、Target Registry 与最新风险构建并冻结 Candidate Set；请求前不修改 Current Subgoal。
    /// </summary>
    public void RequestNextGuidance()
    {
        RequestNextGuidance(SemanticReplanReason.RuntimeFactsChanged);
    }

    /// <summary>
    /// 根据明确的业务原因请求下一段 Guidance；目标锁或在途规划存在时保持当前语义目标。
    /// </summary>
    public void RequestNextGuidance(SemanticReplanReason reason)
    {
        if (planningRequestInProgress)
        {
            return;
        }

        planningRequestInProgress = true;
        try
        {
            RequestNextGuidanceInternal(reason);
        }
        finally
        {
            planningRequestInProgress = false;
        }
    }

    /// <summary>
    /// 规划请求失败后进入可恢复的重规划等待；不结束任务或实验会话。
    /// </summary>
    public void HoldAfterPlanningFailure(string reason)
    {
        var state = taskController != null ? taskController.RuntimeState : null;
        if (state == null || !state.IsNavigating)
        {
            return;
        }

        var riskState = riskEvaluator != null ? riskEvaluator.EvaluateNow() : null;
        EnterHoldAndReplan(
            riskState,
            BuildPlanningFingerprint(riskState),
            string.IsNullOrWhiteSpace(reason) ? "Planning failed; waiting before retry." : reason,
            false);
    }

    private void RequestNextGuidanceInternal(SemanticReplanReason reason)
    {
        if (requestClient == null)
        {
            requestClient = agentClient;
        }

        if (taskController == null || requestClient == null)
        {
            TriggerRuntimeIntervention(null, "Guidance planning dependencies are unavailable.");
            return;
        }

        if (!taskController.RuntimeState.IsNavigating || requestClient.RequestInFlight)
        {
            return;
        }

        // Semantic-only 与 RDW 共用同一套退避等待；唤醒后先清除 hold，避免成功规划后仍被旧状态拦截。
        if (holdForReplan)
        {
            var holdRiskState = riskEvaluator != null ? riskEvaluator.CurrentRiskState : null;
            var holdWakeReason = GetReplanHoldWakeReason(holdRiskState);
            if (string.IsNullOrEmpty(holdWakeReason))
            {
                return;
            }

            ExitReplanHold("replan_hold_woken:" + holdWakeReason);
        }

        if (experimentProfile != null &&
            experimentProfile.plannerMode == ExperimentPlannerMode.SemanticOnly)
        {
            RequestSemanticOnlyPlan(reason);
            return;
        }

        if (semanticTargetSelector == null || riskEvaluator == null)
        {
            TriggerRuntimeIntervention(null, "Guidance planning dependencies are unavailable.");
            return;
        }

        // 当前语义目标锁定期间只允许物理层连续执行，禁止重复创建新的语义规划。
        if (taskController.RuntimeState.HasLockedSemanticGoal ||
            taskController.RuntimeState.HasActiveSemanticPlanning)
        {
            return;
        }

        var replanInfo = new SemanticReplanInfo(reason, taskController.RuntimeState);
        SemanticReplanning?.Invoke(replanInfo);

        var riskState = riskEvaluator != null ? riskEvaluator.EvaluateNow() : null;
        var conditionPolicy = ExperimentConditionMatrix.GetPolicy(taskController.CurrentCondition);
        var candidateSet = semanticTargetSelector != null
            ? semanticTargetSelector.BuildCandidateSet(taskController.RuntimeState, riskState, conditionPolicy)
            : null;
        var requestId = Guid.NewGuid().ToString("N");
        var decisionId = Guid.NewGuid().ToString("N");
        var candidateSetId = Guid.NewGuid().ToString("N");
        if (riskState == null || candidateSet == null)
        {
            TriggerRuntimeIntervention(null, "Guidance planning could not obtain current risk or candidate facts.");
            return;
        }

        var planningFingerprint = BuildPlanningFingerprint(riskState);
        if (holdForReplan && string.IsNullOrEmpty(GetReplanHoldWakeReason(riskState)))
        {
            return;
        }

        var hasExecutableCandidates = candidateSet.AllowedCandidateIds != null &&
                                       candidateSet.AllowedCandidateIds.Count > 0;
        if (!candidateSet.HasModelCandidates &&
            experimentProfile != null &&
            experimentProfile.plannerMode == ExperimentPlannerMode.RdwGroundedTopK &&
            !HasUnvisitedIntermediateNode(taskController.RuntimeState) &&
            taskController.RuntimeState.CanSelectFinalGoal)
        {
            if (taskController.EnterFinalApproach())
            {
                candidateSet = semanticTargetSelector.BuildCandidateSet(
                    taskController.RuntimeState,
                    riskState,
                    conditionPolicy);
            }
        }

        if (!candidateSet.HasModelCandidates ||
            (postResetNavigationRecoveryPending && !hasExecutableCandidates))
        {
            HandleUnavailableCandidatePlanning(riskState, candidateSet, planningFingerprint);
            return;
        }

        RdwRoutePlanningResult rdwPlanningResult = null;
        RoutePlan cachedPlan = null;
        var cachedTargetId = string.Empty;
        if (CurrentRdwPlanningMode != RdwPlanningMode.Legacy)
        {
            if (!TryCreateRdwPlanningResult(
                    riskState,
                    candidateSet,
                    out rdwPlanningResult,
                    out var rdwPlanningFailure))
            {
                LogRdwPlanning(
                    "planning_unavailable",
                    null,
                    null,
                    null,
                    rdwPlanningFailure);
                if (CurrentRdwPlanningMode == RdwPlanningMode.RdwConstrained)
                {
                    if (postResetNavigationRecoveryPending)
                    {
                        BeginNavigationRecovery(riskState, planningFingerprint);
                        return;
                    }

                    EnterHoldAndReplan(
                        riskState,
                        planningFingerprint,
                        rdwPlanningFailure,
                        false);
                    return;
                }
            }
            else
            {
                LogRdwPlanning(
                    "planning_routes_generated",
                    rdwPlanningResult,
                    null,
                    null,
                    rdwPlanningResult.HasRoutes
                        ? "Top-K routes generated."
                        : "No route passed the hard safety filter.");
            }

            if (CurrentRdwPlanningMode == RdwPlanningMode.RdwConstrained)
            {
                if (rdwPlanningResult == null || !rdwPlanningResult.HasRoutes)
                {
                    if (postResetNavigationRecoveryPending)
                    {
                        BeginNavigationRecovery(riskState, planningFingerprint);
                        return;
                    }

                    EnterHoldAndReplan(
                        riskState,
                        planningFingerprint,
                        "RDW planning produced no hard-safe route.",
                        true);
                    return;
                }

                TryResolveCachedRdwStep(
                    rdwPlanningResult,
                    candidateSet,
                    out cachedPlan,
                    out cachedTargetId);
            }
        }

        if (!taskController.TryReserveCandidateSet(
                candidateSet,
                candidateSetId,
                requestId,
                decisionId,
                riskState,
                out var reservation))
        {
            TriggerRuntimeIntervention(null, "Candidate set reservation could not be created.");
            return;
        }

        var context = taskController.BuildRequestContext(reservation);
        if (context == null)
        {
            taskController.AbandonCandidateSetReservation();
            TriggerRuntimeIntervention(null, "Candidate set request context could not be built.", false);
            return;
        }

        ExitReplanHold("executable_candidate_reserved");
        CompletePostResetNavigationRecovery("executable_candidate_reserved");
        SetRequestPhase(GuidanceRequestPhase.Requested);
        if (CurrentRdwPlanningMode == RdwPlanningMode.RdwConstrained)
        {
            if (routeSelectionClient == null ||
                !routeSelectionClient.TryPrepareGuidanceRequest(context, out var guidanceRequest))
            {
                taskController.AbandonCandidateSetReservation();
                InvalidateRdwPlanning(
                    RoutePlanInvalidationReason.GuidanceCancelled,
                    "RDW constrained planning could not prepare the local guidance identity.");
                HandleRequestFailed("RDW constrained planning could not prepare the local guidance identity.");
                return;
            }

            if (cachedPlan != null && !string.IsNullOrWhiteSpace(cachedTargetId))
            {
                ExecuteCachedRdwStep(
                    cachedPlan,
                    cachedTargetId,
                    guidanceRequest,
                    rdwPlanningResult);
                return;
            }

            if (!BeginRdwRouteSelection(rdwPlanningResult, guidanceRequest))
            {
                taskController.AbandonCandidateSetReservation();
                HandleRequestFailed("RDW route selection request could not start.");
            }
            return;
        }

        if (CurrentRdwPlanningMode == RdwPlanningMode.Shadow && rdwPlanningResult != null && rdwPlanningResult.HasRoutes)
        {
            BeginRdwRouteSelection(rdwPlanningResult, null);
        }
        requestClient.RequestGuidance(context);
    }

    private void RequestSemanticOnlyPlan(SemanticReplanReason reason)
    {
        var state = taskController != null ? taskController.RuntimeState : null;
        if (state == null || !state.IsNavigating || semanticPlanClient == null ||
            semanticPlanClient.SemanticPlanRequestInFlight || semanticPlanningGraph == null)
        {
            TriggerRuntimeIntervention(null, "Semantic-only planning dependencies are unavailable.");
            return;
        }
        if (!string.IsNullOrWhiteSpace(state.activePlanId) || state.HasLockedSemanticGoal)
        {
            return;
        }
        if (state.MustSelectFinalGoal && state.taskStage == GuidanceTaskStage.Exploring &&
            !taskController.EnterFinalApproach())
        {
            TriggerRuntimeIntervention(null, "Final goal could not be locked after intermediate visit limit.");
            return;
        }

        var taskContract = NaturalLanguageTaskContract.From(semanticPlanningGraph, state);
        var taskState = NaturalLanguageTaskState.From(state);
        if (taskContract == null || taskState == null)
        {
            TriggerRuntimeIntervention(null, "Natural-language task contract is incomplete.");
            return;
        }

        var sessionId = rdwPlanningLogger != null && rdwPlanningLogger.Session != null
            ? rdwPlanningLogger.Session.sessionId
            : string.Empty;
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            sessionId = "pre-session-" + Guid.NewGuid().ToString("N");
        }

        var requestId = Guid.NewGuid().ToString("N");
        var generation = Math.Max(1, state.planGeneration);
        var request = semanticLocalGraphBuilder.Build(
            semanticPlanningGraph,
            taskContract,
            taskState,
            sessionId,
            requestId,
            generation,
            state.stateVersion,
            3);
        if (!HasUnvisitedSemanticCandidate(request))
        {
            TriggerRuntimeIntervention(null, "No unvisited semantic candidate is available before the final goal.");
            return;
        }

        pendingSemanticPlanRequest = request;
        pendingSemanticPlanId = requestId;
        rdwPlanningLogger?.LogSemanticPlan(
            "semantic_plan_requested",
            request,
            null,
            state,
            "semantic-only local plan requested");
        SemanticReplanning?.Invoke(new SemanticReplanInfo(reason, state));
        SetRequestPhase(GuidanceRequestPhase.Requested);
        semanticPlanClient.RequestSemanticPlan(request);
    }

    private static bool HasUnvisitedSemanticCandidate(SemanticPlanRequest request)
    {
        if (request?.task?.final_goal == null || request.task.intermediate_policy == null ||
            request.task_state == null) return false;

        var state = request.task_state;
        var policy = request.task.intermediate_policy;
        var finalTargetId = request.task.final_goal.exhibit_id;
        var visited = new HashSet<string>(state.visited_exhibit_ids ?? Array.Empty<string>(), StringComparer.Ordinal);
        foreach (var node in request?.local_semantic_nodes ?? Array.Empty<SemanticPlanningNode>())
        {
            if (node == null || visited.Contains(node.id)) continue;
            if (string.Equals(node.id, finalTargetId, StringComparison.Ordinal))
            {
                if (state.intermediate_visit_count >= policy.minimum_visits) return true;
                continue;
            }
            if (state.intermediate_visit_count < policy.maximum_visits &&
                (!policy.require_semantic_connection ||
                 SemanticPlanVerifier.HasSemanticPath(
                     request.local_semantic_edges,
                     node.id,
                     finalTargetId))) return true;
        }
        return false;
    }

    private bool HasUnvisitedIntermediateNode(GuidanceRuntimeState state)
    {
        if (state == null || semanticPlanningGraph == null ||
            string.IsNullOrWhiteSpace(state.currentExhibitId)) return false;

        var visited = new HashSet<string>(state.visitedExhibitIds ?? Array.Empty<string>(), StringComparer.Ordinal);
        var included = new HashSet<string>(StringComparer.Ordinal) { state.currentExhibitId };
        var frontier = new List<string> { state.currentExhibitId };
        for (var hop = 0; hop < 3 && frontier.Count > 0; hop++)
        {
            var next = new List<string>();
            foreach (var source in frontier)
            {
                foreach (var neighbor in semanticPlanningGraph.GetSpatialNeighbors(source))
                {
                    if (!included.Add(neighbor)) continue;
                    if (!visited.Contains(neighbor) &&
                        !string.Equals(neighbor, state.finalTargetId, StringComparison.Ordinal) &&
                        SemanticPlanVerifier.HasSemanticPath(
                            semanticPlanningGraph.SemanticEdges,
                            neighbor,
                            state.finalTargetId)) return true;
                    next.Add(neighbor);
                }
            }
            frontier = next;
        }
        return false;
    }

    private void HandleSemanticPlanReceived(SemanticPlanResponse response)
    {
        var request = pendingSemanticPlanRequest;
        var state = taskController != null ? taskController.RuntimeState : null;
        if (request == null || state == null || response == null ||
            !string.Equals(response.request_id, pendingSemanticPlanId, StringComparison.Ordinal) ||
            request.task_state_version != state.stateVersion ||
            state.planGeneration != request.generation)
        {
            HandleStaleSemanticPlanResponse(request, response, state);
            return;
        }

        if (!response.TryValidate(request, out var validationFailure))
        {
            HandleSemanticPlanFailed("Semantic plan response rejected: " + validationFailure);
            return;
        }

        pendingSemanticPlanRequest = null;
        pendingSemanticPlanId = string.Empty;
        rdwPlanningLogger?.LogSemanticPlan(
            "semantic_plan_received",
            request,
            response,
            state,
            "semantic-only local plan verified");
        SetRequestPhase(GuidanceRequestPhase.Received);
        if (response.ordered_node_ids == null || response.ordered_node_ids.Length == 0)
        {
            HandleSemanticPlanFailed("Semantic plan response contains no executable node.");
            return;
        }

        var targetId = response.ordered_node_ids[0];
        var targetRole = string.Equals(targetId, state.finalTargetId, StringComparison.Ordinal)
            ? NavigationTargetRole.FinalTarget
            : NavigationTargetRole.RouteSubgoal;
        var riskState = riskEvaluator != null ? riskEvaluator.EvaluateNow() : null;
        executionSafetyEvaluator = new GuidanceExecutionSafetyEvaluator(navigationTargets, riskEvaluator);
        if (!executionSafetyEvaluator.TryEvaluate(
                targetId,
                targetRole,
                riskState,
                out _,
                out var safetyReason))
        {
            state.InvalidateSemanticPlan();
            SetRequestPhase(GuidanceRequestPhase.Failed);
            TriggerSemanticPlanRejection(
                response,
                "Semantic plan first target failed local Physical Safety: " + safetyReason);
            if (riskState != null)
            {
                TriggerPauseReset(
                    null,
                    riskState,
                    "Semantic plan first target is not physically safe; starting heading recovery.");
            }
            return;
        }

        state.SetActiveSemanticPlan(response.request_id, response.ordered_node_ids);
        var target = new GuidanceTarget(targetId, targetRole);
        if (!state.TryReserveGuidanceTarget(target, response.request_id) ||
            !taskController.CommitReservation(state.ActiveReservation))
        {
            state.InvalidateSemanticPlan();
            TriggerSemanticPlanRejection(response, "Semantic target reservation could not be committed.");
            return;
        }

        if (!navigationTargets.TryGetTargetBearing(targetId, out var bearing))
        {
            TriggerSemanticPlanRejection(response, "Semantic target bearing is unavailable.");
            return;
        }

        var actionPlanner = new GuidanceActionPlanner();
        var guidanceResponse = new GuidanceResponse
        {
            task_definition_version = "semantic-local-planning-v2",
            guidance_schema_version = "semantic-local-planning-v2",
            candidate_policy_version = "semantic-local-planning-v2",
            risk_policy_version = string.Empty,
            session_id = request.session_id,
            calibration_id = riskState != null ? riskState.calibration_id : string.Empty,
            request_id = request.request_id,
            decision_id = request.request_id,
            candidate_set_id = string.Empty,
            state_version = request.task_state_version,
            risk_snapshot_id = riskState != null ? riskState.risk_snapshot_id : string.Empty,
            risk_revision = riskState != null ? riskState.risk_revision : 0,
            target_role = GuidanceTypeUtility.TargetRoleToProtocol(targetRole),
            selected_target = targetId,
            route_decision = "semantic_local",
            narration = response.narration,
            gaze_cue = BearingLabelProtocolMapper.ToProtocol(bearing.DisplayLabel),
            gaze_target_id = targetId,
            movement_instruction = GuidanceActionProtocolMapper.ToMovementInstruction(
                actionPlanner.Plan(bearing)),
            physical_intent = "unity_execution_safety",
            verifier = new VerifierResult { approved = true, reason = "semantic_plan_verified" }
        };

        SetRequestPhase(GuidanceRequestPhase.Verified);
        guidanceExecutor?.Apply(guidanceResponse);
        arrivalDetector?.WatchReservation(state.ActiveReservation);
        activeResponse = guidanceResponse;
        ResetRuntimeSafetySampling();
        SetRequestPhase(GuidanceRequestPhase.Executing);
        GuidanceTargetSelected?.Invoke(target);
        GuidanceExecuted?.Invoke(guidanceResponse);
    }

    private void HandleSemanticPlanFailed(string reason)
    {
        rdwPlanningLogger?.LogSemanticPlan(
            "semantic_plan_failed",
            pendingSemanticPlanRequest,
            null,
            taskController != null ? taskController.RuntimeState : null,
            reason);
        pendingSemanticPlanRequest = null;
        pendingSemanticPlanId = string.Empty;
        SetRequestPhase(GuidanceRequestPhase.Failed);
        var resolvedReason = string.IsNullOrWhiteSpace(reason)
            ? "Semantic-only planning request failed."
            : reason;
        PlanningFailed?.Invoke(resolvedReason);
    }

    private void HandleStaleSemanticPlanResponse(
        SemanticPlanRequest request,
        SemanticPlanResponse response,
        GuidanceRuntimeState state)
    {
        var requestId = request != null ? request.request_id : string.Empty;
        var responseId = response != null ? response.request_id : string.Empty;
        pendingSemanticPlanRequest = null;
        pendingSemanticPlanId = string.Empty;
        if (state == null || !state.IsNavigating)
        {
            return;
        }

        SetRequestPhase(GuidanceRequestPhase.Expired);
        rdwPlanningLogger?.LogSemanticPlan(
            "semantic_plan_stale",
            request,
            response,
            state,
            $"stale semantic response discarded; request_id={requestId}; response_id={responseId}");
        RequestNextGuidance(SemanticReplanReason.RuntimeFactsChanged);
    }

    private void TriggerSemanticPlanRejection(SemanticPlanResponse response, string reason)
    {
        guidanceExecutor?.Clear();
        arrivalDetector?.WatchReservation(null);
        AbandonReservations();
        RuntimeIntervention?.Invoke(new RuntimeInterventionInfo(
            null,
            reason,
            RuntimeGuidanceSafetyDecision.HardStop,
            false,
            0,
            false));
    }

    /// <summary>
    /// 对后端响应执行本地时效与安全校验，通过后更新导览表现、到达目标和连续导航意图。
    /// </summary>
    /// <param name="response">后端返回的 guidance；null 会被本地校验拒绝。</param>
    public void HandleGuidanceResponse(GuidanceResponse response)
    {
        var runtimeState = taskController != null ? taskController.RuntimeState : null;
        var riskState = riskEvaluator != null ? riskEvaluator.CurrentRiskState : null;

        if (activeSnapshot == null)
        {
            return;
        }

        if (response == null)
        {
            TriggerRuntimeIntervention(null, "Guidance response is null.");
            return;
        }

        if (!activeSnapshot.MatchesResponse(response))
        {
            // 旧响应不得改变当前请求阶段、取消新请求或触发重规划。
            return;
        }

        CompareShadowRouteIfAvailable(response.selected_target);

        if (runtimeState == null || !runtimeState.IsNavigating)
        {
            guidanceExecutor?.Clear();
            SetRequestPhase(GuidanceRequestPhase.Cancelled);
            return;
        }

        SetRequestPhase(GuidanceRequestPhase.Received);

        if (response.verifier == null)
        {
            TriggerRuntimeIntervention(response, "Guidance verifier result is missing.");
            return;
        }

        if (!response.verifier.approved)
        {
            TriggerRuntimeIntervention(response, response.verifier.reason);
            return;
        }

        var candidateReservation = runtimeState.ActiveCandidateSetReservation;
        var conditionPolicy = ExperimentConditionMatrix.GetPolicy(taskController.CurrentCondition);
        if (candidateReservation == null ||
            candidateReservation.Phase != CandidateSetReservationPhase.Reserved ||
            !candidateReservation.ContainsModelCandidate(response.selected_target) ||
            !candidateReservation.TryGetCandidate(response.selected_target, out var frozenCandidate) ||
            !string.Equals(
                response.target_role,
                GuidanceTypeUtility.TargetRoleToProtocol(frozenCandidate.TargetRole),
                StringComparison.Ordinal))
        {
            ExpireAndReplan(response, "Guidance response does not match the active Candidate-Set Reservation.");
            return;
        }

        if (!TryValidateSelectedTargetExactHeading(
                response.selected_target,
                riskState,
                out var exactHeadingReason))
        {
            ExpireAndReplan(response, exactHeadingReason);
            return;
        }

        // 只重验模型实际选择的目标；其他候选变化不应使仍安全的响应过期。
        if (!semanticTargetSelector.IsSelectedCandidateCurrent(
                candidateReservation,
                response.selected_target,
                runtimeState,
                riskState,
                conditionPolicy,
                riskEvaluator,
                out var candidateReason))
        {
            ExpireAndReplan(response, candidateReason);
            return;
        }

        // 后端只选择语义目标；最终身体动作始终由最新连续偏航和本地物理安全层生成。
        if (!semanticTargetSelector.TryAssessCandidate(
                response.selected_target,
                frozenCandidate.TargetRole,
                riskState,
                out var selectedAssessment) ||
            !selectedAssessment.IsExecutable)
        {
            ExpireAndReplan(response, "Selected candidate cannot produce an executable local action.");
            return;
        }
        response.movement_instruction =
            GuidanceActionProtocolMapper.ToMovementInstruction(selectedAssessment.SuggestedAction);

        if (localValidator != null &&
            !localValidator.Validate(response, activeSnapshot, runtimeState, riskState, out var reason))
        {
            if (reason.IndexOf("changed", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                ExpireAndReplan(response, reason);
                return;
            }

            TriggerRuntimeIntervention(response, reason);
            return;
        }

        if (conditionPolicy.UseSafeCandidateFilter == ConditionCapabilityMode.Enabled &&
            !Contains(candidateReservation.AllowedCandidateIds, response.selected_target))
        {
            TriggerRuntimeIntervention(response, "Guidance selected_target is outside allowed_candidate_ids.");
            return;
        }

        // 提交前统一复验目标方向、动作方向和最新物理净空，避免先执行再由下一帧安全层终止。
        if (!TryValidateExecutablePhysicalAction(
                response,
                riskState,
                out _,
                out _,
                out var physicalActionReason))
        {
            ExpireAndReplan(response, physicalActionReason);
            return;
        }

        if (!taskController.TryValidateCandidateSelection(
                candidateReservation,
                response.selected_target,
                out var selectedCandidate) ||
            !taskController.CommitCurrentSubgoal(
                candidateReservation,
                selectedCandidate,
                out var targetReservation))
        {
            taskController.AbandonCandidateSetReservation();
            TriggerRuntimeIntervention(response, "Candidate selection could not be committed atomically.");
            return;
        }

        SetRequestPhase(GuidanceRequestPhase.Verified);
        guidanceExecutor?.Apply(response);
        lastDeterministicRejectionFingerprint = string.Empty;
        if (selectedCandidate.TargetRole != NavigationTargetRole.TurnCueAnchor)
        {
            arrivalDetector?.WatchReservation(targetReservation);
        }
        GuidanceTargetSelected?.Invoke(new GuidanceTarget(
            selectedCandidate.TargetId,
            selectedCandidate.TargetRole));

        activeResponse = response;
        ResetRuntimeSafetySampling();
        SetRequestPhase(GuidanceRequestPhase.Executing);
        GuidanceExecuted?.Invoke(response);
        if (CurrentGuidanceTarget != null &&
            CurrentGuidanceTarget.TargetRole == NavigationTargetRole.TurnCueAnchor)
        {
            BeginTurnCueTracking(response);
        }
        else
        {
            turnCueCompletionTracker.Cancel();
        }
    }

    private void HandleRequestBuilt(GuidanceRequest request)
    {
        activeSnapshot = GuidanceRequestSnapshot.Capture(
            request,
            riskEvaluator != null ? riskEvaluator.CurrentRiskState : null,
            taskController != null ? taskController.RuntimeState : null);
        SetRequestPhase(GuidanceRequestPhase.Requested);
    }

    private bool TryCreateRdwPlanningResult(
        PhysicalRiskState riskState,
        NavigationCandidateSet candidateSet,
        out RdwRoutePlanningResult result,
        out string failureReason)
    {
        result = null;
        failureReason = string.Empty;
        var frame = rdwPlanningFrameCoordinator != null
            ? rdwPlanningFrameCoordinator.LastFrame
            : null;
        if (rdwPlanningPipeline == null || frame == null || !frame.Pose.IsValid)
        {
            failureReason = "Fresh valid RDW pose frame is unavailable.";
            return false;
        }
        if (riskState == null || taskController == null || navigationTargets == null)
        {
            failureReason = "RDW planning dependencies are incomplete.";
            return false;
        }

        try
        {
            result = rdwPlanningPipeline.Plan(
                frame.Pose,
                riskState,
                taskController.RuntimeState,
                candidateSet,
                navigationTargets);
        }
        catch (Exception exception)
        {
            failureReason = $"RDW planning failed: {exception.Message}";
            return false;
        }

        if (result?.Snapshot == null || !result.Snapshot.IsValid)
        {
            failureReason = "Fresh RDW PlanningSnapshot is invalid.";
            result = null;
            return false;
        }
        return true;
    }

    private bool TryResolveCachedRdwStep(
        RdwRoutePlanningResult result,
        NavigationCandidateSet candidateSet,
        out RoutePlan plan,
        out string targetId)
    {
        plan = null;
        targetId = string.Empty;
        var state = taskController != null ? taskController.RuntimeState : null;
        var previousPlan = rdwPlanCache.ActivePlan;
        if (state == null ||
            !rdwPlanCache.TryGet(result.Snapshot, state.stateVersion, out plan))
        {
            if (previousPlan != null)
            {
                LogRdwPlanning(
                    "planning_plan_invalidated",
                    result,
                    null,
                    previousPlan,
                    rdwPlanCache.LastInvalidationReason.ToString());
            }
            return false;
        }

        if (!string.IsNullOrWhiteSpace(plan.ActiveStepId))
        {
            InvalidateRdwPlanning(
                RoutePlanInvalidationReason.GuidanceCancelled,
                "Cached route still owns an uncompleted active step.");
            plan = null;
            return false;
        }
        if (!plan.HasRemainingSteps)
        {
            InvalidateRdwPlanning(
                RoutePlanInvalidationReason.RouteExhausted,
                "Cached route has no remaining step.");
            plan = null;
            return false;
        }

        targetId = plan.RemainingSteps[0];
        if (!Contains(candidateSet.ModelCandidateIds, targetId) ||
            !ContainsHardSafeFirstTarget(result, targetId))
        {
            InvalidateRdwPlanning(
                RoutePlanInvalidationReason.FirstStepRejected,
                "Cached next step is not a current hard-safe model candidate.");
            plan = null;
            targetId = string.Empty;
            return false;
        }
        return true;
    }

    private static bool ContainsHardSafeFirstTarget(
        RdwRoutePlanningResult result,
        string targetId)
    {
        if (result == null) return false;
        foreach (var route in result.Routes)
        {
            if (route != null &&
                string.Equals(route.FirstTargetId, targetId, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    private void ConfirmCompletedRdwStep(string arrivedTargetId, int nextTaskStateVersion)
    {
        if (rdwPlanCache.ActivePlan == null) return;
        if (!rdwPlanCache.ConfirmStepCompleted(arrivedTargetId, nextTaskStateVersion))
        {
            InvalidateRdwPlanning(
                RoutePlanInvalidationReason.TaskStateChanged,
                "Arrived target does not match the active cached route step.");
            return;
        }

        LogRdwPlanning(
            "planning_step_completed",
            null,
            null,
            rdwPlanCache.ActivePlan,
            "The active route step reached its reserved target.");
    }

    private bool BeginRdwRouteSelection(
        RdwRoutePlanningResult result,
        GuidanceRequest guidanceRequest)
    {
        if (result?.Snapshot == null || !result.HasRoutes || routeSelectionClient == null ||
            routeSelectionClient.RouteSelectionRequestInFlight ||
            rdwPlanningCallGate.RequestInFlight)
        {
            return false;
        }

        var identity = rdwPlanningCallGate.TryBegin(result.Snapshot);
        if (identity == null)
        {
            return false;
        }
        var sessionId = guidanceRequest != null
            ? guidanceRequest.session_id
            : rdwPlanningLogger != null && rdwPlanningLogger.Session != null
                ? rdwPlanningLogger.Session.sessionId
                : string.Empty;
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            rdwPlanningCallGate.Invalidate();
            return false;
        }

        var runtimeState = taskController != null ? taskController.RuntimeState : null;
        var request = RdwRouteSelectionRequest.From(
            sessionId,
            identity,
            result,
            NaturalLanguageTaskContract.From(semanticPlanningGraph, runtimeState),
            NaturalLanguageTaskState.From(runtimeState));
        if (request == null)
        {
            rdwPlanningCallGate.Invalidate();
            return false;
        }

        pendingRdwPlanningResult = result;
        pendingRdwRouteSelectionRequest = request;
        pendingRdwGuidanceRequest = guidanceRequest;
        pendingRdwCallIdentity = identity;
        LogRdwPlanning(
            "planning_route_selection_requested",
            result,
            null,
            null,
            "Top-K route_id selection requested.");
        routeSelectionClient.RequestRouteSelection(request);
        return true;
    }

    private void HandleRdwRouteSelectionReceived(RdwRouteSelectionResponse response)
    {
        var result = pendingRdwPlanningResult;
        var guidanceRequest = pendingRdwGuidanceRequest;
        var identity = pendingRdwCallIdentity;
        if (response == null || result == null || identity == null ||
            !rdwPlanningCallGate.Complete(
                response.plan_id,
                response.request_id,
                response.snapshot_id,
                response.generation))
        {
            return;
        }

        RouteCandidate selectedRoute = null;
        foreach (var route in result.Routes)
        {
            if (route != null &&
                string.Equals(route.RouteId, response.route_id, StringComparison.Ordinal))
            {
                selectedRoute = route;
                break;
            }
        }
        if (selectedRoute == null)
        {
            HandleRdwRouteSelectionFailed("Selected route_id is not in the frozen planning result.");
            return;
        }

        LogRdwPlanning(
            "planning_route_selected",
            result,
            response,
            null,
            "Backend selected an existing route_id.");
        if (CurrentRdwPlanningMode == RdwPlanningMode.Shadow)
        {
            ClearPendingRdwPlanning();
            lastShadowPlanningResult = result;
            lastShadowSelectionResponse = response;
            CompareShadowRouteIfAvailable(activeResponse != null
                ? activeResponse.selected_target
                : string.Empty);
            return;
        }
        if (CurrentRdwPlanningMode != RdwPlanningMode.RdwConstrained || guidanceRequest == null)
        {
            ClearPendingRdwPlanning();
            return;
        }

        var adapted = CreateRdwGuidanceResponse(
            guidanceRequest,
            selectedRoute.FirstTargetId,
            response.narration,
            selectedRoute.RouteId,
            response.verifier);
        if (adapted == null)
        {
            InvalidateRdwPlanning(
                RoutePlanInvalidationReason.GuidanceCancelled,
                "Selected route first step cannot be adapted to the active Candidate Reservation.");
            HandleRequestFailed("RDW route first step adaptation failed.");
            return;
        }

        HandleGuidanceResponse(adapted);
        if (requestPhase == GuidanceRequestPhase.Executing &&
            ReferenceEquals(activeResponse, adapted))
        {
            var plan = RoutePlan.FromSelection(
                identity,
                selectedRoute,
                result.Snapshot,
                taskController.RuntimeState.stateVersion,
                response.narration);
            rdwPlanCache.Store(plan);
            LogRdwPlanning(
                "planning_first_step_revalidated",
                result,
                response,
                plan,
                "Unity Exact Safety accepted the selected route first step.");
        }
        ClearPendingRdwPlanning();
    }

    private void ExecuteCachedRdwStep(
        RoutePlan plan,
        string targetId,
        GuidanceRequest guidanceRequest,
        RdwRoutePlanningResult freshResult)
    {
        var adapted = CreateRdwGuidanceResponse(
            guidanceRequest,
            targetId,
            plan.Narration,
            plan.SelectedRouteId,
            new VerifierResult
            {
                approved = true,
                reason = "Cached route membership retained; Unity must revalidate Exact Safety."
            });
        if (adapted == null)
        {
            InvalidateRdwPlanning(
                RoutePlanInvalidationReason.FirstStepRejected,
                "Cached route step cannot be adapted to the current Candidate Reservation.");
            HandleRequestFailed("Cached RDW route step adaptation failed.");
            return;
        }

        HandleGuidanceResponse(adapted);
        if (requestPhase == GuidanceRequestPhase.Executing &&
            ReferenceEquals(activeResponse, adapted))
        {
            if (!rdwPlanCache.CommitNextStep(
                    targetId,
                    taskController.RuntimeState.stateVersion))
            {
                InvalidateRdwPlanning(
                    RoutePlanInvalidationReason.GuidanceCancelled,
                    "Cached route step executed but the cache transition failed.");
                return;
            }
            LogRdwPlanning(
                "planning_cached_step_revalidated",
                freshResult,
                null,
                rdwPlanCache.ActivePlan,
                "Unity Exact Safety accepted the cached route next step.");
        }
    }

    private static GuidanceResponse CreateRdwGuidanceResponse(
        GuidanceRequest request,
        string targetId,
        string narration,
        string routeId,
        VerifierResult verifier)
    {
        if (request == null || request.semantic_candidates == null) return null;
        GuidanceSemanticCandidate candidate = null;
        foreach (var value in request.semantic_candidates)
        {
            if (value != null && string.Equals(value.id, targetId, StringComparison.Ordinal))
            {
                candidate = value;
                break;
            }
        }
        if (candidate == null) return null;

        return new GuidanceResponse
        {
            task_definition_version = request.task_definition_version,
            guidance_schema_version = request.guidance_schema_version,
            candidate_policy_version = request.candidate_policy_version,
            risk_policy_version = request.risk_policy_version,
            session_id = request.session_id,
            calibration_id = request.calibration_id,
            request_id = request.request_id,
            decision_id = request.decision_id,
            candidate_set_id = request.candidate_set_id,
            state_version = request.state_version,
            risk_snapshot_id = request.risk_snapshot_id,
            risk_revision = request.risk_snapshot != null ? request.risk_snapshot.risk_revision : 0,
            target_role = candidate.target_role,
            selected_target = candidate.id,
            route_decision = $"rdw_constrained:{routeId}",
            narration = string.IsNullOrWhiteSpace(narration)
                ? "我们继续前往下一站。"
                : narration,
            gaze_cue = GazeCueForDisplayDirection(candidate.relative_direction),
            gaze_target_id = candidate.id,
            movement_instruction = candidate.suggested_movement_instruction,
            physical_intent = "unity_exact_safety_revalidation",
            verifier = verifier ?? new VerifierResult
            {
                approved = true,
                reason = "route_id membership verified; physical safety remains Unity-owned."
            }
        };
    }

    private static string GazeCueForDisplayDirection(string relativeDirection)
    {
        switch (relativeDirection)
        {
            case "left": return "look_left";
            case "right": return "look_right";
            default: return "look_forward";
        }
    }

    private void HandleRdwRouteSelectionFailed(string reason)
    {
        var result = pendingRdwPlanningResult;
        var activeMode = CurrentRdwPlanningMode;
        LogRdwPlanning(
            "planning_route_selection_failed",
            result,
            null,
            rdwPlanCache.ActivePlan,
            reason);
        rdwPlanningCallGate.Invalidate();
        ClearPendingRdwPlanning();
        if (activeMode == RdwPlanningMode.RdwConstrained)
        {
            rdwPlanCache.Invalidate(RoutePlanInvalidationReason.GuidanceCancelled);
            HandleRequestFailed(reason);
        }
    }

    private void CompareShadowRouteIfAvailable(string formalTargetId)
    {
        if (lastShadowPlanningResult == null || lastShadowSelectionResponse == null ||
            string.IsNullOrWhiteSpace(formalTargetId))
        {
            return;
        }

        var shadowTargetId = string.Empty;
        foreach (var route in lastShadowPlanningResult.Routes)
        {
            if (route != null && string.Equals(
                    route.RouteId,
                    lastShadowSelectionResponse.route_id,
                    StringComparison.Ordinal))
            {
                shadowTargetId = route.FirstTargetId;
                break;
            }
        }
        LogRdwPlanning(
            "planning_shadow_compared",
            lastShadowPlanningResult,
            lastShadowSelectionResponse,
            null,
            $"shadow_first_target={shadowTargetId}; formal_selected_target={formalTargetId}",
            formalTargetId);
        lastShadowPlanningResult = null;
        lastShadowSelectionResponse = null;
    }

    private void ClearPendingRdwPlanning()
    {
        pendingRdwPlanningResult = null;
        pendingRdwRouteSelectionRequest = null;
        pendingRdwGuidanceRequest = null;
        pendingRdwCallIdentity = null;
    }

    private void BindRdwPlanningClient()
    {
        if (routeSelectionClient == null) return;
        routeSelectionClient.RouteSelectionReceived -= HandleRdwRouteSelectionReceived;
        routeSelectionClient.RouteSelectionReceived += HandleRdwRouteSelectionReceived;
        routeSelectionClient.RouteSelectionRequestFailed -= HandleRdwRouteSelectionFailed;
        routeSelectionClient.RouteSelectionRequestFailed += HandleRdwRouteSelectionFailed;
    }

    private void UnbindRdwPlanningClient()
    {
        if (routeSelectionClient == null) return;
        routeSelectionClient.RouteSelectionReceived -= HandleRdwRouteSelectionReceived;
        routeSelectionClient.RouteSelectionRequestFailed -= HandleRdwRouteSelectionFailed;
    }

    private void BindSemanticPlanningClient()
    {
        if (semanticPlanClient == null) return;
        semanticPlanClient.SemanticPlanReceived -= HandleSemanticPlanReceived;
        semanticPlanClient.SemanticPlanReceived += HandleSemanticPlanReceived;
        semanticPlanClient.SemanticPlanRequestFailed -= HandleSemanticPlanFailed;
        semanticPlanClient.SemanticPlanRequestFailed += HandleSemanticPlanFailed;
    }

    private void UnbindSemanticPlanningClient()
    {
        if (semanticPlanClient == null) return;
        semanticPlanClient.SemanticPlanReceived -= HandleSemanticPlanReceived;
        semanticPlanClient.SemanticPlanRequestFailed -= HandleSemanticPlanFailed;
    }

    private void InvalidateRdwPlanning(
        RoutePlanInvalidationReason reason,
        string message,
        bool preserveSemanticPlanning = false)
    {
        var result = pendingRdwPlanningResult;
        var plan = rdwPlanCache.ActivePlan;
        routeSelectionClient?.CancelPendingRouteSelection();
        if (!preserveSemanticPlanning)
        {
            semanticPlanClient?.CancelPendingSemanticPlan();
            pendingSemanticPlanRequest = null;
            pendingSemanticPlanId = string.Empty;
        }
        rdwPlanningCallGate.Invalidate();
        rdwPlanCache.Invalidate(reason);
        ClearPendingRdwPlanning();
        lastShadowPlanningResult = null;
        lastShadowSelectionResponse = null;
        LogRdwPlanning(
            "planning_plan_invalidated",
            result,
            null,
            plan,
            string.IsNullOrWhiteSpace(message) ? reason.ToString() : message);
    }

    private void LogRdwPlanning(
        string eventType,
        RdwRoutePlanningResult result,
        RdwRouteSelectionResponse response,
        RoutePlan plan,
        string message,
        string formalSelectedTargetId = "")
    {
        rdwPlanningLogger?.LogRdwPlanning(
            eventType,
            CurrentRdwPlanningMode,
            result,
            pendingRdwCallIdentity,
            response,
            plan,
            rdwPlanCache.LastInvalidationReason,
            taskController != null ? taskController.RuntimeState : null,
            riskEvaluator != null ? riskEvaluator.CurrentRiskState : null,
            message,
            formalSelectedTargetId);
    }

    /// <summary>
    /// 处理当前路线目标到达：取消旧执行、推进任务，并在未完成时自动重新规划下一段 Guidance。
    /// </summary>
    public void HandleTargetArrived(TargetReservation reservation)
    {
        var state = taskController != null ? taskController.RuntimeState : null;
        var guidanceTarget = state != null ? state.CurrentGuidanceTarget : null;
        if (state == null || !state.IsNavigating || guidanceTarget == null ||
            !state.MatchesActiveReservation(reservation) ||
            guidanceTarget.TargetRole == NavigationTargetRole.TurnCueAnchor ||
            !string.Equals(reservation.TargetId, guidanceTarget.TargetId, StringComparison.Ordinal))
        {
            return;
        }

        FinishActiveGuidanceForProgress();

        if (guidanceTarget.TargetRole == NavigationTargetRole.NavigableDetour)
        {
            if (!taskController.HandleDetourArrived(reservation.TargetId))
            {
                return;
            }

            ConfirmCompletedRdwStep(reservation.TargetId, state.stateVersion);

            TargetReached?.Invoke(guidanceTarget);
            DetourCompleted?.Invoke(guidanceTarget);
            Replanning?.Invoke(state);
            RequestNextGuidance(SemanticReplanReason.TargetArrived);
            return;
        }

        var dynamicTaskPlanner = experimentProfile != null &&
                                 experimentProfile.plannerMode == ExperimentPlannerMode.RdwGroundedTopK;
        if (dynamicTaskPlanner)
        {
            if (!taskController.HandlePlannedTargetArrived(reservation.TargetId))
            {
                return;
            }

            ConfirmCompletedRdwStep(reservation.TargetId, state.stateVersion);
            TargetReached?.Invoke(guidanceTarget);
            if (state.IsCompleted)
            {
                InvalidateRdwPlanning(
                    RoutePlanInvalidationReason.RouteExhausted,
                    "Final task goal reached after the active RDW route step arrived.");
                arrivalDetector?.WatchReservation(null);
                TaskCompleted?.Invoke(state);
                return;
            }

            Replanning?.Invoke(state);
            RequestNextGuidance(SemanticReplanReason.TargetArrived);
            return;
        }

        if (!string.Equals(reservation.TargetId, state.nextTargetId, StringComparison.Ordinal))
        {
            return;
        }

        var previousVersion = state.stateVersion;
        taskController.HandleArrived(reservation.TargetId);
        if (state.stateVersion == previousVersion)
        {
            return;
        }

        ConfirmCompletedRdwStep(reservation.TargetId, state.stateVersion);

        TargetReached?.Invoke(guidanceTarget);
        if (state.IsCompleted)
        {
            InvalidateRdwPlanning(
                RoutePlanInvalidationReason.RouteExhausted,
                "Task completed after the active route step arrived.");
            arrivalDetector?.WatchReservation(null);
            TaskCompleted?.Invoke(state);
            return;
        }

        Replanning?.Invoke(state);
        RequestNextGuidance(SemanticReplanReason.TargetArrived);
    }

    /// <summary>测试与研究工具入口：仅把当前活动预留对应的目标视为有效到达。</summary>
    public void HandleTargetArrived(string targetId)
    {
        var reservation = taskController != null ? taskController.RuntimeState.ActiveReservation : null;
        if (reservation != null && string.Equals(targetId, reservation.TargetId, StringComparison.Ordinal))
        {
            HandleTargetArrived(reservation);
        }
    }

    /// <summary>
    /// 接收用户已完成 TurnCueAnchor 转向的反馈；不触发普通 Arrival Task Progress。
    /// </summary>
    public void HandleTurnCueCompleted()
    {
        var state = taskController != null ? taskController.RuntimeState : null;
        var target = state != null ? state.CurrentGuidanceTarget : null;
        if (state == null || !state.IsNavigating || target == null ||
            target.TargetRole != NavigationTargetRole.TurnCueAnchor)
        {
            return;
        }

        FinishActiveGuidanceForProgress();
        if (!taskController.HandleTurnCueCompleted(target.TargetId))
        {
            return;
        }

        ConfirmCompletedRdwStep(target.TargetId, state.stateVersion);

        Replanning?.Invoke(state);
        RequestNextGuidance(SemanticReplanReason.TargetArrived);
    }

    /// <summary>
    /// 取消当前 Guidance 请求和表现，使暂停后的旧响应无法继续执行。
    /// </summary>
    public void CancelActiveGuidance()
    {
        InvalidateRdwPlanning(
            RoutePlanInvalidationReason.GuidanceCancelled,
            "Active guidance was cancelled.");
        requestClient?.CancelPendingRequest();
        semanticPlanClient?.CancelPendingSemanticPlan();
        pendingSemanticPlanRequest = null;
        pendingSemanticPlanId = string.Empty;
        taskController?.RuntimeState.InvalidateSemanticPlan();
        guidanceExecutor?.Clear();
        rotationGainController?.CancelGain("guidance_cancelled");
        arrivalDetector?.WatchReservation(null);
        // Reset 只释放携带旧 Physical Risk 的 Candidate Set；保留已提交的语义目标预留。
        taskController.AbandonCandidateSetReservation();
        turnCueCompletionTracker.Cancel();
        ResetRuntimeSafetySampling();
        automaticResetAttempts = 0;
        resetAbandonedTargetId = string.Empty;
        postResetNavigationRecoveryPending = false;
        navigationRecoveryResetAttempts = 0;
        navigationRecoveryHoldStartedAt = float.PositiveInfinity;
        hasLastCompletedRecoveryForward = false;
        ClearDeferredNavigationRecovery();
        ExitReplanHold("guidance_cancelled");
        if (requestPhase == GuidanceRequestPhase.Requested ||
            requestPhase == GuidanceRequestPhase.Received ||
            requestPhase == GuidanceRequestPhase.Verified ||
            requestPhase == GuidanceRequestPhase.Executing)
        {
            SetRequestPhase(GuidanceRequestPhase.Cancelled);
        }
    }

    /// <summary>
    /// 请求 Coordinator 继续 Completed Reset；稳定窗口与 Safe 复验尚未完成时不会恢复任务。
    /// </summary>
    /// <returns>任务从 PausedForReset 恢复并进入导航时返回 true。</returns>
    public bool ResumeAfterResetAndReplan()
    {
        if (taskController == null ||
            pauseResetController == null ||
            taskController.RuntimeState.phase != GuidanceRuntimePhase.PausedForReset ||
            pauseResetController.State != ResetLifecycleState.Completed)
        {
            return false;
        }

        ReevaluateAfterReset();
        return taskController.RuntimeState.IsNavigating &&
               pauseResetController.State == ResetLifecycleState.Inactive &&
               recoveryState == GuidanceRecoveryState.Inactive;
    }

    /// <summary>
    /// Reset 完成后基于最新 Physical Pose 统一复验；不以语义候选存在作为恢复完成条件。
    /// </summary>
    public void ReevaluateAfterReset()
    {
        if (recoveryDecisionPending || pauseResetController == null || taskController == null ||
            pauseResetController.State != ResetLifecycleState.Completed ||
            taskController.RuntimeState.phase != GuidanceRuntimePhase.PausedForReset ||
            recoveryState != GuidanceRecoveryState.Stabilizing ||
            Time.realtimeSinceStartup < recoveryRevalidationAt)
        {
            return;
        }

        recoveryDecisionPending = true;
        try
        {
            // EvaluateNow 会同步发布 RiskStateChanged；Reset 抑制门保证该事件只更新事实与日志。
            var riskState = riskEvaluator != null ? riskEvaluator.EvaluateNow() : null;
            TransitionRecoveryTo(GuidanceRecoveryState.Revalidating, "stabilization_completed");
            if (!HasTrustedRecoveryFacts(riskState))
            {
                TriggerRecoveryHardStop(
                    "Reset completed but the latest risk state is unavailable or invalid.");
                return;
            }

            // 兼容由 Dashboard 或旧场景直接启动的 Reset：桥层已经持有同一冻结物理朝向。
            if (frozenRecoveryPlan == null && pauseResetController.ActivePlan != null)
            {
                frozenRecoveryPlan = pauseResetController.ActivePlan;
            }

            if (!TryEvaluateFrozenRecoveryHeading(riskState, out var frozenHeading) ||
                !PhysicalRiskEvaluator.IsRecoveryHeadingAcceptable(frozenHeading))
            {
                if (!pauseResetController.ResumeAfterReset())
                {
                    TriggerRecoveryHardStop("Completed Reset could not return to the inactive state.");
                    return;
                }

                TriggerPauseReset(
                    null,
                    riskState,
                    "Frozen recovery heading is not explicitly safe; selecting a new physical heading.");
                return;
            }

            CompleteResetAndReplan();
        }
        finally
        {
            recoveryDecisionPending = false;
            ConsumeDeferredNavigationRecovery();
        }
    }

    private void CompleteResetAndReplan()
    {
        if (frozenRecoveryPlan != null && frozenRecoveryPlan.IsSuccess)
        {
            lastCompletedRecoveryForward = frozenRecoveryPlan.SafePhysicalForward;
            hasLastCompletedRecoveryForward = true;
        }

        TransitionRecoveryTo(GuidanceRecoveryState.Replanning, "recovery_heading_revalidated");
        if (!pauseResetController.ResumeAfterReset())
        {
            TriggerRecoveryHardStop("Reset completed but could not be released.");
            return;
        }

        taskController.ResumeAfterReset();
        var state = taskController.RuntimeState;
        if (!state.IsNavigating)
        {
            TriggerRuntimeIntervention(null, "Task could not resume navigation after Reset.");
            return;
        }

        automaticResetAttempts = 0;
        resetAbandonedTargetId = string.Empty;
        ExitReplanHold("reset_recovery_completed");
        TransitionRecoveryTo(GuidanceRecoveryState.Inactive, "automatic_recovery_completed");
        frozenRecoveryPlan = null;
        InvalidateRdwPlanning(
            RoutePlanInvalidationReason.ResetCompleted,
            "Reset completed; the next request must capture a fresh PlanningSnapshot.");
        Replanning?.Invoke(state);
        RequestNextGuidance(SemanticReplanReason.ResetCompleted);
    }

    private void TriggerRecoveryHardStop(string reason)
    {
        ClearRecoveryHold();
        postResetNavigationRecoveryPending = false;
        navigationRecoveryHoldStartedAt = float.PositiveInfinity;
        ClearDeferredNavigationRecovery();
        TransitionRecoveryTo(GuidanceRecoveryState.Terminated, reason);
        pauseResetController?.CancelReset(reason);
        taskController?.EndTask();
        TriggerRuntimeIntervention(null, reason);
    }

    private void SetRequestPhase(GuidanceRequestPhase nextPhase)
    {
        if (requestPhase == nextPhase)
        {
            return;
        }

        requestPhase = nextPhase;
        RequestPhaseChanged?.Invoke(requestPhase);
    }

    private void FinishActiveGuidanceForProgress()
    {
        if (requestPhase == GuidanceRequestPhase.Executing)
        {
            SetRequestPhase(GuidanceRequestPhase.Completed);
        }
        else if (requestPhase == GuidanceRequestPhase.Requested ||
                 requestPhase == GuidanceRequestPhase.Received ||
                 requestPhase == GuidanceRequestPhase.Verified)
        {
            SetRequestPhase(GuidanceRequestPhase.Cancelled);
        }

        requestClient?.CancelPendingRequest();
        guidanceExecutor?.Clear();
        rotationGainController?.CancelGain("guidance_completed");
        activeSnapshot = null;
        activeResponse = null;
        turnCueCompletionTracker.Cancel();
        ResetRuntimeSafetySampling();
    }

    private void TriggerRuntimeIntervention(GuidanceResponse response, string reason, bool startReset = false)
    {
        var resolvedReason = string.IsNullOrWhiteSpace(reason)
            ? "Hard Runtime Safety rejected unsafe guidance."
            : reason;
        InvalidateRdwPlanning(
            RoutePlanInvalidationReason.FirstStepRejected,
            resolvedReason);
        requestClient?.CancelPendingRequest();
        guidanceExecutor?.Clear();
        rotationGainController?.AbortGain("runtime_intervention");
        arrivalDetector?.WatchReservation(null);
        AbandonReservations();
        activeSnapshot = null;
        activeResponse = null;
        turnCueCompletionTracker.Cancel();
        ResetRuntimeSafetySampling();
        ExitReplanHold("runtime_intervention");
        SetRequestPhase(GuidanceRequestPhase.Failed);
        RuntimeIntervention?.Invoke(new RuntimeInterventionInfo(
            response,
            resolvedReason,
            RuntimeGuidanceSafetyDecision.HardStop,
            startReset,
            automaticResetAttempts,
            false));
    }

    private void ExpireAndReplan(GuidanceResponse response, string reason)
    {
        InvalidateRdwPlanning(
            RoutePlanInvalidationReason.FirstStepRejected,
            reason);
        var riskState = riskEvaluator != null ? riskEvaluator.CurrentRiskState : null;
        var rejectionFingerprint = BuildRejectionFingerprint(response, riskState, reason);
        if (!string.IsNullOrEmpty(rejectionFingerprint) &&
            string.Equals(lastDeterministicRejectionFingerprint, rejectionFingerprint, StringComparison.Ordinal))
        {
            EnterHoldAndReplan(
                riskState,
                rejectionFingerprint,
                reason,
                true);
            return;
        }

        lastDeterministicRejectionFingerprint = rejectionFingerprint;
        guidanceExecutor?.Clear();
        rotationGainController?.CancelGain("guidance_expired");
        arrivalDetector?.WatchReservation(null);
        AbandonReservations();
        activeSnapshot = null;
        activeResponse = null;
        turnCueCompletionTracker.Cancel();
        ResetRuntimeSafetySampling();
        SetRequestPhase(GuidanceRequestPhase.Expired);
        GuidanceExpired?.Invoke(reason);
        RequestNextGuidance(SemanticReplanReason.GuidanceExpired);
    }

    private void BindRuntimeSafety()
    {
        if (riskEvaluator == null || !isActiveAndEnabled)
        {
            return;
        }

        riskEvaluator.RiskStateChanged -= HandleRuntimeRiskStateChanged;
        riskEvaluator.RiskStateChanged += HandleRuntimeRiskStateChanged;
    }

    private void UnbindRuntimeSafety()
    {
        if (riskEvaluator != null)
        {
            riskEvaluator.RiskStateChanged -= HandleRuntimeRiskStateChanged;
        }
    }

    private void HandleRuntimeRiskStateChanged(PhysicalRiskState riskState)
    {
        if (recoveryState == GuidanceRecoveryState.HoldingForSafeHeading)
        {
            TryResumeRecoveryHold(riskState, false);
            return;
        }

        if (IsResetRecoveryActive)
        {
            HandleActiveRecoveryRiskChanged(riskState);
            return;
        }

        if (runtimeReplanPending)
        {
            return;
        }

        if (holdForReplan)
        {
            if (string.IsNullOrEmpty(GetReplanHoldWakeReason(riskState)))
            {
                return;
            }

            if (recoveryState == GuidanceRecoveryState.RecoveryHold)
            {
                TransitionRecoveryTo(
                    GuidanceRecoveryState.NavigationRecovery,
                    "recovery_hold_woken_by_risk");
            }
            RequestNextGuidance(SemanticReplanReason.RuntimeFactsChanged);
            return;
        }

        if (requestPhase == GuidanceRequestPhase.Requested ||
            requestPhase == GuidanceRequestPhase.Received)
        {
            HandlePendingRequestRiskChanged(riskState);
            return;
        }

        if (requestPhase != GuidanceRequestPhase.Executing ||
            activeSnapshot == null ||
            activeResponse == null)
        {
            return;
        }

        var plannedDirection = navigationTargets != null &&
                               navigationTargets.TryGetTargetDirection(
                                   activeResponse.selected_target,
                                   out var displayDirection)
            ? displayDirection
            : "exact";
        var directionAvailable = TryEvaluateTargetExactHeading(
            activeResponse.selected_target,
            riskState,
            out var plannedHeading);
        var plannedClearance = directionAvailable ? plannedHeading.ClearanceMeters : 0f;

        UpdateUnsafeSampleCount(
            riskState,
            activeResponse.selected_target,
            directionAvailable,
            plannedHeading.Safety);
        var safeCandidate = FindRuntimeSafetyCandidate(riskState, activeResponse.selected_target);
        var plannedDirectionCritical = directionAvailable && plannedHeading.Safety == HeadingSafety.Critical;
        var requiresSafetyIntervention = RequiresRuntimeSafetyIntervention(
            directionAvailable,
            plannedHeading.Safety,
            plannedDirectionCritical);
        // 仅在确实需要安全介入且没有语义绕行时执行全角域搜索，避免正常安全帧重复查询物理几何。
        var shouldSearchRecovery = safeCandidate == null &&
                                   (requiresSafetyIntervention || !directionAvailable);
        var hasRecoveryHeading = shouldSearchRecovery &&
                                 SearchRecoveryHeading(riskState).Plan.IsSuccess;
        var input = new RuntimeGuidanceSafetyInput(
            activeSnapshot.decisionId,
            activeResponse.selected_target,
            activeResponse.movement_instruction,
            riskState,
            riskEvaluator != null && riskEvaluator.IsTrackingValid,
            riskEvaluator != null && riskEvaluator.IsCalibrationValid,
            riskEvaluator != null && riskEvaluator.IsRiskIdentityCurrent(riskState),
            plannedDirection,
            plannedClearance,
            directionAvailable,
            plannedDirectionCritical,
            pauseResetController != null ? pauseResetController.State : ResetLifecycleState.Inactive,
            consecutiveUnsafeSamples,
            requiresSafetyIntervention,
            safeCandidate != null,
            hasRecoveryHeading);

        // 方向事实缺失不必然代表实验不可恢复；基础身份有效时优先复用现有绕行或 Reset 链。
        if (!input.PlannedDirectionAvailable &&
            TryRouteUnavailablePlannedDirection(input, safeCandidate, riskState))
        {
            return;
        }

        runtimeSafetyPolicy.SoftRerouteEnabled = enableSoftRerouteSafetyPolicy;
        var decision = runtimeSafetyPolicy.Evaluate(input);
        if (decision == RuntimeGuidanceSafetyDecision.SoftReroute)
        {
            TriggerSoftReroute(safeCandidate);
        }
        else if (decision == RuntimeGuidanceSafetyDecision.PauseReset)
        {
            if (HasTrustedRecoveryFacts(riskState))
            {
                TriggerPauseReset(activeResponse, riskState, RuntimeSafetyReason(input));
            }
            else
            {
                TriggerRuntimeIntervention(
                    activeResponse,
                    IsUserInsidePhysicalBoundary(riskState)
                        ? "No meaningful safe turning direction is available for automatic reset."
                        : "The user is outside the physical boundary.");
            }
        }
        else if (decision == RuntimeGuidanceSafetyDecision.HardStop)
        {
            TriggerRuntimeIntervention(
                activeResponse,
                RuntimeSafetyReason(input));
        }
    }

    private void BindRecoveryController()
    {
        if (pauseResetController == null)
        {
            return;
        }

        pauseResetController.StateChanged -= HandleResetStateChanged;
        pauseResetController.StateChanged += HandleResetStateChanged;
        pauseResetController.ResetCompleted -= HandleResetCompleted;
        pauseResetController.ResetCompleted += HandleResetCompleted;
        pauseResetController.MappingFailed -= HandleResetMappingFailed;
        pauseResetController.MappingFailed += HandleResetMappingFailed;
    }

    private void UnbindRecoveryController()
    {
        if (pauseResetController == null)
        {
            return;
        }

        pauseResetController.StateChanged -= HandleResetStateChanged;
        pauseResetController.ResetCompleted -= HandleResetCompleted;
        pauseResetController.MappingFailed -= HandleResetMappingFailed;
    }

    private void HandleResetStateChanged(ResetLifecycleState state)
    {
        if (state == ResetLifecycleState.Required ||
            state == ResetLifecycleState.TurningToSafeHeading)
        {
            InvalidateRdwPlanning(
                RoutePlanInvalidationReason.ResetStarted,
                "Reset started; physical route identities are stale, semantic target is retained.",
                true);
        }
        else if (state == ResetLifecycleState.Completed)
        {
            InvalidateRdwPlanning(
                RoutePlanInvalidationReason.ResetCompleted,
                "Reset completed; waiting for fresh physical facts while retaining semantic target.",
                true);
            recoveryRevalidationAt = Time.realtimeSinceStartup + Mathf.Max(0f, resetStabilizationSeconds);
            TransitionRecoveryTo(
                GuidanceRecoveryState.Stabilizing,
                "physical_heading_and_mapping_completed");
        }
        else if (state == ResetLifecycleState.Cancelled &&
                 recoveryState != GuidanceRecoveryState.HoldingForSafeHeading &&
                 recoveryState != GuidanceRecoveryState.Terminated)
        {
            recoveryRevalidationAt = float.PositiveInfinity;
            TransitionRecoveryTo(GuidanceRecoveryState.Inactive, "reset_cancelled");
        }
    }

    private void HandleResetCompleted(ResetEventInfo _)
    {
        // 测试或显式配置为零稳定时间时允许同步复验；生产默认由 Update 等待稳定窗口。
        ReevaluateAfterReset();
    }

    private void HandleResetMappingFailed(string reason)
    {
        if (recoveryDecisionPending ||
            recoveryState != GuidanceRecoveryState.WaitingForUserTurn)
        {
            return;
        }

        EnterRecoveryHold(
            riskEvaluator != null ? riskEvaluator.CurrentRiskState : null,
            string.IsNullOrWhiteSpace(reason) ? "Reset mapping failed." : reason);
    }

    private void HandleActiveRecoveryRiskChanged(PhysicalRiskState riskState)
    {
        if (recoveryDecisionPending ||
            recoveryState != GuidanceRecoveryState.WaitingForUserTurn ||
            frozenRecoveryPlan == null)
        {
            return;
        }

        // Tracking 短暂丢失由会话层按配置时限等待；恢复计划保持冻结，不在此处误判为不可恢复。
        if (riskEvaluator != null &&
            (!riskEvaluator.IsTrackingValid || !riskEvaluator.IsCalibrationValid))
        {
            return;
        }

        if (!HasTrustedRecoveryFacts(riskState))
        {
            TriggerRecoveryHardStop("Automatic recovery lost trusted boundary or risk identity facts.");
            return;
        }

        if (!TryEvaluateFrozenRecoveryHeading(riskState, out var frozenHeading))
        {
            EnterRecoveryHold(
                riskState,
                "Frozen recovery heading could not be evaluated; waiting for new trusted risk facts.");
            return;
        }

        if (PhysicalRiskEvaluator.IsRecoveryHeadingAcceptable(frozenHeading))
        {
            return;
        }

        pauseResetController?.InvalidateMappingChain("frozen_heading_invalid");
        EnterRecoveryHold(
            riskState,
            "Frozen recovery heading is no longer explicitly Safe; waiting for new risk facts.");
    }

    private void TriggerSoftReroute(
        GuidanceTarget safeCandidate,
        string reason = "Active planned direction remained unsafe for two consecutive risk samples.",
        SemanticReplanReason replanReason = SemanticReplanReason.TargetUnreachable)
    {
        if (!enableSoftRerouteSafetyPolicy || runtimeReplanPending || safeCandidate == null)
        {
            return;
        }

        runtimeReplanPending = true;
        var interruptedResponse = activeResponse;
        try
        {
            InvalidateRdwPlanning(
                RoutePlanInvalidationReason.FirstStepRejected,
                reason);
            requestClient?.CancelPendingRequest();
            guidanceExecutor?.Clear();
            rotationGainController?.AbortGain("soft_reroute");
            arrivalDetector?.WatchReservation(null);
            AbandonReservations();
            activeSnapshot = null;
            activeResponse = null;
            turnCueCompletionTracker.Cancel();
            ResetRuntimeSafetySampling();
            SetRequestPhase(GuidanceRequestPhase.Cancelled);
            RuntimeIntervention?.Invoke(new RuntimeInterventionInfo(
                interruptedResponse,
                reason,
                RuntimeGuidanceSafetyDecision.SoftReroute,
                false,
                0,
                true));
            Replanning?.Invoke(taskController != null ? taskController.RuntimeState : null);
            RequestNextGuidance(replanReason);
        }
        finally
        {
            runtimeReplanPending = false;
        }
    }

    private void TriggerPauseReset(
        GuidanceResponse response,
        PhysicalRiskState riskState,
        string reason)
    {
        if (pauseResetController == null || taskController == null)
        {
            TriggerRuntimeIntervention(response, "Automatic reset dependencies are unavailable.");
            return;
        }

        if (IsResetRecoveryActive)
        {
            return;
        }

        var resolvedReason = string.IsNullOrWhiteSpace(reason)
            ? "Planned movement is physically unsafe; starting automatic heading recovery."
            : reason;
        if (string.IsNullOrWhiteSpace(resetAbandonedTargetId) && response != null)
        {
            resetAbandonedTargetId = response.selected_target ?? string.Empty;
        }

        TransitionRecoveryTo(GuidanceRecoveryState.StoppingGuidance, resolvedReason);
        InvalidateRdwPlanning(
            RoutePlanInvalidationReason.ResetStarted,
            resolvedReason);
        requestClient?.CancelPendingRequest();
        guidanceExecutor?.Clear();
        handlingGainTermination = true;
        rotationGainController?.AbortGain("pause_reset");
        handlingGainTermination = false;
        arrivalDetector?.WatchReservation(null);
        AbandonReservations();
        activeSnapshot = null;
        activeResponse = null;
        turnCueCompletionTracker.Cancel();
        ResetRuntimeSafetySampling();
        SetRequestPhase(GuidanceRequestPhase.Cancelled);
        TransitionRecoveryTo(GuidanceRecoveryState.SelectingHeading, "guidance_stopped");
        taskController.PauseForReset();
        var searchResult = SearchRecoveryHeading(riskState);
        var selectedPostResetPlan = SelectPostResetRecoveryPlan(
            riskState,
            searchResult,
            null,
            out _);
        var nextPlan = selectedPostResetPlan ?? searchResult.Plan;
        if (!nextPlan.IsSuccess ||
            !TryEvaluateRecoveryPlanHeading(riskState, nextPlan, out var exactRecoveryHeading) ||
            !PhysicalRiskEvaluator.IsRecoveryHeadingAcceptable(exactRecoveryHeading))
        {
            EnterRecoveryHold(
                riskState,
                nextPlan.IsSuccess
                    ? "Selected recovery heading failed exact final validation."
                    : RecoveryFailureReason(nextPlan.Failure));
            return;
        }

        ClearRecoveryHold();
        automaticResetAttempts++;
        postResetNavigationRecoveryPending = true;
        navigationRecoveryResetAttempts = 0;
        navigationRecoveryHoldStartedAt = float.PositiveInfinity;
        frozenRecoveryPlan = nextPlan;
        RuntimeIntervention?.Invoke(new RuntimeInterventionInfo(
            response,
            resolvedReason,
            RuntimeGuidanceSafetyDecision.PauseReset,
            true,
            automaticResetAttempts,
            false));
        if (!pauseResetController.PauseForReset(resolvedReason, frozenRecoveryPlan))
        {
            TriggerRecoveryHardStop("Automatic recovery controller rejected the frozen heading plan.");
            return;
        }

        TransitionRecoveryTo(GuidanceRecoveryState.WaitingForUserTurn, "recovery_heading_selected");
    }

    /// <summary>
    /// 在尚无活动 Guidance 时根据最新物理事实启动自动朝向恢复；不可恢复时自动终止任务。
    /// </summary>
    /// <param name="riskState">当前可信物理风险快照。</param>
    /// <param name="reason">触发恢复的诊断原因。</param>
    /// <returns>已处于或成功进入自动恢复状态时返回 true。</returns>
    public bool BeginAutomaticRecovery(PhysicalRiskState riskState, string reason)
    {
        if (IsResetRecoveryActive)
        {
            return true;
        }

        if (taskController == null || !taskController.RuntimeState.IsNavigating)
        {
            TriggerRecoveryHardStop("Automatic recovery requires an active navigating task.");
            return false;
        }

        if (!HasTrustedRecoveryFacts(riskState))
        {
            TriggerRecoveryHardStop(
                "Automatic recovery has no trusted tracking, calibration, or boundary facts.");
            return false;
        }

        TriggerPauseReset(null, riskState, reason);
        return IsAutomaticRecoveryActive;
    }

    private void EnterRecoveryHold(PhysicalRiskState riskState, string reason)
    {
        if (!HasTrustedRecoveryFacts(riskState))
        {
            TriggerRecoveryHardStop(
                "Automatic recovery lost trusted tracking, calibration, boundary, or risk identity facts.");
            return;
        }

        taskController?.PauseForReset();
        frozenRecoveryPlan = null;
        recoveryRevalidationAt = float.PositiveInfinity;
        recoveryHoldStartedAt = Time.realtimeSinceStartup;
        nextRecoveryHoldAttemptAt = Time.realtimeSinceStartup + Mathf.Max(0.05f, recoveryHoldRetryIntervalSeconds);
        recoveryHoldAttemptCount = 0;
        CaptureRecoveryHoldFacts(riskState);
        TransitionRecoveryTo(
            GuidanceRecoveryState.HoldingForSafeHeading,
            string.IsNullOrWhiteSpace(reason)
                ? "No explicitly Safe recovery heading is currently available."
                : reason);
        pauseResetController?.CancelReset("recovery_holding_for_safe_heading", true);
    }

    private void UpdateRecoveryHold(PhysicalRiskState riskState)
    {
        if (IsRecoveryHoldBudgetExhausted())
        {
            TriggerRecoveryHardStop("Recovery Hold budget was exhausted without an explicitly Safe heading.");
            return;
        }

        TryResumeRecoveryHold(riskState, true);
    }

    private void TryResumeRecoveryHold(PhysicalRiskState riskState, bool allowTimerWake)
    {
        if (recoveryDecisionPending || recoveryState != GuidanceRecoveryState.HoldingForSafeHeading)
        {
            return;
        }

        // Tracking/Calibration 的短暂丢失由会话层预算处理；此处保留 Recovery Hold。
        if (riskEvaluator != null &&
            (!riskEvaluator.IsTrackingValid || !riskEvaluator.IsCalibrationValid))
        {
            return;
        }

        if (!HasTrustedRecoveryFacts(riskState))
        {
            TriggerRecoveryHardStop(
                "Recovery Hold lost trusted boundary or risk identity facts.");
            return;
        }

        if (IsRecoveryHoldBudgetExhausted())
        {
            TriggerRecoveryHardStop("Recovery Hold budget was exhausted without an explicitly Safe heading.");
            return;
        }

        var factsChanged = HasRecoveryHoldFactsChanged(riskState);
        var timerElapsed = allowTimerWake && Time.realtimeSinceStartup >= nextRecoveryHoldAttemptAt;
        if (!factsChanged && !timerElapsed)
        {
            return;
        }

        recoveryDecisionPending = true;
        try
        {
            recoveryHoldAttemptCount++;
            CaptureRecoveryHoldFacts(riskState);
            nextRecoveryHoldAttemptAt = Time.realtimeSinceStartup + Mathf.Max(0.05f, recoveryHoldRetryIntervalSeconds);
            var searchResult = SearchRecoveryHeading(riskState, true);
            var selectedPostResetPlan = SelectPostResetRecoveryPlan(
                riskState,
                searchResult,
                null,
                out _);
            var plan = selectedPostResetPlan ?? searchResult.Plan;
            if (!plan.IsSuccess ||
                !TryEvaluateRecoveryPlanHeading(riskState, plan, out var selectedHeading) ||
                !PhysicalRiskEvaluator.IsRecoveryHeadingAcceptable(selectedHeading))
            {
                if (IsRecoveryHoldBudgetExhausted())
                {
                    TriggerRecoveryHardStop(
                        "Recovery Hold retry budget was exhausted without an explicitly Safe heading.");
                }
                return;
            }

            frozenRecoveryPlan = plan;
            ClearRecoveryHold();
            automaticResetAttempts++;
            postResetNavigationRecoveryPending = true;
            navigationRecoveryResetAttempts = 0;
            navigationRecoveryHoldStartedAt = float.PositiveInfinity;
            if (!pauseResetController.PauseForReset(
                    "An explicitly Safe recovery heading became available.",
                    frozenRecoveryPlan))
            {
                TriggerRecoveryHardStop("Automatic recovery controller rejected the recovered heading plan.");
                return;
            }

            TransitionRecoveryTo(GuidanceRecoveryState.WaitingForUserTurn, "recovery_hold_safe_heading_selected");
        }
        finally
        {
            recoveryDecisionPending = false;
        }
    }

    private bool IsRecoveryHoldBudgetExhausted()
    {
        return recoveryState == GuidanceRecoveryState.HoldingForSafeHeading &&
               (Time.realtimeSinceStartup - recoveryHoldStartedAt >= Mathf.Max(0.1f, recoveryHoldBudgetSeconds) ||
                recoveryHoldAttemptCount >= Mathf.Max(1, recoveryHoldMaxAttempts));
    }

    private bool HasRecoveryHoldFactsChanged(PhysicalRiskState riskState)
    {
        if (riskState == null || !recoveryHoldPoseCaptured)
        {
            return riskState != null;
        }

        return riskState.risk_revision != recoveryHoldRiskRevision ||
               Vector2.Distance(riskState.user_physical_position, recoveryHoldPhysicalPosition) >=
               Mathf.Max(0.01f, replanWakePositionMeters) ||
               Vector2.Angle(
                   NormalizePhysicalForward(riskState.user_physical_forward),
                   recoveryHoldPhysicalForward) >= Mathf.Clamp(replanWakeHeadingDegrees, 1f, 180f);
    }

    private void CaptureRecoveryHoldFacts(PhysicalRiskState riskState)
    {
        recoveryHoldPoseCaptured = riskState != null;
        recoveryHoldRiskRevision = riskState != null ? riskState.risk_revision : -1;
        recoveryHoldPhysicalPosition = riskState != null
            ? riskState.user_physical_position
            : Vector2.zero;
        recoveryHoldPhysicalForward = riskState != null
            ? NormalizePhysicalForward(riskState.user_physical_forward)
            : Vector2.up;
    }

    private void ClearRecoveryHold()
    {
        recoveryHoldStartedAt = float.PositiveInfinity;
        nextRecoveryHoldAttemptAt = float.PositiveInfinity;
        recoveryHoldAttemptCount = 0;
        recoveryHoldRiskRevision = -1;
        recoveryHoldPhysicalPosition = Vector2.zero;
        recoveryHoldPhysicalForward = Vector2.up;
        recoveryHoldPoseCaptured = false;
    }

    private GuidanceTarget FindRuntimeSafetyCandidate(
        PhysicalRiskState riskState,
        string activeTargetId,
        bool allowPausedForReset = false)
    {
        if (semanticTargetSelector == null || taskController == null || riskState == null)
        {
            return null;
        }

        var candidateSet = semanticTargetSelector.BuildCandidateSet(
            taskController.RuntimeState,
            riskState,
            ExperimentConditionMatrix.GetPolicy(taskController.CurrentCondition),
            allowPausedForReset);
        if (candidateSet == null)
        {
            return null;
        }

        foreach (var candidate in candidateSet.Candidates)
        {
            if (candidate == null ||
                string.Equals(candidate.TargetId, activeTargetId, StringComparison.Ordinal) ||
                semanticTargetSelector.GetCandidateSafetyState(candidate.TargetId, riskState) !=
                CandidateSafetyState.Eligible)
            {
                continue;
            }

            return new GuidanceTarget(candidate.TargetId, candidate.TargetRole);
        }

        return null;
    }

    private void HandlePendingRequestRiskChanged(PhysicalRiskState riskState)
    {
        if (taskController == null)
        {
            TriggerRuntimeIntervention(null, "Pending guidance request has no stable request context.");
            return;
        }

        // AgentClient 构建请求与发布快照之间存在同帧窗口，此时等待 RequestBuilt 完成即可。
        if (activeSnapshot == null)
        {
            return;
        }

        if (!HasTrustedRecoveryFacts(riskState) || !activeSnapshot.MatchesTaskContext(taskController.RuntimeState, riskState))
        {
            TriggerRuntimeIntervention(null, "Pending guidance request received unavailable or mismatched safety facts.");
            return;
        }

        var reservation = taskController.RuntimeState.ActiveCandidateSetReservation;
        if (reservation == null)
        {
            TriggerRuntimeIntervention(null, "Pending guidance request lost its frozen candidate reservation.");
            return;
        }

        // Safe 或 Unknown 抖动都保留在途请求；只有全部模型候选明确 Blocked 才取消。
        if (!AreAllFrozenCandidatesExplicitlyBlocked(reservation, riskState))
        {
            return;
        }

        EnterHoldAndReplan(
            riskState,
            BuildPlanningFingerprint(riskState),
            "Pending guidance candidates are not currently executable; holding for replanning.");
    }

    private bool AreAllFrozenCandidatesExplicitlyBlocked(
        CandidateSetReservation reservation,
        PhysicalRiskState riskState)
    {
        if (reservation == null || semanticTargetSelector == null || taskController == null)
        {
            return false;
        }

        var found = false;
        foreach (var targetId in reservation.ModelCandidateIds)
        {
            if (!reservation.TryGetCandidate(targetId, out var frozenCandidate) ||
                !semanticTargetSelector.TryAssessCandidate(
                    targetId,
                    frozenCandidate.TargetRole,
                    riskState,
                    out var assessment) ||
                assessment == null)
            {
                return false;
            }

            found = true;
            if (assessment.ExecutionState != CandidateExecutionState.Blocked)
            {
                return false;
            }
        }

        return found;
    }

    private void HandleUnavailableCandidatePlanning(
        PhysicalRiskState riskState,
        NavigationCandidateSet candidateSet,
        string planningFingerprint)
    {
        if (!HasTrustedRecoveryFacts(riskState))
        {
            TriggerRuntimeIntervention(
                null,
                "Guidance planning safety facts are unavailable or invalid.");
            return;
        }

        // Reset 后无论候选为空还是全部不可执行，都先走一次主动导航恢复，避免直接进入长期 Hold。
        if (postResetNavigationRecoveryPending && candidateSet != null)
        {
            EmitNavigationRecoveryDiagnostic(
                "fresh_replan_zero_candidates",
                planningFingerprint);
            BeginNavigationRecovery(riskState, planningFingerprint);
            return;
        }

        EnterHoldAndReplan(
            riskState,
            planningFingerprint,
            "No semantic candidate is currently executable; holding while the task remains active.");
    }

    /// <summary>
    /// Reset 后 0 executable candidates 时尝试受控恢复或最多一次 Recovery Reset。
    /// </summary>
    private void BeginNavigationRecovery(
        PhysicalRiskState riskState,
        string planningFingerprint)
    {
        if (recoveryDecisionPending)
        {
            if (postResetNavigationRecoveryPending &&
                recoveryState != GuidanceRecoveryState.NavigationRecovery &&
                recoveryState != GuidanceRecoveryState.WaitingForUserTurn &&
                recoveryState != GuidanceRecoveryState.RecoveryHold)
            {
                DeferNavigationRecoveryOnce(riskState, planningFingerprint);
            }
            return;
        }

        if (!postResetNavigationRecoveryPending ||
            !HasTrustedRecoveryFacts(riskState) || taskController == null ||
            !taskController.RuntimeState.IsNavigating)
        {
            return;
        }

        recoveryDecisionPending = true;
        try
        {
            EmitNavigationRecoveryDiagnostic(
                "navigation_recovery_started",
                planningFingerprint);
            TransitionRecoveryTo(
                GuidanceRecoveryState.NavigationRecovery,
                "navigation_recovery_started");

            var searchResult = SearchRecoveryHeading(riskState, true);
            var candidatePlan = SelectPostResetRecoveryPlan(
                riskState,
                searchResult,
                hasLastCompletedRecoveryForward ? lastCompletedRecoveryForward : (Vector2?)null,
                out var continuation,
                planningFingerprint);

            if (candidatePlan == null || !candidatePlan.IsSuccess ||
                continuation.Level == PostResetContinuationLevel.HoldOnly)
            {
                EnterNavigationRecoveryHold(
                    riskState,
                    planningFingerprint,
                    "No direct target, safe reroute, or RDW recovery is currently available.");
                return;
            }

            var budget = Mathf.Clamp(navigationRecoveryResetBudget, 0, 1);
            if (navigationRecoveryResetAttempts >= budget)
            {
                EnterNavigationRecoveryHold(
                    riskState,
                    planningFingerprint,
                    "Navigation Recovery Reset budget has been exhausted; waiting for new facts.");
                return;
            }

            navigationRecoveryResetAttempts++;
            frozenRecoveryPlan = candidatePlan;
            taskController.PauseForReset();
            InvalidateRdwPlanning(
                RoutePlanInvalidationReason.ResetStarted,
                "Navigation Recovery selected a new Safe Heading for one bounded Recovery Reset.");
            ExitReplanHold("navigation_recovery_reset_started");
            var recoveryResetReason = string.IsNullOrWhiteSpace(continuation.TargetId)
                ? "Navigation Recovery selected a Safe Heading with RDW continuation."
                : $"Navigation Recovery selected continuation target '{continuation.TargetId}'.";
            RuntimeIntervention?.Invoke(new RuntimeInterventionInfo(
                null,
                recoveryResetReason,
                RuntimeGuidanceSafetyDecision.PauseReset,
                true,
                navigationRecoveryResetAttempts + 1,
                continuation.Level != PostResetContinuationLevel.HoldOnly));

            if (pauseResetController == null ||
                !pauseResetController.PauseForReset(recoveryResetReason, frozenRecoveryPlan))
            {
                TriggerRecoveryHardStop("Navigation Recovery Reset was rejected by the reset controller.");
                return;
            }

            EmitNavigationRecoveryDiagnostic(
                "recovery_reset_started",
                planningFingerprint);
            TransitionRecoveryTo(
                GuidanceRecoveryState.WaitingForUserTurn,
                "navigation_recovery_heading_selected");
        }
        finally
        {
            recoveryDecisionPending = false;
        }
    }

    private void DeferNavigationRecoveryOnce(
        PhysicalRiskState riskState,
        string planningFingerprint)
    {
        if (navigationRecoveryDeferred)
        {
            return;
        }

        navigationRecoveryDeferred = true;
        deferredNavigationRecoveryRiskState = riskState;
        deferredNavigationRecoveryPlanningFingerprint = planningFingerprint ?? string.Empty;
        EmitNavigationRecoveryDiagnostic(
            "navigation_recovery_deferred",
            deferredNavigationRecoveryPlanningFingerprint);
    }

    private void ConsumeDeferredNavigationRecovery()
    {
        if (!navigationRecoveryDeferred)
        {
            return;
        }

        var riskState = deferredNavigationRecoveryRiskState;
        var planningFingerprint = deferredNavigationRecoveryPlanningFingerprint;
        ClearDeferredNavigationRecovery();
        EmitNavigationRecoveryDiagnostic(
            "navigation_recovery_deferred_consumed",
            planningFingerprint);
        BeginNavigationRecovery(riskState, planningFingerprint);
    }

    private void ClearDeferredNavigationRecovery()
    {
        navigationRecoveryDeferred = false;
        deferredNavigationRecoveryRiskState = null;
        deferredNavigationRecoveryPlanningFingerprint = string.Empty;
    }

    private void EmitNavigationRecoveryDiagnostic(
        string eventName,
        string planningFingerprint,
        IList<PostResetHeadingSelectionRecord> headingSelections = null)
    {
        NavigationRecoveryDiagnosed?.Invoke(new NavigationRecoveryDiagnosticInfo(
            eventName,
            planningFingerprint,
            navigationRecoveryResetAttempts,
            headingSelections));
    }

    private void EnterNavigationRecoveryHold(
        PhysicalRiskState riskState,
        string planningFingerprint,
        string reason)
    {
        var started = recoveryState != GuidanceRecoveryState.RecoveryHold;
        if (float.IsPositiveInfinity(navigationRecoveryHoldStartedAt))
        {
            navigationRecoveryHoldStartedAt = Time.realtimeSinceStartup;
        }

        TransitionRecoveryTo(GuidanceRecoveryState.RecoveryHold, reason);
        if (started)
        {
            EmitNavigationRecoveryDiagnostic(
                "recovery_hold_started",
                planningFingerprint);
        }
        EnterHoldAndReplan(riskState, planningFingerprint, reason, false);
    }

    private bool IsNavigationRecoveryHoldBudgetExhausted()
    {
        return recoveryState == GuidanceRecoveryState.RecoveryHold &&
               !float.IsPositiveInfinity(navigationRecoveryHoldStartedAt) &&
               Time.realtimeSinceStartup - navigationRecoveryHoldStartedAt >=
               Mathf.Max(0.1f, navigationRecoveryHoldBudgetSeconds);
    }

    private void CompletePostResetNavigationRecovery(string reason)
    {
        if (!postResetNavigationRecoveryPending &&
            recoveryState != GuidanceRecoveryState.NavigationRecovery &&
            recoveryState != GuidanceRecoveryState.RecoveryHold)
        {
            return;
        }

        postResetNavigationRecoveryPending = false;
        navigationRecoveryResetAttempts = 0;
        navigationRecoveryHoldStartedAt = float.PositiveInfinity;
        ClearDeferredNavigationRecovery();
        if (recoveryState == GuidanceRecoveryState.NavigationRecovery ||
            recoveryState == GuidanceRecoveryState.RecoveryHold)
        {
            TransitionRecoveryTo(GuidanceRecoveryState.Inactive, reason);
        }
    }

    private bool HasTrustedRecoveryFacts(PhysicalRiskState riskState)
    {
        return riskEvaluator != null &&
               riskState != null &&
               riskEvaluator.IsTrackingValid &&
               riskEvaluator.IsCalibrationValid &&
               riskEvaluator.IsRiskIdentityCurrent(riskState) &&
               riskState.boundary_context != null &&
               riskState.boundary_context.Count >= 3 &&
               IsUserInsidePhysicalBoundary(riskState);
    }

    private void EnterHoldAndReplan(
        PhysicalRiskState riskState,
        string planningFingerprint,
        string reason,
        bool requiresNewFacts = false)
    {
        var firstEntry = !holdForReplan;
        requestClient?.CancelPendingRequest();
        guidanceExecutor?.Clear();
        handlingGainTermination = true;
        rotationGainController?.AbortGain("hold_and_replan");
        handlingGainTermination = false;
        arrivalDetector?.WatchReservation(null);
        AbandonReservations();
        activeSnapshot = null;
        activeResponse = null;
        turnCueCompletionTracker.Cancel();
        ResetRuntimeSafetySampling();
        if (requestPhase == GuidanceRequestPhase.Requested ||
            requestPhase == GuidanceRequestPhase.Received ||
            requestPhase == GuidanceRequestPhase.Verified ||
            requestPhase == GuidanceRequestPhase.Executing)
        {
            SetRequestPhase(GuidanceRequestPhase.Cancelled);
        }

        holdForReplan = true;
        holdRiskRevision = riskState != null ? riskState.risk_revision : -1;
        holdTaskStateVersion = taskController != null && taskController.RuntimeState != null
            ? taskController.RuntimeState.stateVersion
            : 0;
        holdPlanningFingerprint = planningFingerprint ?? string.Empty;
        holdReason = reason ?? string.Empty;
        replanHoldRequiresNewFacts = requiresNewFacts;
        holdPhysicalPoseCaptured = riskState != null;
        holdPhysicalPosition = riskState != null ? riskState.user_physical_position : Vector2.zero;
        holdPhysicalForward = riskState != null && riskState.user_physical_forward.sqrMagnitude > 0.0001f
            ? riskState.user_physical_forward.normalized
            : Vector2.up;
        replanRetryCount++;
        currentReplanRetryDelaySeconds = Mathf.Min(
            Mathf.Max(0.1f, replanMaxRetryDelaySeconds),
            Mathf.Max(0.1f, replanInitialRetryDelaySeconds) *
            Mathf.Pow(2f, Mathf.Max(0, replanRetryCount - 1)));
        nextReplanAttemptTime = Time.realtimeSinceStartup + currentReplanRetryDelaySeconds;
        ReplanHoldChanged?.Invoke(CreateReplanHoldInfo(true));
        if (firstEntry)
        {
            Replanning?.Invoke(taskController != null ? taskController.RuntimeState : null);
        }
    }

    private void ExitReplanHold(string reason)
    {
        if (!holdForReplan)
        {
            return;
        }

        var completedInfo = new ReplanHoldInfo(
            false,
            reason,
            holdPlanningFingerprint,
            Math.Max(0, holdRiskRevision),
            replanRetryCount,
            currentReplanRetryDelaySeconds);
        holdForReplan = false;
        holdRiskRevision = -1;
        holdTaskStateVersion = 0;
        holdPlanningFingerprint = string.Empty;
        holdReason = string.Empty;
        replanHoldRequiresNewFacts = false;
        holdPhysicalPosition = Vector2.zero;
        holdPhysicalForward = Vector2.up;
        holdPhysicalPoseCaptured = false;
        replanRetryCount = 0;
        nextReplanAttemptTime = 0f;
        currentReplanRetryDelaySeconds = 0f;
        ReplanHoldChanged?.Invoke(completedInfo);
    }

    private ReplanHoldInfo CreateReplanHoldInfo(bool isHolding)
    {
        return new ReplanHoldInfo(
            isHolding,
            holdReason,
            holdPlanningFingerprint,
            Math.Max(0, holdRiskRevision),
            replanRetryCount,
            currentReplanRetryDelaySeconds);
    }

    private string GetReplanHoldWakeReason(PhysicalRiskState riskState)
    {
        if (!holdForReplan)
        {
            return string.Empty;
        }

        var currentTaskStateVersion = taskController != null && taskController.RuntimeState != null
            ? taskController.RuntimeState.stateVersion
            : 0;
        if (currentTaskStateVersion != holdTaskStateVersion)
        {
            return "task_state_changed";
        }

        if (riskState != null)
        {
            if (riskState.risk_revision != holdRiskRevision)
            {
                return "risk_revision_changed";
            }

            if (holdPhysicalPoseCaptured &&
                Vector2.Distance(riskState.user_physical_position, holdPhysicalPosition) >=
                Mathf.Max(0.01f, replanWakePositionMeters))
            {
                return "physical_position_changed";
            }

            var latestForward = riskState.user_physical_forward.sqrMagnitude > 0.0001f
                ? riskState.user_physical_forward.normalized
                : Vector2.up;
            if (holdPhysicalPoseCaptured &&
                Vector2.Angle(latestForward, holdPhysicalForward) >=
                Mathf.Clamp(replanWakeHeadingDegrees, 1f, 180f))
            {
                return "physical_heading_changed";
            }
        }

        return replanHoldRequiresNewFacts
            ? string.Empty
            : Time.realtimeSinceStartup >= nextReplanAttemptTime
                ? "retry_timer_elapsed"
                : string.Empty;
    }

    private string BuildPlanningFingerprint(PhysicalRiskState riskState)
    {
        var stateVersion = taskController != null && taskController.RuntimeState != null
            ? taskController.RuntimeState.stateVersion
            : 0;
        var riskRevision = riskState != null ? riskState.risk_revision : -1;
        return $"state:{stateVersion}|risk:{riskRevision}";
    }

    /// <summary>构造本地确定性拒绝指纹，用于阻止相同事实下重复请求。</summary>
    private string BuildRejectionFingerprint(GuidanceResponse response, PhysicalRiskState riskState, string reason)
    {
        var stateVersion = taskController != null && taskController.RuntimeState != null
            ? taskController.RuntimeState.stateVersion
            : 0;
        var riskRevision = riskState != null ? riskState.risk_revision : -1;
        var targetId = response != null ? response.selected_target : string.Empty;
        var normalizedReason = string.IsNullOrWhiteSpace(reason) ? string.Empty : reason.Trim();
        return string.IsNullOrWhiteSpace(targetId) || string.IsNullOrWhiteSpace(normalizedReason)
            ? string.Empty
            : $"target:{targetId}|state:{stateVersion}|risk:{riskRevision}|reason:{normalizedReason}";
    }

    private static string RecoveryFailureReason(RecoveryHeadingFailure failure)
    {
        switch (failure)
        {
            case RecoveryHeadingFailure.NoAcceptableHeading:
                return "continuous_heading_search_exhausted";
            case RecoveryHeadingFailure.FinalValidationFailed:
                return "recovery_heading_final_validation_failed";
            case RecoveryHeadingFailure.EvaluationUnavailable:
                return "recovery_heading_evaluation_unavailable";
            default:
                return "recovery_heading_search_context_invalid";
        }
    }

    private RecoveryHeadingSearchResult SearchRecoveryHeading(
        PhysicalRiskState riskState,
        bool forceRefresh = false)
    {
        var fingerprint = BuildRecoverySearchFingerprint(riskState);
        if (!forceRefresh &&
            latestRecoverySearchResult != null &&
            string.Equals(latestRecoverySearchFingerprint, fingerprint, StringComparison.Ordinal))
        {
            return latestRecoverySearchResult;
        }

        latestRecoverySearchResult = recoveryHeadingPlanner.Search(
            riskState,
            riskEvaluator,
            recoveryHeadingSearchSettings);
        latestRecoverySearchFingerprint = fingerprint;
        RecoverySearchCompleted?.Invoke(latestRecoverySearchResult);
        return latestRecoverySearchResult;
    }

    /// <summary>
    /// 在所有已搜索 Safe Heading 中优先选择 Reset 后仍可继续导航的朝向。
    /// </summary>
    private RecoveryHeadingPlan SelectPostResetRecoveryPlan(
        PhysicalRiskState riskState,
        RecoveryHeadingSearchResult searchResult,
        Vector2? excludedForward,
        out PostResetContinuationAssessment bestContinuation,
        string planningFingerprint = "")
    {
        bestContinuation = new PostResetContinuationAssessment(
            PostResetContinuationLevel.HoldOnly,
            string.Empty,
            false,
            float.NegativeInfinity,
            "no_safe_heading");
        if (riskState == null || searchResult == null || riskEvaluator == null)
        {
            return null;
        }

        var candidateSet = semanticTargetSelector != null && taskController != null
            ? semanticTargetSelector.BuildCandidateSet(
                taskController.RuntimeState,
                riskState,
                ExperimentConditionMatrix.GetPolicy(taskController.CurrentCondition),
                true)
            : null;
        var candidates = candidateSet != null ? candidateSet.Candidates : null;
        if (experimentProfile != null &&
            experimentProfile.resetMode == ExperimentResetMode.SemanticContextualized)
        {
            var semanticContext = BuildSemanticResetContext();
            candidates = OrderSemanticResetCandidates(candidates, semanticContext);
        }
        RecoveryHeadingPlan bestPlan = null;
        var samples = new List<HeadingSample>(searchResult.Evaluations ?? Array.Empty<HeadingSample>());
        if (searchResult.Plan != null && searchResult.Plan.IsSuccess)
        {
            samples.Add(new HeadingSample(
                searchResult.Plan.RelativeYawDegrees,
                searchResult.Plan.SafePhysicalForward,
                searchResult.Plan.ClearanceMeters,
                searchResult.Plan.Safety));
        }

        var seenHeadings = new HashSet<int>();
        var assessedHeadings = new List<HeadingSample>();
        var assessedContinuations = new List<PostResetContinuationAssessment>();
        foreach (var sample in samples)
        {
            var headingKey = Mathf.RoundToInt(sample.PhysicalForward.x * 10000f) * 100000 +
                             Mathf.RoundToInt(sample.PhysicalForward.y * 10000f);
            if (!seenHeadings.Add(headingKey))
            {
                continue;
            }

            if (sample.Safety != HeadingSafety.Safe)
            {
                assessedHeadings.Add(sample);
                assessedContinuations.Add(HoldContinuation(
                    "physical_heading_not_safe",
                    false));
                continue;
            }

            if (excludedForward.HasValue &&
                Vector2.Angle(sample.PhysicalForward, excludedForward.Value) < 1f)
            {
                assessedHeadings.Add(sample);
                assessedContinuations.Add(HoldContinuation(
                    "previous_recovery_heading_excluded",
                    true));
                continue;
            }

            if (!riskEvaluator.TryEvaluateAbsoluteHeading(
                    riskState,
                    sample.PhysicalForward,
                    out var validatedHeading))
            {
                assessedHeadings.Add(new HeadingSample(
                    sample.RelativeYawDegrees,
                    sample.PhysicalForward,
                    sample.ClearanceMeters,
                    HeadingSafety.Unknown));
                assessedContinuations.Add(HoldContinuation(
                    "exact_heading_evaluation_failed",
                    false));
                continue;
            }

            if (!PhysicalRiskEvaluator.IsRecoveryHeadingAcceptable(validatedHeading))
            {
                assessedHeadings.Add(validatedHeading);
                assessedContinuations.Add(HoldContinuation(
                    "physical_heading_not_acceptable",
                    false));
                continue;
            }

            var plan = new RecoveryHeadingPlan(validatedHeading, sample.ClearanceMeters);
            var continuation = postResetContinuationEvaluator.EvaluateHeading(
                riskState,
                validatedHeading.PhysicalForward,
                plan.Score,
                candidates,
                riskEvaluator,
                false);
            assessedHeadings.Add(validatedHeading);
            assessedContinuations.Add(continuation);
            if (bestPlan == null || IsBetterContinuation(plan, continuation, bestPlan, bestContinuation))
            {
                bestPlan = plan;
                bestContinuation = continuation;
            }
        }

        latestPostResetContinuation = bestContinuation;
        var headingSelections = new List<PostResetHeadingSelectionRecord>(assessedHeadings.Count);
        for (var index = 0; index < assessedHeadings.Count; index++)
        {
            var selected = bestPlan != null &&
                           Vector2.Angle(
                               assessedHeadings[index].PhysicalForward,
                               bestPlan.SafePhysicalForward) < 0.01f;
            headingSelections.Add(new PostResetHeadingSelectionRecord(
                assessedHeadings[index],
                assessedContinuations[index],
                selected));
        }
        EmitNavigationRecoveryDiagnostic(
            "recovery_heading_selection_evaluated",
            planningFingerprint,
            headingSelections);
        return bestPlan;
    }

    private SemanticResetContext BuildSemanticResetContext()
    {
        var state = taskController != null ? taskController.RuntimeState : null;
        var remaining = new List<string>();
        if (state != null && state.activePlanNodes != null)
        {
            remaining.AddRange(state.activePlanNodes);
        }
        var activeRoute = rdwPlanCache.ActivePlan;
        if (activeRoute != null)
        {
            foreach (var nodeId in activeRoute.RemainingSteps)
            {
                if (!remaining.Contains(nodeId)) remaining.Add(nodeId);
            }
        }
        return new SemanticResetContext(
            state != null ? state.finalTargetId : string.Empty,
            state != null ? state.activePlanId : string.Empty,
            activeRoute != null ? activeRoute.SelectedRouteId : string.Empty,
            state != null ? state.currentExhibitId : string.Empty,
            state != null ? state.nextTargetId : string.Empty,
            remaining);
    }

    private static IReadOnlyList<NavigationCandidate> OrderSemanticResetCandidates(
        IReadOnlyList<NavigationCandidate> candidates,
        SemanticResetContext context)
    {
        var ordered = new List<NavigationCandidate>();
        if (context != null)
        {
            foreach (var preferredId in context.RemainingPlanNodeIds)
            {
                foreach (var candidate in candidates ?? Array.Empty<NavigationCandidate>())
                {
                    if (candidate != null &&
                        string.Equals(candidate.TargetId, preferredId, StringComparison.Ordinal) &&
                        !ordered.Contains(candidate))
                    {
                        ordered.Add(candidate);
                    }
                }
            }
        }
        if (context != null &&
            !string.IsNullOrWhiteSpace(context.FinalGoalId) &&
            string.Equals(context.NextSemanticTargetId, context.FinalGoalId, StringComparison.Ordinal))
        {
            foreach (var candidate in candidates ?? Array.Empty<NavigationCandidate>())
            {
                if (candidate != null &&
                    string.Equals(candidate.TargetId, context.FinalGoalId, StringComparison.Ordinal) &&
                    !ordered.Contains(candidate))
                {
                    ordered.Add(candidate);
                }
            }
        }
        foreach (var candidate in candidates ?? Array.Empty<NavigationCandidate>())
        {
            if (candidate != null && !ordered.Contains(candidate)) ordered.Add(candidate);
        }
        return ordered.AsReadOnly();
    }

    private static PostResetContinuationAssessment HoldContinuation(
        string reason,
        bool physicalSafe)
    {
        return new PostResetContinuationAssessment(
            PostResetContinuationLevel.HoldOnly,
            string.Empty,
            physicalSafe,
            float.NegativeInfinity,
            reason);
    }

    private static bool IsBetterContinuation(
        RecoveryHeadingPlan plan,
        PostResetContinuationAssessment continuation,
        RecoveryHeadingPlan currentPlan,
        PostResetContinuationAssessment currentContinuation)
    {
        if (continuation.Level != currentContinuation.Level)
        {
            return continuation.Level > currentContinuation.Level;
        }

        if (!Mathf.Approximately(continuation.Score, currentContinuation.Score))
        {
            return continuation.Score > currentContinuation.Score;
        }

        if (!Mathf.Approximately(plan.ClearanceMeters, currentPlan.ClearanceMeters))
        {
            return plan.ClearanceMeters > currentPlan.ClearanceMeters;
        }

        return plan.PhysicalTurnDegrees < currentPlan.PhysicalTurnDegrees;
    }

    private static string BuildRecoverySearchFingerprint(PhysicalRiskState riskState)
    {
        if (riskState == null)
        {
            return "risk:none";
        }

        return string.Join(
            "|",
            riskState.physical_space,
            riskState.calibration_id,
            riskState.risk_revision,
            riskState.user_physical_position.x.ToString("0.####"),
            riskState.user_physical_position.y.ToString("0.####"),
            riskState.user_physical_forward.x.ToString("0.####"),
            riskState.user_physical_forward.y.ToString("0.####"));
    }

    private bool TryEvaluateFrozenRecoveryHeading(
        PhysicalRiskState riskState,
        out HeadingSample heading)
    {
        heading = default;
        return riskEvaluator != null &&
               frozenRecoveryPlan != null &&
               frozenRecoveryPlan.IsSuccess &&
               riskEvaluator.TryEvaluateAbsoluteHeading(
                   riskState,
                   frozenRecoveryPlan.SafePhysicalForward,
                   out heading);
    }

    private bool TryEvaluateRecoveryPlanHeading(
        PhysicalRiskState riskState,
        RecoveryHeadingPlan plan,
        out HeadingSample heading)
    {
        heading = default;
        return riskEvaluator != null &&
               plan != null &&
               plan.IsSuccess &&
               riskEvaluator.TryEvaluateAbsoluteHeading(
                   riskState,
                   plan.SafePhysicalForward,
                   out heading);
    }

    private bool TryValidateSelectedTargetExactHeading(
        string targetId,
        PhysicalRiskState riskState,
        out string reason)
    {
        reason = string.Empty;
        if (!TryEvaluateTargetExactHeading(targetId, riskState, out var heading))
        {
            reason = "Selected target exact physical heading could not be evaluated from current facts.";
            return false;
        }

        if (!PhysicalRiskEvaluator.IsRecoveryHeadingAcceptable(heading))
        {
            reason = $"Selected target exact physical heading is not Safe: {heading.Safety}.";
            return false;
        }

        return true;
    }

    private bool TryValidateExecutablePhysicalAction(
        GuidanceResponse response,
        PhysicalRiskState riskState,
        out string physicalDirection,
        out float clearanceMeters,
        out string reason)
    {
        physicalDirection = string.Empty;
        clearanceMeters = 0f;
        reason = string.Empty;

        if (response == null || navigationTargets == null ||
            !navigationTargets.TryGetTargetBearing(response.selected_target, out var bearing))
        {
            reason = "Guidance Physical Action Gate could not resolve the selected target direction.";
            return false;
        }
        // DisplayLabel 仅是风险摘要；动作已由最新连续 Bearing 生成，不再与 coarse label 做等值比较。
        physicalDirection = $"relative_yaw:{bearing.RelativeYawDegrees:0.###}";

        if (!TryEvaluateTargetExactHeading(response.selected_target, riskState, out var heading))
        {
            reason = "Guidance Physical Action Gate could not query the exact target direction.";
            return false;
        }
        clearanceMeters = heading.ClearanceMeters;

        if (!PhysicalRiskEvaluator.IsMovementSafe(heading))
        {
            reason =
                $"Guidance Physical Action Gate rejected exact target direction because its safety is {heading.Safety}.";
            return false;
        }

        return true;
    }

    private bool TryRouteUnavailablePlannedDirection(
        RuntimeGuidanceSafetyInput input,
        GuidanceTarget safeCandidate,
        PhysicalRiskState riskState)
    {
        if (input == null || input.PlannedDirectionAvailable)
        {
            return false;
        }

        if (input.LatestRisk == null ||
            !input.TrackingValid ||
            !input.CalibrationValid ||
            !input.PhysicalSpaceMatches ||
            !IsUserInsidePhysicalBoundary(riskState))
        {
            return false;
        }

        if (enableSoftRerouteSafetyPolicy && safeCandidate != null)
        {
            TriggerSoftReroute(
                safeCandidate,
                "Planned physical direction became unavailable; rerouting to a safe semantic candidate.");
            return true;
        }

        // SoftReroute 关闭或没有安全候选时，优先进入可信的物理朝向恢复。
        if (HasTrustedRecoveryFacts(riskState))
        {
            TriggerPauseReset(
                activeResponse,
                riskState,
                "Planned physical direction became unavailable; starting trusted heading recovery.");
            return true;
        }

        return false;
    }

    private bool TryEvaluateTargetExactHeading(
        string targetId,
        PhysicalRiskState riskState,
        out HeadingSample heading)
    {
        heading = default;
        if (riskEvaluator == null || navigationTargets == null || riskState == null ||
            !riskEvaluator.IsRiskIdentityCurrent(riskState) ||
            !navigationTargets.TryGetTargetRelativeYawDegrees(targetId, out var relativeYawDegrees))
        {
            return false;
        }

        var physicalDirection = RotatePhysicalForward(
            riskState.user_physical_forward,
            relativeYawDegrees);
        return riskEvaluator.TryEvaluateAbsoluteHeading(riskState, physicalDirection, out heading);
    }

    private static Vector2 RotatePhysicalForward(Vector2 forward, float yawDegrees)
    {
        forward = NormalizePhysicalForward(forward);
        var radians = yawDegrees * Mathf.Deg2Rad;
        var sin = Mathf.Sin(radians);
        var cos = Mathf.Cos(radians);
        return new Vector2(
            forward.x * cos + forward.y * sin,
            -forward.x * sin + forward.y * cos).normalized;
    }

    private static Vector2 NormalizePhysicalForward(Vector2 forward)
    {
        return forward.sqrMagnitude > 0.0001f ? forward.normalized : Vector2.up;
    }

    private void TransitionRecoveryTo(GuidanceRecoveryState nextState, string reason = "")
    {
        if (recoveryState == nextState)
        {
            return;
        }

        var previousState = recoveryState;
        recoveryState = nextState;
        RecoveryStateChanged?.Invoke(recoveryState);
        RecoveryTransitioned?.Invoke(new GuidanceRecoveryTransitionInfo(
            previousState,
            recoveryState,
            reason,
            frozenRecoveryPlan));
    }

    private static bool IsUserInsidePhysicalBoundary(PhysicalRiskState riskState)
    {
        if (riskState == null || riskState.boundary_context == null || riskState.boundary_context.Count < 3)
        {
            return false;
        }

        var boundary = new Vector2[riskState.boundary_context.Count];
        for (var index = 0; index < boundary.Length; index++)
        {
            boundary[index] = riskState.boundary_context[index];
        }

        return PhysicalSpaceGeometry.ContainsPoint(boundary, riskState.user_physical_position);
    }

    private void UpdateUnsafeSampleCount(
        PhysicalRiskState riskState,
        string directionIdentity,
        bool directionAvailable,
        HeadingSafety safety)
    {
        if (!directionAvailable ||
            (safety != HeadingSafety.Unsafe && safety != HeadingSafety.Critical))
        {
            ResetRuntimeSafetySampling();
            return;
        }

        if (riskState != null &&
            !string.IsNullOrWhiteSpace(riskState.risk_snapshot_id) &&
            string.Equals(lastRuntimeRiskSnapshotId, riskState.risk_snapshot_id, StringComparison.Ordinal))
        {
            return;
        }

        lastRuntimeRiskSnapshotId = riskState != null ? riskState.risk_snapshot_id : string.Empty;

        if (!string.Equals(sampledUnsafeTargetId, directionIdentity, StringComparison.Ordinal))
        {
            sampledUnsafeTargetId = directionIdentity;
            consecutiveUnsafeSamples = 1;
            return;
        }

        consecutiveUnsafeSamples++;
    }

    private void ResetRuntimeSafetySampling()
    {
        consecutiveUnsafeSamples = 0;
        sampledUnsafeTargetId = string.Empty;
        lastRuntimeRiskSnapshotId = string.Empty;
    }

    private bool RequiresRuntimeSafetyIntervention(
        bool directionAvailable,
        HeadingSafety safety,
        bool plannedDirectionCritical)
    {
        if (!directionAvailable)
        {
            return true;
        }

        if (plannedDirectionCritical)
        {
            return true;
        }

        return safety == HeadingSafety.Unsafe &&
               consecutiveUnsafeSamples >= 2;
    }

    private static bool Contains(System.Collections.Generic.IReadOnlyList<string> values, string expected)
    {
        if (values == null || string.IsNullOrWhiteSpace(expected))
        {
            return false;
        }

        foreach (var value in values)
        {
            if (string.Equals(value, expected, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string RuntimeSafetyReason(RuntimeGuidanceSafetyInput input)
    {
        if (input == null || input.LatestRisk == null) return "Runtime risk snapshot is unavailable.";
        if (!input.TrackingValid) return "XR tracking is invalid.";
        if (!input.CalibrationValid) return "Physical calibration is invalid.";
        if (!input.PhysicalSpaceMatches) return "Physical space or risk identity does not match.";
        if (!input.PlannedDirectionAvailable) return "Planned physical direction clearance is unavailable.";
        if (input.ResetState == ResetLifecycleState.Required ||
            input.ResetState == ResetLifecycleState.TurningToSafeHeading ||
            input.ResetState == ResetLifecycleState.Completed) return "Physical reset is active.";
        if (!input.HasRecoveryHeading) return "No acceptable physical recovery heading is available.";
        if (input.LatestRisk.reset_required) return "The risk protocol reports no acceptable automatic recovery state.";
        if (input.PlannedDirectionCritical) return "Planned physical direction is critical and no eligible safety candidate is available.";
        return "No eligible runtime safety candidate is available.";
    }

    private void BindRequestCapture()
    {
        if (agentClient != null)
        {
            // 先解除再订阅，避免 Configure 与 OnEnable 交错时重复捕获同一次请求。
            agentClient.GuidanceRequestBuilt -= HandleRequestBuilt;
            agentClient.GuidanceRequestBuilt += HandleRequestBuilt;
            agentClient.GuidanceRequestFailed.RemoveListener(HandleRequestFailed);
            agentClient.GuidanceRequestFailed.AddListener(HandleRequestFailed);
        }
    }

    private void UnbindRequestCapture()
    {
        if (agentClient != null)
        {
            agentClient.GuidanceRequestBuilt -= HandleRequestBuilt;
            agentClient.GuidanceRequestFailed.RemoveListener(HandleRequestFailed);
        }
    }

    private void HandleRequestFailed(string _)
    {
        arrivalDetector?.WatchReservation(null);
        AbandonReservations();
        activeSnapshot = null;
        activeResponse = null;
        turnCueCompletionTracker.Cancel();
        ResetRuntimeSafetySampling();
        SetRequestPhase(GuidanceRequestPhase.Failed);
    }

    private void AbandonReservations()
    {
        taskController?.AbandonCandidateSetReservation();
        taskController?.AbandonReservation();
    }

    private void BindArrival()
    {
        if (arrivalDetector != null)
        {
            // 到达事件会推进任务状态，重复订阅将导致一次到达跨越多个路线节点。
            arrivalDetector.TargetArrived -= HandleTargetArrived;
            arrivalDetector.TargetArrived += HandleTargetArrived;
        }
    }

    private void UnbindArrival()
    {
        if (arrivalDetector != null)
        {
            arrivalDetector.TargetArrived -= HandleTargetArrived;
        }
    }

    private void ConfigureTurnCueTracker()
    {
        turnCueCompletionTracker.Configure(
            turnCueNoCueToleranceDegrees,
            turnCueReverseAbortDegrees,
            turnCueStableCompletionSeconds,
            turnCueTimeoutSeconds);
    }

    private void BindMotionDetector()
    {
        if (motionDetector == null || !isActiveAndEnabled)
        {
            return;
        }

        motionDetector.TurnSampled -= HandleTurnSampled;
        motionDetector.TurnSampled += HandleTurnSampled;
    }

    private void UnbindMotionDetector()
    {
        if (motionDetector != null)
        {
            motionDetector.TurnSampled -= HandleTurnSampled;
        }
    }

    private void BeginTurnCueTracking(GuidanceResponse response)
    {
        if (response == null || navigationTargets == null ||
            !navigationTargets.TryGetTargetRelativeYawDegrees(response.selected_target, out var relativeYawDegrees))
        {
            TriggerRuntimeIntervention(response, "TurnCue target relative yaw is unavailable.");
            return;
        }

        var directionSign = response.movement_instruction == "turn_left_slowly" ||
                            response.movement_instruction == "look_left"
            ? 1
            : -1;
        var result = turnCueCompletionTracker.Begin(directionSign, Mathf.Abs(relativeYawDegrees));
        if (result == TurnCueTrackingResult.Completed)
        {
            CompleteTurnCueAfterRiskReevaluation();
            return;
        }

        rotationGainController?.QueueFromMovementInstruction(
            response.movement_instruction,
            activeSnapshot != null ? activeSnapshot.decisionId : string.Empty,
            turnCueCompletionTracker.RequiredTurnYawDegrees);
    }

    private void HandleTurnSampled(bool isTurning, float signedYawDeltaDegrees, float deltaTimeSeconds)
    {
        if (requestPhase != GuidanceRequestPhase.Executing || !turnCueCompletionTracker.IsTracking)
        {
            return;
        }

        var result = turnCueCompletionTracker.ProcessSample(
            isTurning,
            signedYawDeltaDegrees,
            deltaTimeSeconds);
        if (result == TurnCueTrackingResult.Completed)
        {
            if (rotationGainController == null || !rotationGainController.IsActive)
            {
                CompleteTurnCueAfterRiskReevaluation();
            }
        }
        else if (result == TurnCueTrackingResult.Reversed)
        {
            HandleTurnCueSoftFailure("turn_cue_reverse");
        }
        else if (result == TurnCueTrackingResult.TimedOut)
        {
            HandleTurnCueSoftFailure("turn_cue_timeout");
        }
    }

    private void HandleTurnCueSoftFailure(string reason)
    {
        if (requestPhase != GuidanceRequestPhase.Executing)
        {
            return;
        }

        handlingGainTermination = true;
        rotationGainController?.AbortGain(reason);
        handlingGainTermination = false;
        turnCueCompletionTracker.Cancel();
        TriggerSoftRerouteFromLatestRisk(reason);
    }

    private void TriggerSoftRerouteFromLatestRisk(string reason)
    {
        if (IsResetRecoveryActive)
        {
            return;
        }

        var riskState = riskEvaluator != null ? riskEvaluator.CurrentRiskState : null;
        if (riskState == null || riskEvaluator == null || !riskEvaluator.IsRiskIdentityCurrent(riskState))
        {
            TriggerRuntimeIntervention(activeResponse, $"{reason}: current risk snapshot is unavailable or stale.");
            return;
        }

        var activeTargetId = activeResponse != null ? activeResponse.selected_target : string.Empty;
        var safeCandidate = FindRuntimeSafetyCandidate(riskState, activeTargetId);
        if (safeCandidate != null && enableSoftRerouteSafetyPolicy)
        {
            TriggerSoftReroute(safeCandidate, reason, SemanticReplanReason.UserDeviated);
            return;
        }

        if (safeCandidate != null && HasTrustedRecoveryFacts(riskState))
        {
            TriggerPauseReset(
                activeResponse,
                riskState,
                $"{reason}: SoftReroute is disabled; starting trusted heading recovery.");
            return;
        }

        // 动作软失败和候选耗尽都不是物理危险证据；保持任务运行并等待新事实重规划。
        EnterHoldAndReplan(
            riskState,
            BuildPlanningFingerprint(riskState),
            $"{reason}: no eligible reroute candidate; holding for replanning.");
    }

    private void CompleteTurnCueAfterRiskReevaluation()
    {
        if (requestPhase != GuidanceRequestPhase.Executing)
        {
            return;
        }

        var riskState = riskEvaluator != null ? riskEvaluator.EvaluateNow() : null;
        if (IsResetRecoveryActive || requestPhase != GuidanceRequestPhase.Executing)
        {
            return;
        }

        var resetRequired = riskState != null && riskState.reset_required;
        var currentHeadingCritical = IsCurrentHeadingCritical(riskState);
        var outsideBoundary = riskState != null && !IsUserInsidePhysicalBoundary(riskState);
        if (riskState == null ||
            riskEvaluator == null ||
            !riskEvaluator.IsRiskIdentityCurrent(riskState) ||
            resetRequired ||
            currentHeadingCritical ||
            outsideBoundary)
        {
            if (HasTrustedRecoveryFacts(riskState))
            {
                TriggerPauseReset(activeResponse, riskState, "TurnCue reevaluation is unsafe.");
            }
            else
            {
                TriggerRuntimeIntervention(
                    activeResponse,
                    riskState == null ? "TurnCue reevaluation has no valid risk snapshot." : "TurnCue reevaluation is unsafe.");
            }
            return;
        }

        HandleTurnCueCompleted();
    }

    private void BindRotationGain()
    {
        if (rotationGainController != null)
        {
            // 完成物理转向后必须先重算风险，不能由 Guidance 展示直接推进 TurnCue。
            rotationGainController.ReevaluationRequested -= HandleGainReevaluationRequested;
            rotationGainController.ReevaluationRequested += HandleGainReevaluationRequested;
            rotationGainController.LifecycleEventOccurred -= HandleRotationGainLifecycleEvent;
            rotationGainController.LifecycleEventOccurred += HandleRotationGainLifecycleEvent;
        }
    }

    private void UnbindRotationGain()
    {
        if (rotationGainController != null)
        {
            rotationGainController.ReevaluationRequested -= HandleGainReevaluationRequested;
            rotationGainController.LifecycleEventOccurred -= HandleRotationGainLifecycleEvent;
        }
    }

    private void HandleRotationGainLifecycleEvent(RotationGainEventInfo info)
    {
        if (IsResetRecoveryActive || handlingGainTermination || info == null ||
            info.EventType != RotationGainEventType.GainCancelled ||
            requestPhase != GuidanceRequestPhase.Executing)
        {
            return;
        }

        if (activeSnapshot == null ||
            !string.Equals(info.DecisionId, activeSnapshot.decisionId, StringComparison.Ordinal))
        {
            return;
        }

        if (info.Reason == "reverse_turn" ||
            info.Reason == "turn_cue_reverse" ||
            info.Reason == "turn_cue_timeout" ||
            info.Reason == "turn_stopped_timeout")
        {
            turnCueCompletionTracker.Cancel();
            TriggerSoftRerouteFromLatestRisk(info.Reason);
        }
        else if (info.Reason == "tracking_invalid" ||
                  info.Reason == "planned_heading_critical" ||
                  info.Reason == "reset_required" ||
                  info.Reason == "outside_physical_boundary" ||
                  info.Reason == "rotation_root_invalid")
        {
            turnCueCompletionTracker.Cancel();
            if ((info.Reason == "planned_heading_critical" || info.Reason == "reset_required") &&
                riskEvaluator != null &&
                HasTrustedRecoveryFacts(riskEvaluator.CurrentRiskState))
            {
                TriggerPauseReset(activeResponse, riskEvaluator.CurrentRiskState, info.Reason);
            }
            else
            {
                TriggerRuntimeIntervention(activeResponse, info.Reason);
            }
        }
    }

    private void HandleGainReevaluationRequested()
    {
        if (IsResetRecoveryActive)
        {
            return;
        }

        var riskState = riskEvaluator != null ? riskEvaluator.EvaluateNow() : null;
        if (IsResetRecoveryActive || requestPhase != GuidanceRequestPhase.Executing)
        {
            return;
        }

        var riskUnavailable = riskState == null ||
                              riskEvaluator == null ||
                              !riskEvaluator.IsRiskIdentityCurrent(riskState);
        var resetRequired = riskState != null && riskState.reset_required;
        var currentHeadingCritical = IsCurrentHeadingCritical(riskState);
        var outsideBoundary = riskState != null && !IsUserInsidePhysicalBoundary(riskState);
        if (riskUnavailable || resetRequired || currentHeadingCritical || outsideBoundary)
        {
            var reason = riskUnavailable
                ? "risk_snapshot_unavailable"
                : resetRequired
                    ? "reset_required"
                    : outsideBoundary ? "outside_physical_boundary" : "planned_heading_critical";
            handlingGainTermination = true;
            rotationGainController?.CompleteReevaluation(false, reason);
            handlingGainTermination = false;
            turnCueCompletionTracker.Cancel();
            if (!riskUnavailable && HasTrustedRecoveryFacts(riskState))
            {
                TriggerPauseReset(activeResponse, riskState, reason);
            }
            else
            {
                TriggerRuntimeIntervention(activeResponse, reason);
            }
            return;
        }

        rotationGainController?.CompleteReevaluation(true);
        if (CurrentGuidanceTarget != null &&
            CurrentGuidanceTarget.TargetRole == NavigationTargetRole.TurnCueAnchor &&
            turnCueCompletionTracker.State == TurnCueTrackingState.Completed)
        {
            CompleteTurnCueAfterRiskReevaluation();
        }
    }

    private bool IsCurrentHeadingCritical(PhysicalRiskState riskState)
    {
        return riskEvaluator != null && riskState != null &&
               riskEvaluator.TryEvaluateAbsoluteHeading(
                   riskState,
                   riskState.user_physical_forward,
                   out var currentHeading) &&
               currentHeading.Safety == HeadingSafety.Critical;
    }
}
