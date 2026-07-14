using UnityEngine;

public sealed class PauseResetController : MonoBehaviour
{
    [SerializeField] private GuidanceExecutor guidanceExecutor;
    [SerializeField] private RejectionPanel rejectionPanel;
    [SerializeField] private RotationGainController rotationGainController;

    public bool IsPaused { get; private set; }

    public void Configure(GuidanceExecutor executor, RejectionPanel panel, RotationGainController gainController = null)
    {
        guidanceExecutor = executor;
        rejectionPanel = panel;
        rotationGainController = gainController;
    }

    public void PauseForReset(string reason)
    {
        IsPaused = true;
        rotationGainController?.StopGain();
        guidanceExecutor?.Clear();
        rejectionPanel?.Show(string.IsNullOrWhiteSpace(reason) ? "Reset required." : reason);
    }

    public void ResumeAfterReset()
    {
        IsPaused = false;
        rejectionPanel?.Hide();
    }
}
