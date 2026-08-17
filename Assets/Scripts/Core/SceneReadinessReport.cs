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
    public static SceneReadinessReport NotEvaluated { get; } = new SceneReadinessReport(
        EmptyReferences, false, "scene readiness not evaluated", false, false, false, false, false);

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

    /// <summary>语义配置是否已从 Build-safe TextAsset 加载。</summary>
    public bool SemanticConfigLoaded { get; }
    /// <summary>语义 schema 是否通过严格校验。</summary>
    public bool SemanticSchemaValid { get; }
    /// <summary>语义图及其关系是否有效。</summary>
    public bool SemanticGraphValid { get; }
    /// <summary>MainScene Anchor 是否与 JSON 一一绑定。</summary>
    public bool SceneBindingValid { get; }
    /// <summary>任务引用是否全部存在于语义图。</summary>
    public bool TaskReferencesValid { get; }
    /// <summary>正式会话是否满足完整语义就绪门禁。</summary>
    public bool SemanticReady => SemanticConfigLoaded && SemanticSchemaValid && SemanticGraphValid &&
                                 SceneBindingValid && TaskReferencesValid;

    /// <summary>
    /// 根据缺失引用集合构造一次场景就绪校验结果。
    /// </summary>
    public SceneReadinessReport(IReadOnlyList<string> missingReferences)
        : this(
            missingReferences ?? EmptyReferences,
            missingReferences != null && missingReferences.Count == 0,
            BuildErrorSummary(missingReferences),
            false, false, false, false, false)
    {
    }

    /// <summary>构造包含语义就绪状态的完整启动报告。</summary>
    public SceneReadinessReport(
        IReadOnlyList<string> missingReferences,
        bool semanticConfigLoaded,
        bool semanticSchemaValid,
        bool semanticGraphValid,
        bool sceneBindingValid,
        bool taskReferencesValid)
        : this(
            missingReferences ?? EmptyReferences,
            missingReferences != null && missingReferences.Count == 0,
            BuildErrorSummary(missingReferences),
            semanticConfigLoaded,
            semanticSchemaValid,
            semanticGraphValid,
            sceneBindingValid,
            taskReferencesValid)
    {
    }

    private SceneReadinessReport(
        IReadOnlyList<string> missingReferences,
        bool isReady,
        string errorSummary,
        bool semanticConfigLoaded,
        bool semanticSchemaValid,
        bool semanticGraphValid,
        bool sceneBindingValid,
        bool taskReferencesValid)
    {
        MissingReferences = missingReferences;
        IsReady = isReady;
        ErrorSummary = errorSummary;
        SemanticConfigLoaded = semanticConfigLoaded;
        SemanticSchemaValid = semanticSchemaValid;
        SemanticGraphValid = semanticGraphValid;
        SceneBindingValid = sceneBindingValid;
        TaskReferencesValid = taskReferencesValid;
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
