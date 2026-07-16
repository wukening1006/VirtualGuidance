using UnityEngine;

/// <summary>
/// 统一执行物理 reset 所需的暂停与恢复表现，供 RDW 和导览协调流程调用。
/// 暂停时停止旋转增益、清除 guidance 并显示原因，恢复时仅解除暂停提示。
/// </summary>
public sealed class PauseResetController : MonoBehaviour
{
    [SerializeField] private GuidanceExecutor guidanceExecutor;
    [SerializeField] private RejectionPanel rejectionPanel;
    [SerializeField] private RotationGainController rotationGainController;

    /// <summary>当前是否已因物理 reset 要求暂停导览。</summary>
    public bool IsPaused { get; private set; }

    /// <summary>注入暂停流程使用的导览表现、拒绝面板和可选旋转增益控制器。</summary>
    public void Configure(GuidanceExecutor executor, RejectionPanel panel, RotationGainController gainController = null)
    {
        if (executor != null) guidanceExecutor = executor;
        if (panel != null) rejectionPanel = panel;
        if (gainController != null) rotationGainController = gainController;
    }

    /// <summary>
    /// 立即停止可能改变用户朝向的表现并显示 reset 原因；重复调用会刷新提示。
    /// </summary>
    public void PauseForReset(string reason)
    {
        IsPaused = true;
        rotationGainController?.StopGain();
        guidanceExecutor?.Clear();
        rejectionPanel?.Show(string.IsNullOrWhiteSpace(reason) ? "Reset required." : reason);
    }

    /// <summary>
    /// 在外部确认 reset 完成后解除暂停并隐藏提示，不会自动重新请求 guidance。
    /// </summary>
    public void ResumeAfterReset()
    {
        IsPaused = false;
        rejectionPanel?.Hide();
    }
}
