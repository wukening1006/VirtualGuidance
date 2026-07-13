using UnityEngine;

public sealed class PauseResetController : MonoBehaviour
{
    [SerializeField] private GuidanceExecutor guidanceExecutor;
    [SerializeField] private RejectionPanel rejectionPanel;

    public bool IsPaused { get; private set; }

    public void Configure(GuidanceExecutor executor, RejectionPanel panel)
    {
        guidanceExecutor = executor;
        rejectionPanel = panel;
    }

    public void PauseForReset(string reason)
    {
        IsPaused = true;
        guidanceExecutor?.Clear();
        rejectionPanel?.Show(string.IsNullOrWhiteSpace(reason) ? "Reset required." : reason);
    }

    public void ResumeAfterReset()
    {
        IsPaused = false;
        rejectionPanel?.Hide();
    }
}
