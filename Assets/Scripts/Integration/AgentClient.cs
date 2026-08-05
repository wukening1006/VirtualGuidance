using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.Networking;

/// <summary>
/// 定义 GuidanceCoordinator 使用的最小请求客户端契约，隔离 HTTP 传输与任务编排。
/// </summary>
public interface IGuidanceRequestClient
{
    /// <summary>当前是否已有请求等待响应。</summary>
    bool RequestInFlight { get; }

    /// <summary>按任务上下文发起一次 Guidance 请求。</summary>
    void RequestGuidance(GuidanceRequestContext context);

    /// <summary>废弃当前在途请求，确保旧响应不再进入业务流程。</summary>
    void CancelPendingRequest();
}

/// <summary>表示后端健康检查的容错状态。</summary>
public enum BackendHealthStatus
{
    /// <summary>尚未获得健康检查结果。</summary>
    Unknown,
    /// <summary>最近一次健康检查成功。</summary>
    Online,
    /// <summary>健康检查正在失败，但仍处于瞬时故障宽限期。</summary>
    Degraded,
    /// <summary>连续失败次数和失败持续时间均已超过离线阈值。</summary>
    Offline
}

/// <summary>保存一次不可变的后端健康状态转换信息。</summary>
public sealed class BackendHealthStatusInfo
{
    /// <summary>当前后端健康状态。</summary>
    public BackendHealthStatus Status { get; }
    /// <summary>当前连续失败次数。</summary>
    public int ConsecutiveFailures { get; }
    /// <summary>本轮连续失败持续时间，单位：秒。</summary>
    public float FailureElapsedSeconds { get; }
    /// <summary>进入 Offline 所需的连续失败次数。</summary>
    public int FailureThreshold { get; }
    /// <summary>进入 Offline 所需的失败宽限时间，单位：秒。</summary>
    public float OfflineGraceSeconds { get; }

    /// <summary>创建后端健康状态诊断快照。</summary>
    public BackendHealthStatusInfo(
        BackendHealthStatus status,
        int consecutiveFailures,
        float failureElapsedSeconds,
        int failureThreshold,
        float offlineGraceSeconds)
    {
        Status = status;
        ConsecutiveFailures = Math.Max(0, consecutiveFailures);
        FailureElapsedSeconds = Mathf.Max(0f, failureElapsedSeconds);
        FailureThreshold = Math.Max(1, failureThreshold);
        OfflineGraceSeconds = Mathf.Max(0f, offlineGraceSeconds);
    }
}

/// <summary>
/// Unity 到后端 guidance endpoint 的 HTTP 客户端，只负责请求和解析，不直接执行导览效果。
/// </summary>
public sealed class AgentClient : MonoBehaviour, IGuidanceRequestClient
{
    private static readonly HashSet<string> ResponseFieldNames = new HashSet<string>(StringComparer.Ordinal)
    {
        "task_definition_version",
        "guidance_schema_version",
        "candidate_policy_version",
        "risk_policy_version",
        "session_id",
        "calibration_id",
        "request_id",
        "decision_id",
        "candidate_set_id",
        "state_version",
        "risk_snapshot_id",
        "risk_revision",
        "target_role",
        "selected_target",
        "route_decision",
        "narration",
        "gaze_cue",
        "gaze_target_id",
        "movement_instruction",
        "physical_intent",
        "verifier"
    };

    private static readonly HashSet<string> VerifierFieldNames = new HashSet<string>(StringComparer.Ordinal)
    {
        "approved",
        "reason"
    };

    [Header("后端接口")]
    [Tooltip("后端 guidance endpoint 地址。")]
    [SerializeField] private string guidanceEndpoint = "http://127.0.0.1:8000/api/guidance";
    [Tooltip("请求超时时间，单位：秒。")]
    [SerializeField] private int requestTimeoutSeconds = 8;
    [Tooltip("后端健康检查地址。")]
    [SerializeField] private string healthEndpoint = "http://127.0.0.1:8000/health";
    [Tooltip("健康检查间隔，单位：秒。")]
    [SerializeField] private float healthCheckIntervalSeconds = 2f;
    [Tooltip("进入离线状态前允许的连续健康检查失败次数。")]
    [SerializeField, Min(1)] private int healthCheckFailureThreshold = 3;
    [Tooltip("进入离线状态前允许的连续失败时间，单位：秒。")]
    [SerializeField, Min(0f)] private float backendOfflineGraceSeconds = 8f;
    [Tooltip("活动实验在 BackendHold 中等待恢复的最长时间，单位：秒。")]
    [SerializeField, Min(1f)] private float backendFatalTimeoutSeconds = 30f;

