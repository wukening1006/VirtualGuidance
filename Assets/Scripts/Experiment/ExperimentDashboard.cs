using TMPro;
using UnityEngine;

public sealed class ExperimentDashboard : MonoBehaviour
{
    [SerializeField] private string participantId = "P001";
    [SerializeField] private int conditionIndex = 5;
    [SerializeField] private int taskIndex = 1;
    [SerializeField] private int physicalSpaceIndex = 1;
    [SerializeField] private bool requestGuidanceOnApply;

    [Header("Dependencies")]
    [SerializeField] private PhysicalSpaceManager physicalSpaceManager;
    [SerializeField] private UserPoseTracker userPoseTracker;
    [SerializeField] private PhysicalRiskEvaluator riskEvaluator;
    [SerializeField] private GuidanceTaskController taskController;
    [SerializeField] private GuidanceCoordinator guidanceCoordinator;
    [SerializeField] private AgentClient agentClient;
    [SerializeField] private ExperimentLogger experimentLogger;
    [SerializeField] private TMP_Text statusText;

    private ExperimentConditionSpec currentSpec;

    private void Awake()
    {
        ResolveReferences();
    }

    private void OnEnable()
    {
        Subscribe();
    }

    private void OnDisable()
    {
        Unsubscribe();
    }

    public void Configure(
        PhysicalSpaceManager spaceManager,
        UserPoseTracker poseTracker,
        PhysicalRiskEvaluator evaluator,
        GuidanceTaskController task,
        GuidanceCoordinator coordinator,
        AgentClient client,
        ExperimentLogger logger,
        TMP_Text status)
    {
        Unsubscribe();
        physicalSpaceManager = spaceManager;
        userPoseTracker = poseTracker;
        riskEvaluator = evaluator;
        taskController = task;
        guidanceCoordinator = coordinator;
        agentClient = client;
        experimentLogger = logger;
        statusText = status;
        Subscribe();
        ApplySelection();
    }

    public void ApplySelection()
    {
        ResolveReferences();
        currentSpec = ExperimentConditionMatrix.Build(conditionIndex, taskIndex, physicalSpaceIndex, participantId);
        experimentLogger?.StartSession(currentSpec);
        experimentLogger?.LogConditionSelected(currentSpec);
        physicalSpaceManager?.SetPhysicalSpace(currentSpec.physicalSpaceId);
        userPoseTracker?.SetSimulatedPhysicalPose(currentSpec.simulatedPhysicalPosition, currentSpec.simulatedPhysicalYawDegrees);
        taskController?.ConfigureTask(currentSpec.ToTaskDefinition());
        riskEvaluator?.EvaluateNow();
        WriteStatus();

        if (requestGuidanceOnApply)
        {
            RequestGuidance();
        }
    }

    public void RequestGuidance()
    {
        if (guidanceCoordinator != null)
        {
            guidanceCoordinator.RequestNextGuidance();
            return;
        }

        if (agentClient != null && currentSpec != null)
        {
            agentClient.RequestGuidance(BuildFallbackContext(currentSpec));
        }
    }

    public void NextCondition()
    {
        conditionIndex++;
        ApplySelection();
    }

    public void NextTask()
    {
        taskIndex++;
        ApplySelection();
    }

    public void NextPhysicalSpace()
    {
        physicalSpaceIndex++;
        ApplySelection();
    }

    private GuidanceRequestContext BuildFallbackContext(ExperimentConditionSpec spec)
    {
        return new GuidanceRequestContext
        {
            condition = spec.condition,
            task_id = spec.taskId,
            current_exhibit = spec.startExhibitId,
            semantic_next_target = spec.semanticNextTargetId,
            participant_id = spec.participantId,
            user_task = spec.userTask
        };
    }

