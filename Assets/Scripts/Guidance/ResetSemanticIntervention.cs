using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

/// <summary>
/// Reset 语义旁路读取的最小上下文；只包含稳定语义事实和去重信息。
/// </summary>
[Serializable]
public sealed class ResetSemanticContext
{
    /// <summary>当前实验会话的稳定标识。</summary>
    public string session_id;
    /// <summary>本次 Reset 的稳定标识。</summary>
    public string reset_id;
    /// <summary>由会话与 Reset 标识生成的确定性变化种子。</summary>
    public int variation_seed;
    /// <summary>当前展品稳定 ID。</summary>
    public string current_exhibit_id;
    /// <summary>当前展品显示名称。</summary>
    public string current_exhibit_name;
    /// <summary>Reset 后继续执行的目标稳定 ID。</summary>
    public string next_target_id;
    /// <summary>Reset 后继续执行的目标显示名称。</summary>
    public string next_target_name;
    /// <summary>当前导览主题。</summary>
    public string current_topic;
    /// <summary>Unity 从静态语义图选择的本地落地话术。</summary>
    public string semantic_hint;
    /// <summary>本地落地话术对应的稳定内容键。</summary>
    public string semantic_hint_key;
    /// <summary>最近展示过的导览或 Reset 话术。</summary>
    public string[] recent_narrations = Array.Empty<string>();
    /// <summary>最近展示过的 Reset 内容键。</summary>
    public string[] recent_content_keys = Array.Empty<string>();
}

/// <summary>后端返回的 Reset 语义干预文本；不包含任何物理控制字段。</summary>
[Serializable]
public sealed class ResetSemanticInterventionResponse
{
    /// <summary>Reset 期间播放的语义话术。</summary>
    public string narration;
    /// <summary>话术引用的稳定语义内容键。</summary>
    public string content_key;
}

/// <summary>Reset 语义请求/响应的严格 JSON 协议。</summary>
public static class ResetSemanticInterventionProtocol
{
    /// <summary>Reset 语义干预协议版本。</summary>
    public const string SchemaVersion = "reset-semantic-intervention-v2";

    private static readonly HashSet<string> ResponseFields = new HashSet<string>(StringComparer.Ordinal)
    {
        "narration",
        "content_key"
    };

    /// <summary>序列化最小 Reset 语义请求。</summary>
    public static string SerializeRequest(ResetSemanticContext context)
    {
        if (context == null) throw new ArgumentNullException(nameof(context));
        var payload = new JObject
        {
            ["planning_schema_version"] = SchemaVersion,
            ["session_id"] = context.session_id ?? string.Empty,
            ["reset_id"] = context.reset_id ?? string.Empty,
            ["variation_seed"] = Math.Max(0, context.variation_seed),
            ["current_exhibit_id"] = context.current_exhibit_id ?? string.Empty,
            ["current_exhibit_name"] = context.current_exhibit_name ?? string.Empty,
            ["next_target_id"] = context.next_target_id ?? string.Empty,
            ["next_target_name"] = context.next_target_name ?? string.Empty,
            ["current_topic"] = context.current_topic ?? string.Empty,
            ["semantic_hint"] = context.semantic_hint ?? string.Empty,
            ["semantic_hint_key"] = context.semantic_hint_key ?? string.Empty,
            ["recent_narrations"] = new JArray(context.recent_narrations ?? Array.Empty<string>()),
            ["recent_content_keys"] = new JArray(context.recent_content_keys ?? Array.Empty<string>())
        };
        return payload.ToString(Formatting.None);
    }

    /// <summary>解析严格的语义文本与内容键响应，拒绝物理控制字段和未知属性。</summary>
    public static bool TryParseResponse(
        string json,
        out ResetSemanticInterventionResponse response,
        out string failureReason)
    {
        response = null;
        failureReason = string.Empty;
        if (string.IsNullOrWhiteSpace(json))
        {
            failureReason = "reset_semantic_response_missing";
            return false;
        }

        try
        {
            var root = JObject.Parse(json);
            foreach (var field in ResponseFields)
            {
                if (root.Property(field) == null)
                {
                    failureReason = "reset_semantic_response_field_missing:" + field;
                    return false;
                }
            }

            foreach (var property in root.Properties())
            {
                if (!ResponseFields.Contains(property.Name))
                {
                    failureReason = "reset_semantic_response_unexpected_field:" + property.Name;
                    return false;
                }
            }

            if (root["narration"]?.Type != JTokenType.String ||
                root["content_key"]?.Type != JTokenType.String)
            {
                failureReason = "reset_semantic_response_field_type_invalid";
                return false;
            }

            response = root.ToObject<ResetSemanticInterventionResponse>();
        }
        catch (JsonException exception)
        {
            failureReason = "reset_semantic_response_invalid_json:" + exception.Message;
            return false;
        }

        if (response == null || string.IsNullOrWhiteSpace(response.narration))
        {
            failureReason = "reset_semantic_response_narration_missing";
            return false;
        }

        if (string.IsNullOrWhiteSpace(response.content_key))
        {
            failureReason = "reset_semantic_response_content_key_missing";
            return false;
        }

        response.narration = response.narration.Trim();
        response.content_key = response.content_key.Trim();
        return true;
    }
}