    [Header("状态来源")]
    [Tooltip("当前 physical space 管理器；未配置时自动查找。")]
    [SerializeField] private PhysicalSpaceManager physicalSpaceManager;
    [Tooltip("用户虚拟/物理位姿来源；未配置时自动查找。")]
    [SerializeField] private UserPoseTracker userPoseTracker;
    [Tooltip("提供请求对应的 risk_snapshot_id。")]
    [SerializeField] private PhysicalRiskEvaluator riskEvaluator;
    [Tooltip("提供请求对应的 calibration_id。")]
    [SerializeField] private PhysicalSpaceCalibration physicalSpaceCalibration;
    [Tooltip("提供当前 session_id。")]
    [SerializeField] private ExperimentLogger experimentLogger;

    [Header("默认请求上下文")]
    [Tooltip("没有外部任务编排传入上下文时使用的默认 guidance 请求参数。")]
    [SerializeField] private GuidanceRequestContext defaultContext = new GuidanceRequestContext();

    /// <summary>
    /// 每个成功解析的请求响应触发一次，无论 verifier 为 approved 或 rejected；参数为完整 guidance response。
    /// </summary>
    [Header("事件")]
    [Tooltip("后端返回 approved 或 rejected guidance response 后触发。")]
    public GuidanceResponseEvent GuidanceReceived = new GuidanceResponseEvent();

    /// <summary>
    /// 每次请求因网络、HTTP 状态或响应解析失败时触发一次；参数为可展示给调试 UI 的错误信息。
    /// </summary>
    [Tooltip("guidance 请求失败时触发，参数为可展示给调试 UI 的错误信息。")]
    public StringEvent GuidanceRequestFailed = new StringEvent();

    /// <summary>
    /// 每个实际发出的 guidance 请求开始时触发一次；参数为当前请求状态文本。
    /// </summary>
    [Tooltip("guidance 请求开始时触发，参数为当前请求状态文本。")]
    public StringEvent GuidanceRequestStarted = new StringEvent();

    [Tooltip("后端在线状态发生变化时触发。")]
    public BoolEvent BackendOnlineChanged = new BoolEvent();

    /// <summary>
    /// 请求上下文和位姿组装完成、发送前触发一次；参数为本次请求快照，供日志和过期响应检测使用。
    /// </summary>
    public event Action<GuidanceRequest> GuidanceRequestBuilt;
    /// <summary>后端健康状态发生变化时触发。</summary>
    public event Action<BackendHealthStatusInfo> BackendHealthChanged;

    private GuidanceRequest lastRequest;
    private bool requestInFlight;
    private bool healthRequestInFlight;
    private bool backendOnline;
    private int requestGeneration;
    private UnityWebRequest activeGuidanceRequest;
    private Coroutine healthLoop;
    private BackendHealthStatus backendHealthStatus = BackendHealthStatus.Unknown;
    private int consecutiveHealthCheckFailures;
    private float firstHealthCheckFailureTime = -1f;
    private bool lastRequestFailureRecoverable;

    /// <summary>
    /// 当前是否正在等待后端响应，用于避免重复请求造成状态乱序。
    /// </summary>
    public bool RequestInFlight => requestInFlight;
    /// <summary>后端是否已成功连接且尚未超过离线判定阈值。</summary>
    public bool BackendOnline => backendOnline;
    /// <summary>当前后端健康状态。</summary>
    public BackendHealthStatus BackendHealthStatus => backendHealthStatus;
    /// <summary>当前连续健康检查失败次数。</summary>
    public int ConsecutiveHealthCheckFailures => consecutiveHealthCheckFailures;
    /// <summary>活动实验允许等待后端恢复的最长时间，单位：秒。</summary>
    public float BackendFatalTimeoutSeconds => Mathf.Max(1f, backendFatalTimeoutSeconds);
    /// <summary>最近一次 Guidance 请求失败是否属于可恢复的连接、无响应或服务端暂时故障。</summary>
    public bool LastRequestFailureRecoverable => lastRequestFailureRecoverable;

