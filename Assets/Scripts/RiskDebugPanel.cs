using System;
using System.Text;
using TMPro;
using UnityEngine;

/// <summary>
/// 中文说明：将 physical risk state 写入研究者调试面板，便于复现、录屏检查和 baseline 对比。
/// </summary>
public sealed class RiskDebugPanel : MonoBehaviour
{
    [Header("调试面板引用")]
    [Tooltip("提供当前 physical risk state 的计算器；未配置时会在场景中自动查找。")]
    [SerializeField] private PhysicalRiskEvaluator riskEvaluator;
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
            WriteState(riskEvaluator.CurrentRiskState);
        }
    }

    private void OnDisable()
    {
        if (riskEvaluator != null)
        {
            riskEvaluator.RiskStateChanged -= HandleRiskStateChanged;
        }
    }

    /// <summary>
    /// 中文说明：注入风险计算器和文本控件，支持由场景构建器或 Inspector 统一绑定引用。
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
        builder.Append("Forward Clearance: ").Append(state.clearance_forward.ToString("0.00")).AppendLine("m");
        builder.Append("Left Clearance: ").Append(state.clearance_left.ToString("0.00")).AppendLine("m");
        builder.Append("Right Clearance: ").Append(state.clearance_right.ToString("0.00")).AppendLine("m");
        builder.Append("Boundary Risk: ").AppendLine(EmptyFallback(state.boundary_risk));
        builder.Append("Reset Risk: ").AppendLine(EmptyFallback(state.reset_risk));
        builder.Append("Safe: ").AppendLine(JoinOrNone(state.safe_directions));
        builder.Append("Unsafe: ").AppendLine(JoinOrNone(state.unsafe_directions));
        builder.Append("Reset Required: ").Append(state.reset_required ? "true" : "false");

        riskText.text = builder.ToString();
    }

    private void ResolveReferences()
    {
        if (riskEvaluator == null)
        {
            // 调试面板允许手工拖拽，也允许场景构建脚本生成后自动补齐引用。
            riskEvaluator = FindObjectOfType<PhysicalRiskEvaluator>();
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

    private static string JoinOrNone(string[] values)
    {
        return values == null || values.Length == 0 ? "none" : string.Join(", ", values);
    }

    private static string EmptyFallback(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? "unknown" : value;
    }
}