/// <summary>
/// 监听 Reset 生命周期并旁路生成语义话术；任何网络故障都回退为普通物理 Reset。
/// </summary>
[AddComponentMenu("VirtualGuidance/导览/Reset 语义干预 Manager")]
[DisallowMultipleComponent]
public sealed class ResetSemanticInterventionManager : MonoBehaviour
{
    private const int RecentHistoryLimit = 4;

    [Header("Reset 语义干预")]
    [SerializeField, Tooltip("是否启用 OURS 的 Reset 语义旁路。")]
    private bool interventionEnabled = true;

    private PauseResetController resetController;
    private GuidanceCoordinator guidanceCoordinator;
    private IResetSemanticInterventionClient client;
    private NarrationPlayer narrationPlayer;
    private ExperimentLogger experimentLogger;
    private readonly Queue<string> recentNarrations = new Queue<string>();
    private readonly Queue<string> recentContentKeys = new Queue<string>();
    private bool resetActive;

    /// <summary>当前 Reset 语义旁路是否正在等待或展示话术。</summary>
    public bool IsResetInterventionActive => resetActive;
    /// <summary>本地或后端 Reset 语义话术可交给 Participant UI 显示时触发。</summary>
    public event Action<string> ResetSemanticNarrationReceived;

    /// <summary>仅当 Inspector 开关启用且实验 Profile 为 OURS 语义 Reset 时返回 true。</summary>
    public bool IsEnabledForProfile(ExperimentProfile profile)
    {
        return interventionEnabled &&
               profile != null &&
               profile.resetMode == ExperimentResetMode.SemanticContextualized;
    }

    /// <summary>注入 Reset、上下文、传输与字幕依赖。</summary>
    public void Configure(
        PauseResetController controller,
        GuidanceCoordinator coordinator,
        IResetSemanticInterventionClient interventionClient,
        NarrationPlayer player,
        ExperimentLogger logger = null,
        bool enabled = true)
    {
        Unbind();
        resetController = controller;
        guidanceCoordinator = coordinator;
        client = interventionClient;
        narrationPlayer = player;
        experimentLogger = logger;
        interventionEnabled = enabled;
        Bind();
    }

    private void OnEnable()
    {
        Bind();
    }

    private void OnDisable()
    {
        Unbind();
        StopIntervention();
    }

    private void Bind()
    {
        if (resetController != null)
        {
            resetController.ResetStarted -= HandleResetStarted;
            resetController.ResetStarted += HandleResetStarted;
            resetController.ResetCompleted -= HandleResetCompleted;
            resetController.ResetCompleted += HandleResetCompleted;
            resetController.StateChanged -= HandleResetStateChanged;
            resetController.StateChanged += HandleResetStateChanged;
        }

        if (client != null)
        {
            client.ResetSemanticInterventionReceived -= HandleInterventionReceived;
            client.ResetSemanticInterventionReceived += HandleInterventionReceived;
            client.ResetSemanticInterventionRequestFailed -= HandleInterventionFailed;
            client.ResetSemanticInterventionRequestFailed += HandleInterventionFailed;
        }
    }

    private void Unbind()
    {
        if (resetController != null)
        {
            resetController.ResetStarted -= HandleResetStarted;
            resetController.ResetCompleted -= HandleResetCompleted;
            resetController.StateChanged -= HandleResetStateChanged;
        }

        if (client != null)
        {
            client.ResetSemanticInterventionReceived -= HandleInterventionReceived;
            client.ResetSemanticInterventionRequestFailed -= HandleInterventionFailed;
        }
    }