    private void Awake()
    {
        ResolveReferences();
    }

    private void OnEnable()
    {
        ResetHealthTracking();
        healthLoop = StartCoroutine(HealthLoop());
    }

    private void OnDisable()
    {
        if (healthLoop != null)
        {
            StopCoroutine(healthLoop);
            healthLoop = null;
        }

        CancelPendingRequest();
        healthRequestInFlight = false;
        ResetHealthTracking();
    }

    /// <summary>
    /// 注入依赖和默认上下文，供 bootstrap 或后续 ExperimentDashboard 统一配置。
    /// </summary>
    public void Configure(
        PhysicalSpaceManager spaceManager,
        UserPoseTracker poseTracker,
        GuidanceRequestContext context = null,
        PhysicalRiskEvaluator evaluator = null,
        PhysicalSpaceCalibration calibration = null,
        ExperimentLogger logger = null)
    {
        if (spaceManager != null) physicalSpaceManager = spaceManager;
        if (poseTracker != null) userPoseTracker = poseTracker;
        if (evaluator != null) riskEvaluator = evaluator;
        if (calibration != null) physicalSpaceCalibration = calibration;
        if (logger != null) experimentLogger = logger;
        if (context != null)
        {
            defaultContext = context;
        }
    }

    /// <summary>
    /// 按当前场景状态组装请求并发送给后端。
    /// </summary>
    public void RequestGuidance()
    {
        ResolveReferences();
        RequestGuidance(defaultContext);
    }

    /// <summary>
    /// 按指定上下文组装请求并发送给后端，Phase 5 可用它接入任务编排。
    /// </summary>
    public void RequestGuidance(GuidanceRequestContext context)
    {
        lastRequestFailureRecoverable = false;
        if (requestInFlight)
        {
            GuidanceRequestFailed?.Invoke("Guidance request is already in progress.");
            return;
        }

        var request = BuildRequest(context ?? defaultContext);
        if (request == null)
        {
            GuidanceRequestFailed?.Invoke("Cannot build guidance request: tracking, calibration, physical space, or risk is not ready.");
            return;
        }

        lastRequest = request;
        GuidanceRequestBuilt?.Invoke(request);
        var generation = ++requestGeneration;
        requestInFlight = true;
        StartCoroutine(SendGuidanceRequest(request, generation));
    }

    /// <summary>拒绝绕过 Coordinator 的直接重试；重试必须创建新的 Candidate-Set Reservation。</summary>
    public void RetryLastRequest()
    {
        lastRequestFailureRecoverable = false;
        GuidanceRequestFailed?.Invoke(
            "Direct retry is disabled; GuidanceCoordinator must create a new Candidate-Set Reservation.");
    }

    /// <summary>立即废弃并中止当前 guidance 请求；旧响应不会再触发任何业务事件。</summary>
    public void CancelPendingRequest()
    {
        requestGeneration++;
        requestInFlight = false;
        if (activeGuidanceRequest != null)
        {
            activeGuidanceRequest.Abort();
            activeGuidanceRequest = null;
        }
    }

    /// <summary>立即触发一次后端健康检查。</summary>
    public void CheckHealth()
    {
        if (!healthRequestInFlight && isActiveAndEnabled)
        {
            StartCoroutine(SendHealthRequest());
        }
    }

