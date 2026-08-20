using TMPro;
using UnityEngine;

/// <summary>
/// 将实验语义状态映射为 Participant 头显 UI，并保证 Guidance 与 Safety 面板互斥。
/// 原始 verifier、网络和内部 ID 不得传入本控制器。
/// </summary>
public sealed class ParticipantUiController : MonoBehaviour
{
    [SerializeField] private CanvasGroup guidancePanel;
    [SerializeField] private CanvasGroup safetyPanel;
    [SerializeField] private TMP_Text narrationText;
    [SerializeField] private TMP_Text actionText;
    [SerializeField] private TMP_Text gazeText;
    [SerializeField] private TMP_Text safetyText;
    [Tooltip("显示 OURS Reset 期间语义干预话术的 TextMeshPro 文本控件。")]
    [SerializeField] private TMP_Text resetNarrationText;

    public bool SafetyVisible => safetyPanel != null && safetyPanel.alpha > 0.5f;
    /// <summary>当前安全面板是否正在显示本次 Reset 的 participant-facing HUD。</summary>
    public bool ResetHudVisible { get; private set; }

    private void Awake()
    {
        ShowWaiting();
    }

    public void Configure(
        CanvasGroup guidance,
        CanvasGroup safety,
        TMP_Text narration,
        TMP_Text action,
        TMP_Text gaze,
        TMP_Text safetyMessage)
    {
        guidancePanel = guidance;
        safetyPanel = safety;
        narrationText = narration;
        actionText = action;
        gazeText = gaze;
        safetyText = safetyMessage;
        ShowWaiting();
    }

    public void ShowGuidance()
    {
        ResetHudVisible = false;
        SetPanels(showGuidance: true, showSafety: false);
    }

    public void ShowWaiting() => ShowSafetyMessage("设备已连接，请等待实验开始");
    public void ShowStarting() => ShowSafetyMessage("实验即将开始，请保持原地");
    /// <summary>
    /// 显示本次任务的自然语言目标，并在首条 Guidance 返回前保持 SafetyPanel 可见。
    /// </summary>
    public void ShowTaskBriefing(string taskText)
    {
        ShowSafetyMessage(string.IsNullOrWhiteSpace(taskText)
            ? "请等待系统生成导览引导"
            : taskText);
    }

    public void ShowResearcherPaused() => ShowSafetyMessage("实验已暂停，请保持原地");
    public void ShowCriticalSafety() => ShowSafetyMessage("请立即停止移动，等待研究员指引");
    /// <summary>显示不暴露实验条件或内部风险原因的统一 Runtime Intervention 提示。</summary>
    public void ShowRuntimeIntervention() => ShowSafetyMessage("请停止移动，等待安全引导");
    /// <summary>显示自动安全转向正在执行，不要求研究员介入。</summary>
    public void ShowAutomaticReset() => ShowSafetyMessage("请停止移动并按安全提示原地转向");
    /// <summary>显示 Tracking 或临时校准状态正在自动恢复。</summary>
    public void ShowTrackingRecovery() => ShowSafetyMessage("请停止移动，正在恢复定位");
    /// <summary>根据冻结恢复计划显示明确的原地转向方向和大致角度。</summary>
    public void ShowRecoveryStarted(ResetEventInfo info)
    {
        if (info == null)
        {
            ShowSafetyMessage("请停止前进\n请原地转身", true);
            return;
        }

        var approximateDegrees = Mathf.Max(0, Mathf.RoundToInt(info.ApproximateTurnDegrees / 5f) * 5);
        if (info.RelativeYawDegrees < -0.1f)
        {
            ShowSafetyMessage($"请停止前进\n请原地向左转身 ←\n约 {approximateDegrees}°", true);
        }
        else if (info.RelativeYawDegrees > 0.1f)
        {
            ShowSafetyMessage($"请停止前进\n请原地向右转身 →\n约 {approximateDegrees}°", true);
        }
        else
        {
            ShowSafetyMessage("请停止前进\n请保持当前安全朝向", true);
        }
    }

    /// <summary>在不覆盖物理安全指令的前提下显示 Reset 期间的语义干预话术。</summary>
    public void ShowResetNarration(string narration)
    {
        if (!ResetHudVisible || resetNarrationText == null)
        {
            return;
        }

        resetNarrationText.text = string.IsNullOrWhiteSpace(narration) ? string.Empty : narration;
    }

    /// <summary>清理 Reset HUD；不会冻结 Camera、XR Tracking 或时间流逝。</summary>
    public void HideResetHud()
    {
        if (!ResetHudVisible)
        {
            return;
        }

        ResetHudVisible = false;
        if (safetyText != null)
        {
            safetyText.text = string.Empty;
        }

        ClearResetNarrationText();

        SetPanels(showGuidance: false, showSafety: false);
    }

    /// <summary>显示安全朝向已到达，系统正在复验并重新规划。</summary>
    public void ShowRecoveryReplanning() => ShowSafetyMessage("请保持朝向，正在重新规划");
    /// <summary>显示当前没有可执行候选，系统正在等待新物理事实重新规划。</summary>
    public void ShowReplanningHold() => ShowSafetyMessage("请保持当前位置，正在更新安全路线");
    /// <summary>显示后端暂时不可用，系统正在自动等待恢复。</summary>
    public void ShowBackendHold() => ShowSafetyMessage("系统连接暂时中断，请保持原地等待恢复");
    public void ShowSystemUnavailable() => ShowSafetyMessage("系统暂时无法继续，请保持原地");
    public void ShowCompleted() => ShowSafetyMessage("任务已完成，请保持原地");
    public void ShowEnded() => ShowSafetyMessage("实验已结束，请等待研究员");

    public void ClearGuidanceText()
    {
        if (narrationText != null) narrationText.text = string.Empty;
        if (actionText != null) actionText.text = string.Empty;
        if (gazeText != null) gazeText.text = string.Empty;
    }

    private void ShowSafetyMessage(string message, bool isResetHud = false)
    {
        ResetHudVisible = isResetHud;
        ClearGuidanceText();
        ClearResetNarrationText();
        if (safetyText != null)
        {
            safetyText.text = message;
        }

        SetPanels(showGuidance: false, showSafety: true);
    }

    private void SetPanels(bool showGuidance, bool showSafety)
    {
        // Safety 优先，避免两个面板因错误调用同时可见。
        if (showSafety)
        {
            showGuidance = false;
        }

        SetCanvasGroup(guidancePanel, showGuidance);
        SetCanvasGroup(safetyPanel, showSafety);
    }

    private void ClearResetNarrationText()
    {
        if (resetNarrationText != null)
        {
            resetNarrationText.text = string.Empty;
        }
    }

    private static void SetCanvasGroup(CanvasGroup group, bool visible)
    {
        if (group == null)
        {
            return;
        }

        group.alpha = visible ? 1f : 0f;
        group.interactable = false;
        group.blocksRaycasts = false;
    }
}
