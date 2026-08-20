using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

/// <summary>Reset 期间的语义干预强度；只由 Unity 根据 Reset 转角确定。</summary>
public enum ResetSemanticInterventionLevel
{
    /// <summary>短 Reset 使用的轻量补充。</summary>
    Low,
    /// <summary>中等 Reset 使用的背景与细节延伸。</summary>
    Medium,
    /// <summary>大角度 Reset 使用的故事、悬念与叙事过渡。</summary>
    High
}

/// <summary>
/// Reset 语义旁路读取的最小上下文；不包含 Physical Safety、Boundary 或 Gain 事实。
/// </summary>
[Serializable]
public sealed class ResetSemanticContext
{
    /// <summary>当前展品标识与名称。</summary>
    public string current_exhibit;
    /// <summary>当前导览主题。</summary>
    public string current_topic;
    /// <summary>Reset 后继续执行的目标标识与名称。</summary>
    public string next_target;
    /// <summary>Reset 前最近一次导览话术。</summary>
    public string recent_narration;
    /// <summary>本次 Reset 的相对转角，单位：度。</summary>
    public float reset_angle;
    /// <summary>触发 Reset 的稳定原因。</summary>
    public string reset_reason;
}

/// <summary>后端返回的 Reset 语义干预文本；不包含任何物理控制字段。</summary>
[Serializable]
public sealed class ResetSemanticInterventionResponse
{
    /// <summary>后端返回的文本等级；Unity 不使用它决定干预等级。</summary>
    public string intervention_level;
    /// <summary>Reset 期间播放的语义话术。</summary>
    public string narration;
}

/// <summary>Reset 语义请求/响应的严格 JSON 协议。</summary>
public static class ResetSemanticInterventionProtocol
{
    /// <summary>Reset 语义干预协议版本。</summary>
    public const string SchemaVersion = "reset-semantic-intervention-v1";

    private static readonly HashSet<string> ResponseFields = new HashSet<string>(StringComparer.Ordinal)
    {
        "intervention_level",
        "narration"
    };

    /// <summary>序列化最小 Reset 语义请求。</summary>
    public static string SerializeRequest(ResetSemanticContext context)
    {
        if (context == null) throw new ArgumentNullException(nameof(context));
        var payload = new JObject
        {
            ["planning_schema_version"] = SchemaVersion,
            ["current_exhibit"] = context.current_exhibit ?? string.Empty,
            ["current_topic"] = context.current_topic ?? string.Empty,
            ["next_target"] = context.next_target ?? string.Empty,
            ["recent_narration"] = context.recent_narration ?? string.Empty,
            ["reset_angle"] = Mathf.Clamp(context.reset_angle, 0f, 360f),
            ["reset_reason"] = context.reset_reason ?? string.Empty
        };
        return payload.ToString(Formatting.None);
    }

    /// <summary>解析严格的两字段响应，拒绝物理控制字段和未知属性。</summary>
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

            if (root["intervention_level"]?.Type != JTokenType.String ||
                root["narration"]?.Type != JTokenType.String)
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

        if (!Enum.TryParse(response.intervention_level, true, out ResetSemanticInterventionLevel level) ||
            !Enum.IsDefined(typeof(ResetSemanticInterventionLevel), level))
        {
            failureReason = "reset_semantic_response_level_invalid";
            return false;
        }

        response.intervention_level = level.ToString().ToLowerInvariant();
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
    [Header("Reset 语义干预")]
    [SerializeField, Tooltip("是否启用 OURS 的 Reset 语义旁路。")]
    private bool interventionEnabled = true;
    [SerializeField, Range(0f, 180f), Tooltip("Medium 等级下限，单位：度。")]
    private float mediumThresholdDegrees = 60f;
    [SerializeField, Range(0f, 360f), Tooltip("High 等级下限，单位：度。")]
    private float highThresholdDegrees = 120f;