    /// <summary>
    /// 解析并校验 Backend GuidanceResponse；缺字段、未知枚举、Identity 或 Target 不匹配时 Fail-Closed。
    /// </summary>
    /// <param name="responseText">Backend 返回的原始 JSON。</param>
    /// <param name="request">产生该响应的当前请求。</param>
    /// <param name="response">成功时返回完整 GuidanceResponse。</param>
    /// <param name="failureReason">失败时返回稳定、可诊断的协议错误。</param>
    /// <returns>JSON 结构完整且精确回显当前请求契约时返回 true。</returns>
    public static bool TryParseGuidanceResponse(
        string responseText,
        GuidanceRequest request,
        out GuidanceResponse response,
        out string failureReason)
    {
        response = null;
        failureReason = string.Empty;
        if (string.IsNullOrWhiteSpace(responseText))
        {
            failureReason = "empty response body";
            return false;
        }

        try
        {
            var responseObject = JObject.Parse(responseText);
            if (!HasExactFields(responseObject, ResponseFieldNames, "response", out failureReason))
            {
                return false;
            }

            if (!(responseObject["verifier"] is JObject verifierObject))
            {
                failureReason = "verifier must be a JSON object";
                return false;
            }

            if (!HasExactFields(verifierObject, VerifierFieldNames, "verifier", out failureReason))
            {
                return false;
            }

            response = responseObject.ToObject<GuidanceResponse>();
        }
        catch (JsonException error)
        {
            failureReason = $"invalid JSON: {error.Message}";
            return false;
        }

        return ValidateGuidanceResponseContract(response, request, out failureReason);
    }

    private static bool HasExactFields(
        JObject objectValue,
        HashSet<string> expectedFields,
        string objectName,
        out string failureReason)
    {
        foreach (var expectedField in expectedFields)
        {
            if (objectValue.Property(expectedField) == null)
            {
                failureReason = $"required {objectName} field is missing: {expectedField}";
                return false;
            }
        }

        foreach (var property in objectValue.Properties())
        {
            if (!expectedFields.Contains(property.Name))
            {
                failureReason = $"unexpected {objectName} field: {property.Name}";
                return false;
            }
        }

        failureReason = string.Empty;
        return true;
    }

    private GuidanceRequest BuildRequest(GuidanceRequestContext context)
    {
        if (context == null || string.IsNullOrWhiteSpace(context.request_id) ||
            string.IsNullOrWhiteSpace(context.decision_id) || string.IsNullOrWhiteSpace(context.candidate_set_id) ||
            context.candidate_ids == null || context.candidate_ids.Length == 0 ||
            context.model_candidate_ids == null || context.model_candidate_ids.Length == 0 ||
            context.semantic_candidates == null || !TryGetCurrentRiskState(out var riskState) ||
            !MatchesOptionalIdentity(context.calibration_id, riskState.calibration_id) ||
            !MatchesOptionalIdentity(context.risk_snapshot_id, riskState.risk_snapshot_id))
        {
            return null;
        }

        var virtualPosition = userPoseTracker.UserVirtualPosition;
        var virtualForward = userPoseTracker.UserVirtualForward;

        return new GuidanceRequest
        {
            task_definition_version = GuidanceProtocolVersions.TaskDefinition,
            guidance_schema_version = GuidanceProtocolVersions.GuidanceSchema,
            candidate_policy_version = GuidanceProtocolVersions.CandidatePolicy,
            risk_policy_version = GuidanceProtocolVersions.RiskPolicy,
            session_id = EmptyFallback(
                context.session_id,
                experimentLogger != null && experimentLogger.Session != null
                    ? experimentLogger.Session.sessionId
                    : string.Empty),
            calibration_id = EmptyFallback(
                context.calibration_id,
                physicalSpaceCalibration != null
                    ? physicalSpaceCalibration.CalibrationId
                    : riskState != null ? riskState.calibration_id : string.Empty),
            request_id = context.request_id,
            decision_id = context.decision_id,
            candidate_set_id = context.candidate_set_id,
            state_version = context.state_version,
            risk_snapshot_id = EmptyFallback(
                context.risk_snapshot_id,
                riskState.risk_snapshot_id),
            condition = EmptyFallback(context.condition, "ours_full"),
            task_id = EmptyFallback(context.task_id, "T2_main_target"),
            physical_space_id = EmptyFallback(physicalSpaceManager.CurrentSpaceId, "wide_room"),
            current_exhibit = EmptyFallback(context.current_exhibit, "E1"),
            final_target = EmptyFallback(context.final_target, "E5"),
            preferred_task_target = EmptyFallback(context.preferred_task_target, "E5"),
            candidate_ids = context.candidate_ids,
            model_candidate_ids = context.model_candidate_ids,
            allowed_candidate_ids = context.allowed_candidate_ids ?? Array.Empty<string>(),
            semantic_candidates = context.semantic_candidates,
            participant_id = EmptyFallback(context.participant_id, "P001"),
            user_task = context.user_task,
            risk_snapshot = GuidanceRiskSnapshot.From(riskState),
            user_physical_position = GuidanceTypeUtility.Vector2ToArray(riskState.user_physical_position),
            user_physical_forward = GuidanceTypeUtility.Vector2ToArray(riskState.user_physical_forward),
            user_virtual_position = GuidanceTypeUtility.Vector3ToArray(virtualPosition),
            user_virtual_forward = GuidanceTypeUtility.Vector3ToArray(virtualForward)
        };
    }

