using UnityEngine;

/// <summary>
/// Guidance 的唯一执行入口，先检查 verifier，再分发给各表现层。
/// </summary>
public sealed class GuidanceExecutor : MonoBehaviour
{
    [Header("表现层控制器")]
    [Tooltip("显示后端 narration 字段的字幕控制器；未配置时自动查找。")]
    [SerializeField] private NarrationPlayer narrationPlayer;
    [Tooltip("显示 gaze_cue 弱提示的控制器；未配置时自动查找。")]
    [SerializeField] private GazeCueController gazeCueController;
    [Tooltip("显示 movement_instruction 身体动作提示的控制器；未配置时自动查找。")]
    [SerializeField] private MovementHintController movementHintController;
    [Tooltip("执行 avatar_action 导览员表现的控制器；未配置时自动查找。")]
    [SerializeField] private GuideAvatarController guideAvatarController;
    [Tooltip("根据 selected_target 高亮展品的控制器；未配置时自动查找。")]
    [SerializeField] private ExhibitHighlightManager exhibitHighlightManager;
    [Tooltip("显示 verifier 拒绝原因或请求错误的面板；未配置时自动查找。")]
    [SerializeField] private RejectionPanel rejectionPanel;
    [Tooltip("显示 guidance response 关键字段的研究者调试面板；未配置时自动查找。")]
    [SerializeField] private GuidanceDebugPanel guidanceDebugPanel;

    private void Awake()
    {
        ResolveReferences();
    }

    /// <summary>
    /// 注入所有表现层引用，确保执行路径统一经过 verifier gate。
    /// </summary>
    public void Configure(
        NarrationPlayer narration,
        GazeCueController gazeCue,
        MovementHintController movementHint,
        GuideAvatarController avatar,
        ExhibitHighlightManager highlight,
        RejectionPanel rejection,
        GuidanceDebugPanel debugPanel)
    {
        narrationPlayer = narration;
        gazeCueController = gazeCue;
        movementHintController = movementHint;
        guideAvatarController = avatar;
        exhibitHighlightManager = highlight;
        rejectionPanel = rejection;
        guidanceDebugPanel = debugPanel;
    }

    /// <summary>
    /// 应用一次后端 guidance；approved=false 时只显示拒绝原因，不执行导览表现。
    /// </summary>
    public void Apply(GuidanceResponse response)
    {
        ResolveReferences();
        guidanceDebugPanel?.Show(response);

        if (response == null)
        {
            Reject("Guidance response is null.");
            return;
        }

        if (response.verifier == null)
        {
            Reject("Guidance verifier result is missing.");
            return;
        }

        if (!response.verifier.approved)
        {
            Reject(response.verifier.reason);
            return;
        }

        rejectionPanel?.Hide();
        narrationPlayer?.Play(response.narration);
        guideAvatarController?.Apply(response.avatar_action, response.selected_target, response.gaze_target_id);
        movementHintController?.Show(response.movement_instruction);
        exhibitHighlightManager?.Highlight(response.selected_target);
        gazeCueController?.Show(response.gaze_cue, response.gaze_target_id);
    }

    /// <summary>
    /// 清除所有导览表现，供 rejected、retry 或 pause/reset 流程调用。
    /// </summary>
    public void Clear()
    {
        narrationPlayer?.Clear();
        gazeCueController?.Clear();
        movementHintController?.Clear();
        guideAvatarController?.Wait();
        exhibitHighlightManager?.Clear();
    }

    private void Reject(string reason)
    {
        Clear();
        rejectionPanel?.Show(string.IsNullOrWhiteSpace(reason) ? "Guidance rejected by verifier." : reason);
    }

    private void ResolveReferences()
    {
        if (narrationPlayer == null)
        {
            narrationPlayer = FindObjectOfType<NarrationPlayer>();
        }

        if (gazeCueController == null)
        {
            gazeCueController = FindObjectOfType<GazeCueController>();
        }

        if (movementHintController == null)
        {
            movementHintController = FindObjectOfType<MovementHintController>();
        }

        if (guideAvatarController == null)
        {
            guideAvatarController = FindObjectOfType<GuideAvatarController>();
        }

        if (exhibitHighlightManager == null)
        {
            exhibitHighlightManager = FindObjectOfType<ExhibitHighlightManager>();
        }

        if (rejectionPanel == null)
        {
            rejectionPanel = FindObjectOfType<RejectionPanel>();
        }

        if (guidanceDebugPanel == null)
        {
            guidanceDebugPanel = FindObjectOfType<GuidanceDebugPanel>();
        }
    }
}
