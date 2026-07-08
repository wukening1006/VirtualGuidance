using UnityEngine;

/// <summary>
/// 中文说明：Guidance 的唯一执行入口，先检查 verifier，再分发给各表现层。
/// </summary>
public sealed class GuidanceExecutor : MonoBehaviour
{
    [Header("表现层控制器")]
    [SerializeField] private NarrationPlayer narrationPlayer;
    [SerializeField] private GazeCueController gazeCueController;
    [SerializeField] private MovementHintController movementHintController;
    [SerializeField] private GuideAvatarController guideAvatarController;
    [SerializeField] private ExhibitHighlightManager exhibitHighlightManager;
    [SerializeField] private RejectionPanel rejectionPanel;
    [SerializeField] private GuidanceDebugPanel guidanceDebugPanel;

    private void Awake()
    {
        ResolveReferences();
    }

    /// <summary>
    /// 中文说明：注入所有表现层引用，确保执行路径统一经过 verifier gate。
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
    /// 中文说明：应用一次后端 guidance；approved=false 时只显示拒绝原因，不执行导览表现。
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
    /// 中文说明：清除所有导览表现，供 rejected、retry 或 pause/reset 流程调用。
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