    private void HandleResetStarted(ResetEventInfo info)
    {
        StopIntervention();
        if (!IsEnabledForProfile(guidanceCoordinator?.CurrentExperimentProfile) ||
            !HasValidSemanticReset(info))
        {
            return;
        }

        var context = guidanceCoordinator.CreateResetSemanticContext(
            info,
            recentNarrations.ToArray(),
            recentContentKeys.ToArray());
        if (context == null || string.IsNullOrWhiteSpace(context.semantic_hint) ||
            string.IsNullOrWhiteSpace(context.semantic_hint_key))
        {
            return;
        }

        resetActive = true;
        PresentNarration(context.semantic_hint, context.semantic_hint_key, "local");
        context.recent_narrations = AppendRequestHistory(
            context.recent_narrations,
            context.semantic_hint);
        context.recent_content_keys = AppendRequestHistory(
            context.recent_content_keys,
            context.semantic_hint_key);
        experimentLogger?.LogStatus(
            "reset_semantic_intervention_requested",
            null,
            guidanceCoordinator.RuntimeState,
            null,
            $"reset_id={context.reset_id}; local_content_key={context.semantic_hint_key}");
        client?.RequestResetSemanticIntervention(context);
    }

    private void HandleInterventionReceived(ResetSemanticInterventionResponse response)
    {
        if (!resetActive || response == null || string.IsNullOrWhiteSpace(response.narration) ||
            resetController == null || !resetController.IsActive)
        {
            return;
        }

        PresentNarration(response.narration, response.content_key, "backend");
    }

    private void HandleInterventionFailed(string reason)
    {
        if (!resetActive)
        {
            return;
        }

        experimentLogger?.LogStatus(
            "reset_semantic_intervention_failed",
            null,
            guidanceCoordinator != null ? guidanceCoordinator.RuntimeState : null,
            null,
            string.IsNullOrWhiteSpace(reason) ? "ordinary reset fallback" : reason);
        // 保留已经展示的本地语义回退，直到本次 Reset 正常结束。
    }

    private void HandleResetCompleted(ResetEventInfo _)
    {
        StopIntervention();
    }

    private void HandleResetStateChanged(ResetLifecycleState state)
    {
        if (state == ResetLifecycleState.Cancelled || state == ResetLifecycleState.Inactive)
        {
            StopIntervention();
        }
    }

    private void StopIntervention()
    {
        resetActive = false;
        client?.CancelPendingResetSemanticIntervention();
        narrationPlayer?.ClearReset();
    }

    private static bool HasValidSemanticReset(ResetEventInfo info)
    {
        return info != null &&
               !string.IsNullOrWhiteSpace(info.ResetId) &&
               info.RecoveryPlan != null &&
               info.RecoveryPlan.IsSuccess &&
               Mathf.Abs(info.RelativeYawDegrees) > 0.1f;
    }

    private void PresentNarration(string narration, string contentKey, string source)
    {
        var normalizedNarration = narration?.Trim();
        var normalizedKey = contentKey?.Trim();
        if (!resetActive || string.IsNullOrWhiteSpace(normalizedNarration) ||
            string.IsNullOrWhiteSpace(normalizedKey) ||
            recentNarrations.Contains(normalizedNarration) ||
            recentContentKeys.Contains(normalizedKey))
        {
            return;
        }

        Remember(recentNarrations, normalizedNarration);
        Remember(recentContentKeys, normalizedKey);
        narrationPlayer?.PlayReset(normalizedNarration);
        ResetSemanticNarrationReceived?.Invoke(normalizedNarration);
        experimentLogger?.LogStatus(
            "reset_semantic_intervention_presented",
            null,
            guidanceCoordinator != null ? guidanceCoordinator.RuntimeState : null,
            null,
            $"source={source}; content_key={normalizedKey}");
    }

    private static void Remember(Queue<string> history, string value)
    {
        history.Enqueue(value);
        while (history.Count > RecentHistoryLimit)
        {
            history.Dequeue();
        }
    }

    private static string[] AppendRequestHistory(string[] history, string value)
    {
        const int RequestHistoryLimit = 6;
        var merged = new List<string>();
        if (history != null)
        {
            foreach (var item in history)
            {
                if (!string.IsNullOrWhiteSpace(item) && !merged.Contains(item)) merged.Add(item);
            }
        }

        if (!string.IsNullOrWhiteSpace(value) && !merged.Contains(value)) merged.Add(value);
        if (merged.Count > RequestHistoryLimit)
        {
            merged.RemoveRange(0, merged.Count - RequestHistoryLimit);
        }

        return merged.ToArray();
    }
}
