using UnityEngine;

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

    public GuidanceRuntimeState RuntimeState => taskController != null ? taskController.RuntimeState : null;

    private void OnEnable()
    {
        BindRequestCapture();
        BindArrival();
    }

    private void OnDisable()
    {
        UnbindRequestCapture();
        UnbindArrival();
    }

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

        agentClient = client;
        guidanceExecutor = executor;
        taskController = task;
        navigationTargets = targetManager;
        semanticTargetSelector = selector;
        arrivalDetector = detector;
        riskEvaluator = evaluator;
        localValidator = validator;
        pauseResetController = resetController;
        rotationGainController = gainController;
        rejectionPanel = rejection;

        BindRequestCapture();
        BindArrival();
    }

    public void RequestNextGuidance()
    {
        if (agentClient == null || taskController == null)
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
        agentClient.RequestGuidance(taskController.BuildRequestContext(selectedTarget));
    }

    public void HandleGuidanceResponse(GuidanceResponse response)
    {
        var runtimeState = taskController != null ? taskController.RuntimeState : null;
        var riskState = riskEvaluator != null ? riskEvaluator.CurrentRiskState : null;

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

    private void HandleTargetArrived(string targetId)
    {
        taskController?.HandleArrived(targetId);
    }

    private void BindRequestCapture()
    {
        if (agentClient != null)
        {
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
