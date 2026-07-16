using System;
using UnityEngine;

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
    [SerializeField] private RejectionPanel rejectionPanel;

    private GuidanceRequestSnapshot activeSnapshot;
    private IGuidanceRequestClient requestClient;

    public event Action<string> GuidanceRejected;
    /// <summary>到达中间路线目标并准备请求下一段 Guidance 时触发。</summary>
    public event Action<GuidanceRuntimeState> Replanning;
    /// <summary>到达最终目标、任务进入 Completed 时触发。</summary>
    public event Action<GuidanceRuntimeState> TaskCompleted;

    /// <summary>
    /// 当前任务运行状态；任务控制器未配置时返回 null。
    /// </summary>
    public GuidanceRuntimeState RuntimeState => taskController != null ? taskController.RuntimeState : null;

    private void OnEnable()
    {
        if (requestClient == null)
        {
            requestClient = agentClient;
        }
        BindRequestCapture();
        BindArrival();
    }

    private void OnDisable()
    {
        UnbindRequestCapture();
        UnbindArrival();
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
        RejectionPanel rejection)
    {
        UnbindRequestCapture();
        UnbindArrival();

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
        if (rejection != null) rejectionPanel = rejection;

        BindRequestCapture();
        BindArrival();
    }

    /// <summary>
    /// 替换请求传输实现；正式运行使用 AgentClient，流程测试可使用内存客户端。
    /// </summary>
    public void ConfigureRequestClient(IGuidanceRequestClient client)
    {
        requestClient = client;
    }

    /// <summary>
    /// 根据最新物理风险选择安全语义目标并发起 guidance 请求；无可用目标时进入暂停重置流程。
    /// </summary>
    public void RequestNextGuidance()
    {
        if (requestClient == null)
        {
            requestClient = agentClient;
        }

        if (requestClient == null || taskController == null || !taskController.RuntimeState.IsNavigating ||
            requestClient.RequestInFlight)
        {
            return;
        }

        var riskState = riskEvaluator != null ? riskEvaluator.EvaluateNow() : null;
        var selectedTarget = semanticTargetSelector != null
            ? semanticTargetSelector.SelectNextTarget(taskController.RuntimeState, riskState)
            : taskController.RuntimeState.nextTargetId;

        if (string.IsNullOrWhiteSpace(selectedTarget))
        {
            pauseResetController?.PauseForReset("No safe semantic navigation candidate is available.");
            taskController.PauseForReset();
            return;
        }

        arrivalDetector?.WatchTarget(selectedTarget);
        requestClient.RequestGuidance(taskController.BuildRequestContext(selectedTarget));
    }

    /// <summary>
    /// 对后端响应执行本地时效与安全校验，通过后更新导览表现、到达目标和旋转增益。
    /// </summary>
    /// <param name="response">后端返回的 guidance；null 会被本地校验拒绝。</param>
    public void HandleGuidanceResponse(GuidanceResponse response)
    {
        var runtimeState = taskController != null ? taskController.RuntimeState : null;
        var riskState = riskEvaluator != null ? riskEvaluator.CurrentRiskState : null;

        if (runtimeState == null || !runtimeState.IsNavigating)
        {
            guidanceExecutor?.Clear();
            return;
        }

        if (response == null || response.verifier == null || !response.verifier.approved)
        {
            guidanceExecutor?.Apply(response);
            var verifierReason = response == null
                ? "Guidance response is null."
                : response.verifier == null
                    ? "Guidance verifier result is missing."
                    : response.verifier.reason;
            GuidanceRejected?.Invoke(string.IsNullOrWhiteSpace(verifierReason) ? "Guidance rejected by verifier." : verifierReason);
            return;
        }

        // 请求发出后任务或物理风险可能已变化，旧响应必须在任何表现执行前被拒绝。
        if (localValidator != null &&
            !localValidator.Validate(response, activeSnapshot, runtimeState, riskState, out var reason))
        {
            guidanceExecutor?.Clear();
            rejectionPanel?.Show(reason);
            if (riskState != null && riskState.reset_required)
            {
                pauseResetController?.PauseForReset(reason);
                taskController?.PauseForReset();
            }

            GuidanceRejected?.Invoke(reason);

            return;
        }

        guidanceExecutor?.Apply(response);
        if (response != null && !string.IsNullOrWhiteSpace(response.selected_target))
        {
            arrivalDetector?.WatchTarget(response.selected_target);
        }

        rotationGainController?.QueueFromMovementInstruction(response != null ? response.movement_instruction : string.Empty);
    }

    private void HandleRequestBuilt(GuidanceRequest request)
    {
        activeSnapshot = GuidanceRequestSnapshot.Capture(
            request,
            riskEvaluator != null ? riskEvaluator.CurrentRiskState : null,
            taskController != null ? taskController.RuntimeState : null);
    }

    /// <summary>
    /// 处理当前路线目标到达：取消旧执行、推进任务，并在未完成时自动重新规划下一段 Guidance。
    /// </summary>
    public void HandleTargetArrived(string targetId)
    {
        var state = taskController != null ? taskController.RuntimeState : null;
        if (state == null || !state.IsNavigating ||
            !string.Equals(targetId, state.nextTargetId, StringComparison.Ordinal))
        {
            return;
        }

        requestClient?.CancelPendingRequest();
        guidanceExecutor?.Clear();
        rotationGainController?.StopGain();
        activeSnapshot = null;

        var previousVersion = state.stateVersion;
        taskController.HandleArrived(targetId);
        if (state.stateVersion == previousVersion)
        {
            return;
        }

        if (state.IsCompleted)
        {
            arrivalDetector?.WatchTarget(null);
            TaskCompleted?.Invoke(state);
            return;
        }

        Replanning?.Invoke(state);
        RequestNextGuidance();
    }

    private void BindRequestCapture()
    {
        if (agentClient != null)
        {
            // 先解除再订阅，避免 Configure 与 OnEnable 交错时重复捕获同一次请求。
            agentClient.GuidanceRequestBuilt -= HandleRequestBuilt;
            agentClient.GuidanceRequestBuilt += HandleRequestBuilt;
        }
    }

    private void UnbindRequestCapture()
    {
        if (agentClient != null)
        {
            agentClient.GuidanceRequestBuilt -= HandleRequestBuilt;
        }
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
}