    private PauseResetController resetController;
    private GuidanceCoordinator guidanceCoordinator;
    private IResetSemanticInterventionClient client;
    private NarrationPlayer narrationPlayer;
    private ExperimentLogger experimentLogger;
    private bool resetActive;

    /// <summary>当前 Reset 语义旁路是否正在等待或展示话术。</summary>
    public bool IsResetInterventionActive => resetActive;
    /// <summary>Reset 语义话术已通过协议校验并可交给 Participant UI 显示时触发。</summary>
    public event Action<string> ResetSemanticNarrationReceived;
    /// <summary>最近一次 Reset 按转角确定的等级。</summary>
    public ResetSemanticInterventionLevel CurrentLevel { get; private set; }
    /// <summary>Medium 阈值，单位：度。</summary>
    public float MediumThresholdDegrees => Mathf.Clamp(mediumThresholdDegrees, 0f, 180f);
    /// <summary>High 阈值，单位：度。</summary>
    public float HighThresholdDegrees => Mathf.Clamp(highThresholdDegrees, MediumThresholdDegrees, 360f);

    /// <summary>注入 Reset、上下文、传输与字幕依赖。</summary>
    public void Configure(
        PauseResetController controller,
        GuidanceCoordinator coordinator,
        IResetSemanticInterventionClient interventionClient,
        NarrationPlayer player,
        ExperimentLogger logger = null,
        bool enabled = true,
        float mediumThresholdDegrees = 60f,
        float highThresholdDegrees = 120f)
    {
        Unbind();
        resetController = controller;
        guidanceCoordinator = coordinator;
        client = interventionClient;
        narrationPlayer = player;
        experimentLogger = logger;
        interventionEnabled = enabled;
        this.mediumThresholdDegrees = mediumThresholdDegrees;
        this.highThresholdDegrees = highThresholdDegrees;
        Bind();
    }

    /// <summary>按 Reset 相对转角确定干预等级；不读取或修改安全状态。</summary>
    public ResetSemanticInterventionLevel Classify(float resetAngleDegrees)
    {
        var angle = Mathf.Abs(Mathf.Clamp(resetAngleDegrees, 0f, 360f));
        return angle >= HighThresholdDegrees
            ? ResetSemanticInterventionLevel.High
            : angle >= MediumThresholdDegrees
                ? ResetSemanticInterventionLevel.Medium
                : ResetSemanticInterventionLevel.Low;
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
        if (!interventionEnabled ||
            guidanceCoordinator?.CurrentExperimentProfile?.plannerMode != ExperimentPlannerMode.RdwGroundedTopK)
        {
            return;
        }

        var context = guidanceCoordinator.CreateResetSemanticContext(info);
        if (context == null || client == null)
        {
            return;
        }

        CurrentLevel = Classify(context.reset_angle);
        resetActive = true;
        experimentLogger?.LogStatus(
            "reset_semantic_intervention_requested",
            null,
            guidanceCoordinator.RuntimeState,
            null,
            $"level={CurrentLevel.ToString().ToLowerInvariant()}; angle={context.reset_angle:F1} deg");
        client.RequestResetSemanticIntervention(context);
    }

    private void HandleInterventionReceived(ResetSemanticInterventionResponse response)
    {
        if (!resetActive || response == null || string.IsNullOrWhiteSpace(response.narration) ||
            resetController == null || !resetController.IsActive)
        {
            return;
        }

        // 等级由本地转角确定；后端返回的等级只保留为协议字段，不参与控制。
        narrationPlayer?.PlayReset(response.narration);
        ResetSemanticNarrationReceived?.Invoke(response.narration);
        experimentLogger?.LogStatus(
            "reset_semantic_intervention_received",
            null,
            guidanceCoordinator != null ? guidanceCoordinator.RuntimeState : null,
            null,
            $"level={CurrentLevel.ToString().ToLowerInvariant()}");
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
        resetActive = false;
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
}
