using System;
using System.Collections.Generic;

/// <summary>
/// MainScene 显式引用校验的只读结果，不执行对象查找、创建或修复。
/// </summary>
public sealed class SceneReadinessReport
{
    private static readonly string[] EmptyReferences = Array.Empty<string>();

    /// <summary>
    /// 尚未执行场景校验时使用的空结果。
    /// </summary>
    public static SceneReadinessReport NotEvaluated { get; } = new SceneReadinessReport(EmptyReferences, false, "scene readiness not evaluated");

    /// <summary>
    /// 所有必要引用是否完整。
    /// </summary>
    public bool IsReady { get; }

    /// <summary>
    /// 缺失的 Inspector 引用名称。
    /// </summary>
    public IReadOnlyList<string> MissingReferences { get; }

    /// <summary>
    /// 可直接用于 Console 或研究员界面的错误摘要。
    /// </summary>
    public string ErrorSummary { get; }

    /// <summary>
    /// 根据缺失引用集合构造一次场景就绪校验结果。
    /// </summary>
    public SceneReadinessReport(IReadOnlyList<string> missingReferences)
        : this(
            missingReferences ?? EmptyReferences,
            missingReferences != null && missingReferences.Count == 0,
            BuildErrorSummary(missingReferences))
    {
    }

    private SceneReadinessReport(IReadOnlyList<string> missingReferences, bool isReady, string errorSummary)
    {
        MissingReferences = missingReferences;
        IsReady = isReady;
        ErrorSummary = errorSummary;
    }

    private static string BuildErrorSummary(IReadOnlyList<string> missingReferences)
    {
        if (missingReferences == null || missingReferences.Count == 0)
        {
            return string.Empty;
        }

        return "missing references: " + string.Join(", ", missingReferences);
    }
}
