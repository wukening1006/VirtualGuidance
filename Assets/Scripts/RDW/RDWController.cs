using UnityEngine;

public sealed class RDWController : MonoBehaviour
{
    [SerializeField] private PhysicalRiskEvaluator riskEvaluator;
    [SerializeField] private PauseResetController pauseResetController;
    [SerializeField] private bool pauseImmediatelyOnCriticalReset = true;

    private void OnEnable()
    {
        ResolveReferences();
        if (riskEvaluator != null)
        {
            riskEvaluator.RiskStateChanged += ApplyRiskState;
        }
    }

    private void OnDisable()
    {
        if (riskEvaluator != null)
        {
            riskEvaluator.RiskStateChanged -= ApplyRiskState;
        }
    }

    public void Configure(PhysicalRiskEvaluator evaluator, PauseResetController resetController)
    {
        if (riskEvaluator != null)
        {
            riskEvaluator.RiskStateChanged -= ApplyRiskState;
        }

        riskEvaluator = evaluator;
        pauseResetController = resetController;

        if (isActiveAndEnabled && riskEvaluator != null)
        {
            riskEvaluator.RiskStateChanged += ApplyRiskState;
        }
    }

    public void ApplyRiskState(PhysicalRiskState state)
    {
        if (!pauseImmediatelyOnCriticalReset || state == null || !state.reset_required)
        {
            return;
        }

        pauseResetController?.PauseForReset("All physical directions are unsafe; pause and reset before continuing.");
    }

    private void ResolveReferences()
    {
        if (riskEvaluator == null)
        {
            riskEvaluator = FindObjectOfType<PhysicalRiskEvaluator>();
        }

        if (pauseResetController == null)
        {
            pauseResetController = FindObjectOfType<PauseResetController>();
        }
    }
}
