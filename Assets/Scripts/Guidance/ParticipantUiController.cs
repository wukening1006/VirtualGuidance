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

    public bool SafetyVisible => safetyPanel != null && safetyPanel.alpha > 0.5f;

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
        SetPanels(showGuidance: true, showSafety: false);
    }

    public void ShowWaiting() => ShowSafetyMessage("设备已连接，请等待实验开始");
    public void ShowStarting() => ShowSafetyMessage("实验即将开始，请保持原地");
    public void ShowResearcherPaused() => ShowSafetyMessage("实验已暂停，请保持原地");
    public void ShowCriticalSafety() => ShowSafetyMessage("请立即停止移动，等待研究员指引");
    public void ShowUnsafeDirection() => ShowSafetyMessage("请停止前进");
    public void ShowSystemUnavailable() => ShowSafetyMessage("系统暂时无法继续，请保持原地");
    public void ShowCompleted() => ShowSafetyMessage("任务已完成，请保持原地");
    public void ShowEnded() => ShowSafetyMessage("实验已结束，请等待研究员");

    public void ClearGuidanceText()
    {
        if (narrationText != null) narrationText.text = string.Empty;
        if (actionText != null) actionText.text = string.Empty;
        if (gazeText != null) gazeText.text = string.Empty;
    }

    private void ShowSafetyMessage(string message)
    {
        ClearGuidanceText();
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
