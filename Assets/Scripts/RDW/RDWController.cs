using UnityEngine;

/// <summary>
/// 监听确定性物理风险状态，并在所有自然方向不可用时触发统一暂停 reset 流程。
/// 风险输入来自 PhysicalRiskEvaluator，控制结果交给 PauseResetController。
/// </summary>
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
            // 风险事件可能持续刷新，启用期间只保留一个订阅以避免重复触发暂停表现。
            riskEvaluator.RiskStateChanged += ApplyRiskState;
        }
    }

    private void OnDisable()
    {
        if (riskEvaluator != null)
        {
            // 组件禁用后必须解绑，避免失效控制器继续响应风险事件。
            riskEvaluator.RiskStateChanged -= ApplyRiskState;
        }
    }

    /// <summary>
    /// 替换风险与 reset 依赖，并在组件启用时将事件订阅迁移到新计算器。
    /// </summary>
    public void Configure(PhysicalRiskEvaluator evaluator, PauseResetController resetController)
    {
        var nextEvaluator = evaluator != null ? evaluator : riskEvaluator;
        if (riskEvaluator != null)
        {
            riskEvaluator.RiskStateChanged -= ApplyRiskState;
        }

        riskEvaluator = nextEvaluator;
        if (resetController != null) pauseResetController = resetController;

        if (isActiveAndEnabled && riskEvaluator != null)
        {
            riskEvaluator.RiskStateChanged += ApplyRiskState;
        }
    }

    /// <summary>
    /// 将 critical reset 风险转换为暂停操作；普通方向风险由 guidance 选择与校验处理。
    /// </summary>
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