    private bool TryGetCurrentRiskState(out PhysicalRiskState riskState)
    {
        riskState = null;
        if (physicalSpaceManager == null || userPoseTracker == null || physicalSpaceManager.CurrentSpace == null ||
            physicalSpaceCalibration == null || !physicalSpaceCalibration.IsCalibrationValid ||
            !string.Equals(
                physicalSpaceCalibration.CalibratedSpaceId,
                physicalSpaceManager.CurrentSpaceId,
                StringComparison.Ordinal))
        {
            return false;
        }

        riskState = riskEvaluator != null ? riskEvaluator.CurrentRiskState : null;
        if (riskState == null && riskEvaluator != null)
        {
            riskState = riskEvaluator.EvaluateNow();
        }

        return riskState != null &&
               !string.IsNullOrWhiteSpace(riskState.risk_snapshot_id) &&
               riskState.risk_revision > 0 &&
               string.Equals(riskState.source, "unity", StringComparison.Ordinal) &&
               string.Equals(riskState.calibration_id, physicalSpaceCalibration.CalibrationId, StringComparison.Ordinal) &&
               string.Equals(riskState.physical_space, physicalSpaceManager.CurrentSpaceId, StringComparison.Ordinal);
    }

    private bool MatchesCurrentRequestIdentity(GuidanceRequest request, PhysicalRiskState riskState)
    {
        return request != null &&
               string.Equals(request.calibration_id, riskState.calibration_id, StringComparison.Ordinal) &&
               request.risk_snapshot != null &&
               request.risk_snapshot.risk_revision == riskState.risk_revision &&
               string.Equals(request.physical_space_id, physicalSpaceManager.CurrentSpaceId, StringComparison.Ordinal);
    }

    private static bool MatchesOptionalIdentity(string expected, string current)
    {
        return string.IsNullOrWhiteSpace(expected) || string.Equals(expected, current, StringComparison.Ordinal);
    }

