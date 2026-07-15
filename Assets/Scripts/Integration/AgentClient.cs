using System;
using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.Networking;

/// <summary>
/// Unity 到后端 guidance endpoint 的 HTTP 客户端，只负责请求和解析，不直接执行导览效果。
/// </summary>
public sealed class AgentClient : MonoBehaviour
{
    [Header("后端接口")]
    [Tooltip("后端 guidance endpoint 地址。")]
    [SerializeField] private string guidanceEndpoint = "http://127.0.0.1:8000/api/guidance";
    [Tooltip("请求超时时间，单位：秒。")]
    [SerializeField] private int requestTimeoutSeconds = 8;

    [Header("状态来源")]
    [Tooltip("当前 physical space 管理器；未配置时自动查找。")]
    [SerializeField] private PhysicalSpaceManager physicalSpaceManager;
    [Tooltip("用户虚拟/物理位姿来源；未配置时自动查找。")]
    [SerializeField] private UserPoseTracker userPoseTracker;

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

    /// <summary>
    /// 请求上下文和位姿组装完成、发送前触发一次；参数为本次请求快照，供日志和过期响应检测使用。
    /// </summary>
    public event Action<GuidanceRequest> GuidanceRequestBuilt;

    private GuidanceRequest lastRequest;
    private bool requestInFlight;

    /// <summary>
    /// 当前是否正在等待后端响应，用于避免重复请求造成状态乱序。
    /// </summary>
    public bool RequestInFlight => requestInFlight;

    private void Awake()
    {
        ResolveReferences();
    }

    /// <summary>
    /// 注入依赖和默认上下文，供 bootstrap 或后续 ExperimentDashboard 统一配置。
    /// </summary>
    public void Configure(PhysicalSpaceManager spaceManager, UserPoseTracker poseTracker, GuidanceRequestContext context = null)
    {
        physicalSpaceManager = spaceManager;
        userPoseTracker = poseTracker;
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
            GuidanceRequestFailed?.Invoke("Cannot build guidance request: missing PhysicalSpaceManager or UserPoseTracker.");
            return;
        }

        lastRequest = request;
        GuidanceRequestBuilt?.Invoke(request);
        StartCoroutine(SendGuidanceRequest(request));
    }

    /// <summary>
    /// 重试上一次请求，常用于 rejected 或网络失败后的调试流程。
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

        StartCoroutine(SendGuidanceRequest(lastRequest));
    }

    private GuidanceRequest BuildRequest(GuidanceRequestContext context)
    {
        if (physicalSpaceManager == null || userPoseTracker == null)
        {
            return null;
        }

        var physicalPosition = userPoseTracker.PhysicalPositionXZ;
        var physicalForward = userPoseTracker.PhysicalForwardXZ;
        var virtualPosition = userPoseTracker.UserVirtualPosition;
        var virtualForward = userPoseTracker.UserVirtualForward;

        return new GuidanceRequest
        {
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

    private IEnumerator SendGuidanceRequest(GuidanceRequest request)
    {
        requestInFlight = true;
        GuidanceRequestStarted?.Invoke("Requesting guidance...");

        var json = JsonUtility.ToJson(request);
        using (var webRequest = new UnityWebRequest(guidanceEndpoint, UnityWebRequest.kHttpVerbPOST))
        {
            webRequest.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
            webRequest.downloadHandler = new DownloadHandlerBuffer();
            webRequest.timeout = Mathf.Max(1, requestTimeoutSeconds);
            webRequest.SetRequestHeader("Content-Type", "application/json");

            yield return webRequest.SendWebRequest();
            requestInFlight = false;

            if (webRequest.result != UnityWebRequest.Result.Success)
            {
                GuidanceRequestFailed?.Invoke($"Guidance request failed: {webRequest.error}");
                yield break;
            }

            var responseText = webRequest.downloadHandler != null ? webRequest.downloadHandler.text : string.Empty;
            if (string.IsNullOrWhiteSpace(responseText))
            {
                GuidanceRequestFailed?.Invoke("Guidance request failed: empty response body.");
                yield break;
            }

            GuidanceResponse response;
            try
            {
                response = JsonUtility.FromJson<GuidanceResponse>(responseText);
            }
            catch (Exception error)
            {
                GuidanceRequestFailed?.Invoke($"Guidance JSON parse failed: {error.Message}");
                yield break;
            }

            if (response == null)
            {
                GuidanceRequestFailed?.Invoke("Guidance JSON parse failed: response is null.");
                yield break;
            }

            GuidanceReceived?.Invoke(response);
        }
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
