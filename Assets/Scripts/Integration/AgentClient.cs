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

/// <summary>
/// Unity 到后端 guidance endpoint 的 HTTP 客户端，只负责请求和解析，不直接执行导览效果。
/// </summary>
public sealed class AgentClient : MonoBehaviour, IGuidanceRequestClient
{
    private static readonly HashSet<string> ResponseFieldNames = new HashSet<string>(StringComparer.Ordinal)
    {
        "session_id",
        "calibration_id",
        "request_id",
        "decision_id",
        "state_version",
        "risk_snapshot_id",
        "target_role",
        "selected_target",
        "route_decision",
        "narration",
        "gaze_cue",
        "gaze_target_id",
        "avatar_action",
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

    private GuidanceRequest lastRequest;
    private bool requestInFlight;
    private bool healthRequestInFlight;
    private bool backendOnline;
    private int requestGeneration;
    private UnityWebRequest activeGuidanceRequest;
    private Coroutine healthLoop;

    /// <summary>
    /// 当前是否正在等待后端响应，用于避免重复请求造成状态乱序。
    /// </summary>
    public bool RequestInFlight => requestInFlight;
    /// <summary>最近一次健康检查是否成功。</summary>
    public bool BackendOnline => backendOnline;

    private void Awake()
    {
        ResolveReferences();
    }

    private void OnEnable()
    {
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

    /// <summary>
    /// 在 Calibration、Physical Space 与 Risk Snapshot 仍匹配时重试上一次请求。
    /// </summary>
    public void RetryLastRequest()
    {
        if (requestInFlight)
        {
            GuidanceRequestFailed?.Invoke("Guidance retry skipped because a request is already in progress.");
            return;
        }

        if (lastRequest == null)
        {
            RequestGuidance();
            return;
        }

        if (!TryGetCurrentRiskState(out var riskState) || !MatchesCurrentRequestIdentity(lastRequest, riskState))
        {
            GuidanceRequestFailed?.Invoke("Guidance retry rejected because calibration, physical space, or risk snapshot changed.");
            return;
        }

        // 重试属于同一 decision，但必须使用新的 request_id 以区分每次传输尝试。
        lastRequest.request_id = Guid.NewGuid().ToString("N");
        GuidanceRequestBuilt?.Invoke(lastRequest);
        var generation = ++requestGeneration;
        requestInFlight = true;
        StartCoroutine(SendGuidanceRequest(lastRequest, generation));
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
        if (!TryGetCurrentRiskState(out var riskState) ||
            !MatchesOptionalIdentity(context.calibration_id, riskState.calibration_id) ||
            !MatchesOptionalIdentity(context.risk_snapshot_id, riskState.risk_snapshot_id))
        {
            return null;
        }

        var physicalPosition = userPoseTracker.PhysicalPositionXZ;
        var physicalForward = userPoseTracker.PhysicalForwardXZ;
        var virtualPosition = userPoseTracker.UserVirtualPosition;
        var virtualForward = userPoseTracker.UserVirtualForward;

        return new GuidanceRequest
        {
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
            request_id = Guid.NewGuid().ToString("N"),
            decision_id = EmptyFallback(context.decision_id, Guid.NewGuid().ToString("N")),
            state_version = context.state_version,
            risk_snapshot_id = EmptyFallback(
                context.risk_snapshot_id,
                riskState.risk_snapshot_id),
            target_role = EmptyFallback(context.target_role, "route_subgoal"),
            condition = EmptyFallback(context.condition, "ours_full"),
            task_id = EmptyFallback(context.task_id, "T2_main_target"),
            physical_space_id = EmptyFallback(physicalSpaceManager.CurrentSpaceId, "wide_room"),
            current_exhibit = EmptyFallback(context.current_exhibit, "E1"),
            semantic_next_target = EmptyFallback(context.semantic_next_target, "E5"),
            participant_id = EmptyFallback(context.participant_id, "P001"),
            user_task = context.user_task,
            user_physical_position = GuidanceTypeUtility.Vector2ToArray(physicalPosition),
            user_physical_forward = GuidanceTypeUtility.Vector2ToArray(physicalForward),
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
               string.Equals(riskState.calibration_id, physicalSpaceCalibration.CalibrationId, StringComparison.Ordinal) &&
               string.Equals(riskState.physical_space, physicalSpaceManager.CurrentSpaceId, StringComparison.Ordinal);
    }

    private bool MatchesCurrentRequestIdentity(GuidanceRequest request, PhysicalRiskState riskState)
    {
        return request != null &&
               string.Equals(request.calibration_id, riskState.calibration_id, StringComparison.Ordinal) &&
               string.Equals(request.risk_snapshot_id, riskState.risk_snapshot_id, StringComparison.Ordinal) &&
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

        if (string.IsNullOrWhiteSpace(response.session_id) ||
            string.IsNullOrWhiteSpace(response.calibration_id) ||
            string.IsNullOrWhiteSpace(response.request_id) ||
            string.IsNullOrWhiteSpace(response.decision_id) ||
            response.state_version < 0 ||
            string.IsNullOrWhiteSpace(response.risk_snapshot_id) ||
            string.IsNullOrWhiteSpace(response.target_role) ||
            string.IsNullOrWhiteSpace(response.selected_target) ||
            string.IsNullOrWhiteSpace(response.route_decision) ||
            string.IsNullOrWhiteSpace(response.narration) ||
            string.IsNullOrWhiteSpace(response.gaze_cue) ||
            string.IsNullOrWhiteSpace(response.gaze_target_id) ||
            string.IsNullOrWhiteSpace(response.avatar_action) ||
            string.IsNullOrWhiteSpace(response.movement_instruction) ||
            string.IsNullOrWhiteSpace(response.physical_intent) ||
            response.verifier == null ||
            string.IsNullOrWhiteSpace(response.verifier.reason))
        {
            failureReason = "required response field is missing";
            return false;
        }

        if (!string.Equals(response.session_id, request.session_id, StringComparison.Ordinal) ||
            !string.Equals(response.calibration_id, request.calibration_id, StringComparison.Ordinal) ||
            !string.Equals(response.request_id, request.request_id, StringComparison.Ordinal) ||
            !string.Equals(response.decision_id, request.decision_id, StringComparison.Ordinal) ||
            response.state_version != request.state_version ||
            !string.Equals(response.risk_snapshot_id, request.risk_snapshot_id, StringComparison.Ordinal))
        {
            failureReason = "response identity does not match the current request";
            return false;
        }

        if (!string.Equals(response.target_role, request.target_role, StringComparison.Ordinal))
        {
            failureReason = "target_role does not match the current request";
            return false;
        }

        if (!string.Equals(response.selected_target, request.semantic_next_target, StringComparison.Ordinal))
        {
            failureReason = "selected_target does not match semantic_next_target";
            return false;
        }

        if (!IsKnownTargetRole(response.target_role) ||
            !IsKnownMovementInstruction(response.movement_instruction) ||
            !IsKnownAvatarAction(response.avatar_action))
        {
            failureReason = "response contains an unknown protocol enum value";
            return false;
        }

        failureReason = string.Empty;
        return true;
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

    private static bool IsKnownAvatarAction(string value)
    {
        return value == "point_left" ||
               value == "point_right" ||
               value == "point_forward" ||
               value == "look_at_target" ||
               value == "wait";
    }

    private IEnumerator SendGuidanceRequest(GuidanceRequest request, int generation)
    {
        requestInFlight = true;
        GuidanceRequestStarted?.Invoke("Requesting guidance...");

        var json = JsonUtility.ToJson(request);
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

            if (webRequest.result != UnityWebRequest.Result.Success)
            {
                GuidanceRequestFailed?.Invoke($"Guidance request failed: {webRequest.error}");
                yield break;
            }

            var responseText = webRequest.downloadHandler != null ? webRequest.downloadHandler.text : string.Empty;
            if (!TryParseGuidanceResponse(responseText, request, out var response, out var failureReason))
            {
                GuidanceRequestFailed?.Invoke($"Guidance response rejected: {failureReason}.");
                yield break;
            }

            GuidanceReceived?.Invoke(response);
        }
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
            SetBackendOnline(request.result == UnityWebRequest.Result.Success && request.responseCode >= 200 && request.responseCode < 300);
        }

        healthRequestInFlight = false;
    }

    private void SetBackendOnline(bool online)
    {
        if (backendOnline == online)
        {
            return;
        }

        backendOnline = online;
        BackendOnlineChanged?.Invoke(backendOnline);
    }

#if UNITY_EDITOR
    /// <summary>仅供 EditMode 测试设置健康状态。</summary>
    public void SetBackendOnlineForTesting(bool online)
    {
        SetBackendOnline(online);
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