    private void Subscribe()
    {
        if (agentClient != null)
        {
            agentClient.GuidanceRequestBuilt -= HandleRequestBuilt;
            agentClient.GuidanceRequestBuilt += HandleRequestBuilt;
            agentClient.GuidanceReceived.RemoveListener(HandleResponseReceived);
            agentClient.GuidanceReceived.AddListener(HandleResponseReceived);
            agentClient.GuidanceRequestFailed.RemoveListener(HandleRequestFailed);
            agentClient.GuidanceRequestFailed.AddListener(HandleRequestFailed);
        }

        if (taskController != null)
        {
            taskController.StateChanged -= HandleTaskStateChanged;
            taskController.StateChanged += HandleTaskStateChanged;
        }

        if (riskEvaluator != null)
        {
            riskEvaluator.RiskStateChanged -= HandleRiskStateChanged;
            riskEvaluator.RiskStateChanged += HandleRiskStateChanged;
        }
    }

    private void Unsubscribe()
    {
        if (agentClient != null)
        {
            agentClient.GuidanceRequestBuilt -= HandleRequestBuilt;
            agentClient.GuidanceReceived.RemoveListener(HandleResponseReceived);
            agentClient.GuidanceRequestFailed.RemoveListener(HandleRequestFailed);
        }

        if (taskController != null)
        {
            taskController.StateChanged -= HandleTaskStateChanged;
        }

        if (riskEvaluator != null)
        {
            riskEvaluator.RiskStateChanged -= HandleRiskStateChanged;
        }
    }

    private void HandleRequestBuilt(GuidanceRequest request)
    {
        experimentLogger?.LogRequest(request, riskEvaluator != null ? riskEvaluator.CurrentRiskState : null);
    }

    private void HandleResponseReceived(GuidanceResponse response)
    {
        experimentLogger?.LogResponse(response, riskEvaluator != null ? riskEvaluator.CurrentRiskState : null);
    }

    private void HandleRequestFailed(string message)
    {
        experimentLogger?.LogStatus("request_failed", taskController != null ? taskController.RuntimeState : null, riskEvaluator != null ? riskEvaluator.CurrentRiskState : null, message);
    }

    private void HandleTaskStateChanged(GuidanceRuntimeState state)
    {
        experimentLogger?.LogStatus("task_state_changed", state, riskEvaluator != null ? riskEvaluator.CurrentRiskState : null, "task state changed");
        WriteStatus();
    }

    private void HandleRiskStateChanged(PhysicalRiskState state)
    {
        experimentLogger?.LogStatus("risk_state_changed", taskController != null ? taskController.RuntimeState : null, state, "risk state changed");
    }

    private void WriteStatus()
    {
        if (statusText == null || currentSpec == null)
        {
            return;
        }

        statusText.text =
            $"Participant: {currentSpec.participantId}\n" +
            $"Condition: {currentSpec.condition}\n" +
            $"Task: {currentSpec.taskId}\n" +
            $"Physical Space: {currentSpec.physicalSpaceId}\n" +
            $"Matrix: {ExperimentConditionMatrix.TotalCombinations} combinations";
    }

    private void ResolveReferences()
    {
        if (physicalSpaceManager == null)
        {
            physicalSpaceManager = FindObjectOfType<PhysicalSpaceManager>();
        }

        if (userPoseTracker == null)
        {
            userPoseTracker = FindObjectOfType<UserPoseTracker>();
        }

        if (riskEvaluator == null)
        {
            riskEvaluator = FindObjectOfType<PhysicalRiskEvaluator>();
        }

        if (taskController == null)
        {
            taskController = FindObjectOfType<GuidanceTaskController>();
        }

        if (guidanceCoordinator == null)
        {
            guidanceCoordinator = FindObjectOfType<GuidanceCoordinator>();
        }

        if (agentClient == null)
        {
            agentClient = FindObjectOfType<AgentClient>();
        }

        if (experimentLogger == null)
        {
            experimentLogger = FindObjectOfType<ExperimentLogger>();
        }
    }
}
