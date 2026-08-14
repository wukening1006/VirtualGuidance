using System;
using System.Text;
using TMPro;
using UnityEngine;

/// <summary>
/// 将 physical risk state 写入研究者调试面板，便于复现、录屏检查和 baseline 对比。
/// </summary>
public sealed class RiskDebugPanel : MonoBehaviour
{
    [Header("调试面板引用")]
    [Tooltip("提供当前 physical risk state 的计算器；未配置时会在场景中自动查找。")]
    [SerializeField] private PhysicalRiskEvaluator riskEvaluator;
    [Tooltip("提供最近一次 Continuous Recovery Heading Search 诊断；未配置时会自动查找。")]
    [SerializeField] private GuidanceCoordinator guidanceCoordinator;
    [Tooltip("显示研究者调试信息的 TextMeshPro 文本控件。")]
    [SerializeField] private TMP_Text riskText;

    // 面板刷新频率较高，复用 StringBuilder 避免反复分配字符串缓冲。
    private readonly StringBuilder builder = new StringBuilder(320);

    private void Awake()
    {
        ResolveReferences();
    }

    private void OnEnable()
    {
        ResolveReferences();
        if (riskEvaluator != null)
        {
            riskEvaluator.RiskStateChanged += HandleRiskStateChanged;
        }
        if (guidanceCoordinator != null)
        {
            guidanceCoordinator.RecoverySearchCompleted += HandleRecoverySearchCompleted;
        }
        WriteState(riskEvaluator != null ? riskEvaluator.CurrentRiskState : null);
    }

    private void OnDisable()
    {
        if (riskEvaluator != null)
        {
            riskEvaluator.RiskStateChanged -= HandleRiskStateChanged;
        }
        if (guidanceCoordinator != null)
        {
            guidanceCoordinator.RecoverySearchCompleted -= HandleRecoverySearchCompleted;
        }
    }

    /// <summary>
    /// 注入风险计算器和文本控件，支持由场景构建器或 Inspector 统一绑定引用。
    /// </summary>
    /// <param name="evaluator">提供最新 physical risk state 的计算器。</param>
    /// <param name="text">用于显示研究者调试信息的 TMP 文本控件。</param>
    public void Configure(PhysicalRiskEvaluator evaluator, TMP_Text text)
    {
        if (riskEvaluator != null)
        {
            // 重新配置前先解除旧订阅，避免重复刷新或引用已替换对象。
            riskEvaluator.RiskStateChanged -= HandleRiskStateChanged;
        }

        riskEvaluator = evaluator;
        riskText = text;

        if (isActiveAndEnabled && riskEvaluator != null)
        {
            riskEvaluator.RiskStateChanged += HandleRiskStateChanged;
        }

        WriteState(riskEvaluator != null ? riskEvaluator.CurrentRiskState : null);
    }

    private void HandleRiskStateChanged(PhysicalRiskState state)
    {
        WriteState(state);
    }

    private void HandleRecoverySearchCompleted(RecoveryHeadingSearchResult _)
    {
        WriteState(riskEvaluator != null ? riskEvaluator.CurrentRiskState : null);
    }

    private void WriteState(PhysicalRiskState state)
    {
        if (riskText == null)
        {
            return;
        }

        if (state == null)
        {
            // 场景刚启动或配置未加载时显示等待状态，避免研究者误判为低风险。
            riskText.text = "Physical Space: loading\nRisk: waiting for state";
            return;
        }

        builder.Clear();
        builder.Append("Physical Space: ").AppendLine(EmptyFallback(state.physical_space));
        builder.Append("Physical Pose: (")
            .Append(state.user_physical_position.x.ToString("0.00")).Append(", ")
            .Append(state.user_physical_position.y.ToString("0.00")).AppendLine(")");
        builder.Append("Current Heading: ")
            .Append(state.current_heading.RelativeYawDegrees.ToString("0.###"))
            .Append(" deg, clearance=")
            .Append(state.current_heading.ClearanceMeters.ToString("0.00"))
            .Append("m, safety=").AppendLine(state.current_heading.Safety.ToString());
        builder.Append("Current Physical Forward Risk: ")
            .AppendLine(EmptyFallback(state.boundary_risk));
        builder.Append("Reset Risk: ").AppendLine(EmptyFallback(state.reset_risk));
        builder.Append("Reset Required: ").AppendLine(state.reset_required ? "true" : "false");
        AppendRecoverySearch(guidanceCoordinator != null
            ? guidanceCoordinator.LatestRecoverySearchResult
            : null);

        riskText.text = builder.ToString();
    }

    private void ResolveReferences()
    {
        if (riskEvaluator == null)
        {
            // 调试面板允许手工拖拽，也允许场景构建脚本生成后自动补齐引用。
            riskEvaluator = FindObjectOfType<PhysicalRiskEvaluator>();
        }

        if (guidanceCoordinator == null)
        {
            guidanceCoordinator = FindObjectOfType<GuidanceCoordinator>();
        }

        if (riskText == null)
        {
            var textObject = GameObject.Find("RiskDebugPanel_Text");
            if (textObject != null)
            {
                riskText = textObject.GetComponent<TMP_Text>();
            }
        }
    }

    private static string EmptyFallback(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? "unknown" : value;
    }

    private void AppendRecoverySearch(RecoveryHeadingSearchResult result)
    {
        if (result == null)
        {
            builder.Append("Recovery Search: none");
            return;
        }

        builder.Append("Recovery Search: ")
            .Append(result.AttemptedEvaluationCount)
            .Append(" attempted / ")
            .Append(result.SuccessfulEvaluationCount)
            .Append(" successful, ")
            .Append(result.Evaluations.Count)
            .Append(" search samples, resolution=")
            .Append(float.IsInfinity(result.CompletedCoverageResolutionDegrees)
                ? "none"
                : result.CompletedCoverageResolutionDegrees.ToString("0.###"))
            .Append(" deg, budget=")
            .AppendLine(result.BudgetExhausted ? "exhausted" : "available");

        if (result.Plan.IsSuccess)
        {
            builder.Append("Best Safe Heading: ")
                .Append(result.Plan.RelativeYawDegrees.ToString("0.###"))
                .Append(" deg, clearance=")
                .Append(result.Plan.ClearanceMeters.ToString("0.00"))
                .Append("m, score=")
                .Append(result.Plan.Score.ToString("0.###"));
            return;
        }

        builder.Append("Search Failure: ").Append(
            string.IsNullOrWhiteSpace(result.FailureReason)
                ? result.Plan.Failure.ToString()
                : result.FailureReason);
    }
}