    private static bool ValidateGuidanceResponseContract(
        GuidanceResponse response,
        GuidanceRequest request,
        out string failureReason)
    {
        if (request == null)
        {
            failureReason = "current request is unavailable";
            return false;
        }

        if (response == null)
        {
            failureReason = "response is null";
            return false;
        }

        if (string.IsNullOrWhiteSpace(response.task_definition_version) ||
            string.IsNullOrWhiteSpace(response.guidance_schema_version) ||
            string.IsNullOrWhiteSpace(response.candidate_policy_version) ||
            string.IsNullOrWhiteSpace(response.risk_policy_version) ||
            string.IsNullOrWhiteSpace(response.session_id) ||
            string.IsNullOrWhiteSpace(response.calibration_id) ||
            string.IsNullOrWhiteSpace(response.request_id) ||
            string.IsNullOrWhiteSpace(response.decision_id) ||
            string.IsNullOrWhiteSpace(response.candidate_set_id) ||
            response.state_version < 0 ||
            response.risk_revision < 1 ||
            string.IsNullOrWhiteSpace(response.risk_snapshot_id) ||
            string.IsNullOrWhiteSpace(response.target_role) ||
            string.IsNullOrWhiteSpace(response.selected_target) ||
            string.IsNullOrWhiteSpace(response.route_decision) ||
            string.IsNullOrWhiteSpace(response.narration) ||
            string.IsNullOrWhiteSpace(response.gaze_cue) ||
            string.IsNullOrWhiteSpace(response.gaze_target_id) ||
            string.IsNullOrWhiteSpace(response.movement_instruction) ||
            string.IsNullOrWhiteSpace(response.physical_intent) ||
            response.verifier == null ||
            string.IsNullOrWhiteSpace(response.verifier.reason))
        {
            failureReason = "required response field is missing";
            return false;
        }

        if (!string.Equals(response.task_definition_version, request.task_definition_version, StringComparison.Ordinal) ||
            !string.Equals(response.guidance_schema_version, request.guidance_schema_version, StringComparison.Ordinal) ||
            !string.Equals(response.candidate_policy_version, request.candidate_policy_version, StringComparison.Ordinal) ||
            !string.Equals(response.risk_policy_version, request.risk_policy_version, StringComparison.Ordinal) ||
            !string.Equals(response.session_id, request.session_id, StringComparison.Ordinal) ||
            !string.Equals(response.calibration_id, request.calibration_id, StringComparison.Ordinal) ||
            !string.Equals(response.request_id, request.request_id, StringComparison.Ordinal) ||
            !string.Equals(response.decision_id, request.decision_id, StringComparison.Ordinal) ||
            !string.Equals(response.candidate_set_id, request.candidate_set_id, StringComparison.Ordinal) ||
            response.state_version != request.state_version ||
            !string.Equals(response.risk_snapshot_id, request.risk_snapshot_id, StringComparison.Ordinal) ||
            request.risk_snapshot == null || response.risk_revision != request.risk_snapshot.risk_revision)
        {
            failureReason = "response identity does not match the current request";
            return false;
        }

        if (!Contains(request.model_candidate_ids, response.selected_target))
        {
            failureReason = "selected_target is outside model_candidate_ids";
            return false;
        }

        if (!TryGetCandidateRole(request.semantic_candidates, response.selected_target, out var expectedRole) ||
            !string.Equals(response.target_role, expectedRole, StringComparison.Ordinal))
        {
            failureReason = "target_role does not match the frozen candidate metadata";
            return false;
        }

        if (!IsKnownTargetRole(response.target_role) ||
            !IsKnownMovementInstruction(response.movement_instruction))
        {
            failureReason = "response contains an unknown protocol enum value";
            return false;
        }

        failureReason = string.Empty;
        return true;
    }

    private static bool TryGetCandidateRole(
        GuidanceSemanticCandidate[] candidates,
        string targetId,
        out string targetRole)
    {
        if (candidates != null)
        {
            foreach (var candidate in candidates)
            {
                if (candidate != null && string.Equals(candidate.id, targetId, StringComparison.Ordinal))
                {
                    targetRole = candidate.target_role;
                    return !string.IsNullOrWhiteSpace(targetRole);
                }
            }
        }

        targetRole = string.Empty;
        return false;
    }

