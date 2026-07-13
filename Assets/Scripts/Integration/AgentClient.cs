using System;
using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.Networking;

/// <summary>
/// Unity ??? guidance endpoint ? HTTP ???????????????????????
/// </summary>
public sealed class AgentClient : MonoBehaviour
{
    [Header("????")]
    [Tooltip("?? guidance endpoint ???")]
    [SerializeField] private string guidanceEndpoint = "http://127.0.0.1:8000/api/guidance";
    [Tooltip("????????????")]
    [SerializeField] private int requestTimeoutSeconds = 8;

    [Header("????")]
    [Tooltip("?? physical space ?????????????")]
    [SerializeField] private PhysicalSpaceManager physicalSpaceManager;
    [Tooltip("????/????????????????")]
    [SerializeField] private UserPoseTracker userPoseTracker;

    [Header("???????")]
    [Tooltip("??????????????????? guidance ?????")]
    [SerializeField] private GuidanceRequestContext defaultContext = new GuidanceRequestContext();

    /// <summary>
    /// ???? approved ? rejected guidance response ????
    /// </summary>
    [Header("??")]
    [Tooltip("???? approved ? rejected guidance response ????")]
    public GuidanceResponseEvent GuidanceReceived = new GuidanceResponseEvent();

    /// <summary>
    /// guidance ????????????????? UI ??????
    /// </summary>
    [Tooltip("guidance ????????????????? UI ??????")]
    public StringEvent GuidanceRequestFailed = new StringEvent();

    /// <summary>
    /// guidance ????????????????????
    /// </summary>
    [Tooltip("guidance ????????????????????")]
    public StringEvent GuidanceRequestStarted = new StringEvent();

    private GuidanceRequest lastRequest;
    private bool requestInFlight;

    /// <summary>
    /// ????????????????????????????
    /// </summary>
    public bool RequestInFlight => requestInFlight;

    private void Awake()
    {
        ResolveReferences();
    }

    /// <summary>
    /// ???????????? bootstrap ??? ExperimentDashboard ?????
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
    /// ??????????????????
    /// </summary>
    public void RequestGuidance()
    {
        ResolveReferences();
        RequestGuidance(defaultContext);
    }

    /// <summary>
    /// ?????????????????Phase 5 ??????????
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
        StartCoroutine(SendGuidanceRequest(request));
    }

    /// <summary>
    /// ??????????? rejected ????????????
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
/// UnityEvent ?? guidance response??? Inspector ? bootstrap ???
/// </summary>
[Serializable]
public sealed class GuidanceResponseEvent : UnityEvent<GuidanceResponse>
{
}

/// <summary>
/// UnityEvent ?????????????
/// </summary>
[Serializable]
public sealed class StringEvent : UnityEvent<string>
{
}