    private static bool Contains(string[] values, string expected)
    {
        if (values == null || string.IsNullOrWhiteSpace(expected)) return false;
        foreach (var value in values)
        {
            if (string.Equals(value, expected, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    private static bool IsKnownTargetRole(string value)
    {
        return value == "final_target" ||
               value == "route_subgoal" ||
               value == "turn_cue_anchor" ||
               value == "navigable_detour";
    }

    private static bool IsKnownMovementInstruction(string value)
    {
        return value == "move_forward_slowly" ||
               value == "turn_left_slowly" ||
               value == "turn_right_slowly" ||
               value == "look_left" ||
               value == "look_right" ||
               value == "look_forward" ||
               value == "pause_and_wait" ||
               value == "reset_required";
    }

    private IEnumerator SendGuidanceRequest(GuidanceRequest request, int generation)
    {
        requestInFlight = true;
        GuidanceRequestStarted?.Invoke("Requesting guidance...");

        var json = SerializeGuidanceRequest(request);
        using (var webRequest = new UnityWebRequest(guidanceEndpoint, UnityWebRequest.kHttpVerbPOST))
        {
            activeGuidanceRequest = webRequest;
            webRequest.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
            webRequest.downloadHandler = new DownloadHandlerBuffer();
            webRequest.timeout = Mathf.Max(1, requestTimeoutSeconds);
            webRequest.SetRequestHeader("Content-Type", "application/json");

            yield return webRequest.SendWebRequest();
            if (activeGuidanceRequest == webRequest)
            {
                activeGuidanceRequest = null;
            }

            if (generation != requestGeneration)
            {
                yield break;
            }

            requestInFlight = false;

            var responseText = webRequest.downloadHandler != null ? webRequest.downloadHandler.text : string.Empty;

            if (webRequest.result != UnityWebRequest.Result.Success)
            {
                lastRequestFailureRecoverable =
                    webRequest.result == UnityWebRequest.Result.ConnectionError ||
                    webRequest.responseCode <= 0 ||
                    webRequest.responseCode >= 500;
                if (lastRequestFailureRecoverable)
                {
                    RecordHealthCheckResult(false, Time.realtimeSinceStartup);
                }
                GuidanceRequestFailed?.Invoke(FormatGuidanceRequestFailure(webRequest.error, responseText));
                yield break;
            }

            RecordHealthCheckResult(true, Time.realtimeSinceStartup);
            if (!TryParseGuidanceResponse(responseText, request, out var response, out var failureReason))
            {
                lastRequestFailureRecoverable = false;
                GuidanceRequestFailed?.Invoke($"Guidance response rejected: {failureReason}.");
                yield break;
            }

            GuidanceReceived?.Invoke(response);
        }
    }

    /// <summary>
    /// 使用支持嵌套数组的 JSON.NET 序列化正式请求，确保边界点不会退化为空对象。
    /// </summary>
    public static string SerializeGuidanceRequest(GuidanceRequest request)
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        return JsonConvert.SerializeObject(request, Formatting.None);
    }

    /// <summary>
    /// 把 HTTP 状态与 Backend 结构化错误合并为限长诊断文本，避免丢失 FastAPI 字段级校验原因。
    /// </summary>
    public static string FormatGuidanceRequestFailure(string transportError, string responseBody)
    {
        const int MaxDetailLength = 512;
        var message = $"Guidance request failed: {EmptyFallback(transportError, "unknown transport error")}";
        if (string.IsNullOrWhiteSpace(responseBody))
        {
            return message;
        }

        string detail;
        try
        {
            var responseToken = JToken.Parse(responseBody);
            detail = responseToken["detail"] != null
                ? responseToken["detail"].ToString(Formatting.None)
                : responseToken.ToString(Formatting.None);
        }
        catch (JsonException)
        {
            detail = responseBody.Trim()
                .Replace("\r\n", " ")
                .Replace("\r", " ")
                .Replace("\n", " ");
        }

        if (detail.Length > MaxDetailLength)
        {
            detail = detail.Substring(0, MaxDetailLength) + "...";
        }
        return $"{message}; backend detail: {detail}";
    }

    private IEnumerator HealthLoop()
    {
        while (isActiveAndEnabled)
        {
            yield return SendHealthRequest();
            yield return new WaitForSecondsRealtime(Mathf.Max(0.5f, healthCheckIntervalSeconds));
        }
    }

    private IEnumerator SendHealthRequest()
    {
        if (healthRequestInFlight)
        {
            yield break;
        }

        healthRequestInFlight = true;
        using (var request = UnityWebRequest.Get(healthEndpoint))
        {
            request.timeout = Mathf.Max(1, requestTimeoutSeconds);
            yield return request.SendWebRequest();
            RecordHealthCheckResult(
                request.result == UnityWebRequest.Result.Success &&
                request.responseCode >= 200 &&
                request.responseCode < 300,
                Time.realtimeSinceStartup);
        }

        healthRequestInFlight = false;
    }

    private void RecordHealthCheckResult(bool succeeded, float timestampSeconds)
    {
        if (succeeded)
        {
            consecutiveHealthCheckFailures = 0;
            firstHealthCheckFailureTime = -1f;
            SetBackendHealthStatus(BackendHealthStatus.Online, 0f);
            return;
        }

        if (consecutiveHealthCheckFailures == 0)
        {
            firstHealthCheckFailureTime = timestampSeconds;
        }

        consecutiveHealthCheckFailures++;
        var failureElapsedSeconds = firstHealthCheckFailureTime >= 0f
            ? Mathf.Max(0f, timestampSeconds - firstHealthCheckFailureTime)
            : 0f;
        var offline = consecutiveHealthCheckFailures >= Mathf.Max(1, healthCheckFailureThreshold) &&
                      failureElapsedSeconds >= Mathf.Max(0f, backendOfflineGraceSeconds);
        SetBackendHealthStatus(
            offline ? BackendHealthStatus.Offline : BackendHealthStatus.Degraded,
            failureElapsedSeconds,
            true);
    }

    private void SetBackendHealthStatus(
        BackendHealthStatus status,
        float failureElapsedSeconds,
        bool forceNotification = false)
    {
        var previousOnline = backendOnline;
        backendOnline = status == BackendHealthStatus.Online ||
                        (status == BackendHealthStatus.Degraded && backendOnline);
        var statusChanged = backendHealthStatus != status;
        backendHealthStatus = status;
        if (statusChanged || forceNotification)
        {
            BackendHealthChanged?.Invoke(CreateBackendHealthInfo(failureElapsedSeconds));
        }

        if (previousOnline != backendOnline)
        {
            BackendOnlineChanged?.Invoke(backendOnline);
        }
    }

    private BackendHealthStatusInfo CreateBackendHealthInfo(float failureElapsedSeconds)
    {
        return new BackendHealthStatusInfo(
            backendHealthStatus,
            consecutiveHealthCheckFailures,
            failureElapsedSeconds,
            healthCheckFailureThreshold,
            backendOfflineGraceSeconds);
    }

    private void ResetHealthTracking()
    {
        consecutiveHealthCheckFailures = 0;
        firstHealthCheckFailureTime = -1f;
        backendOnline = false;
        backendHealthStatus = BackendHealthStatus.Unknown;
    }

#if UNITY_EDITOR
    /// <summary>仅供 EditMode 测试设置健康状态。</summary>
    public void SetBackendOnlineForTesting(bool online)
    {
        consecutiveHealthCheckFailures = online ? 0 : Mathf.Max(1, healthCheckFailureThreshold);
        firstHealthCheckFailureTime = online ? -1f : 0f;
        SetBackendHealthStatus(
            online ? BackendHealthStatus.Online : BackendHealthStatus.Offline,
            online ? 0f : Mathf.Max(0f, backendOfflineGraceSeconds),
            true);
    }

    /// <summary>仅供 EditMode 测试配置后端健康容错阈值。</summary>
    public void ConfigureHealthPolicyForTesting(
        int failureThreshold,
        float offlineGraceSeconds,
        float fatalTimeoutSeconds = 30f)
    {
        healthCheckFailureThreshold = Mathf.Max(1, failureThreshold);
        backendOfflineGraceSeconds = Mathf.Max(0f, offlineGraceSeconds);
        backendFatalTimeoutSeconds = Mathf.Max(1f, fatalTimeoutSeconds);
        ResetHealthTracking();
    }

    /// <summary>仅供 EditMode 测试注入一次健康检查结果及其时间。</summary>
    public void RecordHealthCheckResultForTesting(bool succeeded, float timestampSeconds)
    {
        RecordHealthCheckResult(succeeded, Mathf.Max(0f, timestampSeconds));
    }
#endif

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

        if (riskEvaluator == null) riskEvaluator = FindObjectOfType<PhysicalRiskEvaluator>();
        if (physicalSpaceCalibration == null) physicalSpaceCalibration = FindObjectOfType<PhysicalSpaceCalibration>();
        if (experimentLogger == null) experimentLogger = FindObjectOfType<ExperimentLogger>();
    }

    private static string EmptyFallback(string value, string fallback)
    {
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }
}

/// <summary>
/// UnityEvent 封装 guidance response，便于 Inspector 或 bootstrap 接线。
/// </summary>
[Serializable]
public sealed class GuidanceResponseEvent : UnityEvent<GuidanceResponse>
{
}

/// <summary>
/// UnityEvent 封装字符串状态或错误信息。
/// </summary>
[Serializable]
public sealed class StringEvent : UnityEvent<string>
{
}

/// <summary>UnityEvent 封装布尔状态。</summary>
[Serializable]
public sealed class BoolEvent : UnityEvent<bool>
{
}
