using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>为连续恢复搜索提供任意 Physical Heading 的权威精确评估。</summary>
public interface IPhysicalHeadingEvaluator
{
    /// <summary>在指定可信风险快照上评估一条绝对 Physical Room 朝向。</summary>
    bool TryEvaluateAbsoluteHeading(
        PhysicalRiskState riskState,
        Vector2 absolutePhysicalForward,
        out HeadingSample sample);
}

/// <summary>表示连续恢复航向搜索失败的明确原因。</summary>
public enum RecoveryHeadingFailure
{
    /// <summary>搜索成功。</summary>
    None,
    /// <summary>风险快照、评估器或搜索配置无效。</summary>
    InvalidSearchContext,
    /// <summary>精确方向评估无法产生任何可信样本。</summary>
    EvaluationUnavailable,
    /// <summary>在当前全角域搜索分辨率和预算下未发现 Safe Heading。</summary>
    NoAcceptableHeading,
    /// <summary>最佳候选在独立精确终验时不再为 Safe。</summary>
    FinalValidationFailed
}

/// <summary>配置全角域分层恢复航向搜索及 Safe candidate 评分。</summary>
[Serializable]
public sealed class RecoveryHeadingSearchSettings
{
    [Tooltip("全角域第一层采样步长，单位：度。")]
    [Min(1f)] public float coarseStepDegrees = 15f;
    [Tooltip("全角域覆盖停止时的最小角分辨率，单位：度。")]
    [Min(0.1f)] public float minimumResolutionDegrees = 1f;
    [Tooltip("每层步长的缩小倍率；每一层仍覆盖完整 360 度。")]
    [Min(1.1f)] public float refinementFactor = 3f;
    [Tooltip("单次搜索允许执行的最大精确方向评估次数。")]
    [Min(2)] public int maximumEvaluations = 720;
    [Tooltip("clearance 归一化距离，单位：米。")]
    [Min(0.01f)] public float clearanceNormalizationMeters = 3f;
    [Tooltip("Safe candidate 的有效净空评分权重。")]
    [Min(0f)] public float clearanceWeight = 1f;
    [Tooltip("Safe candidate 的未来可行走距离评分权重。")]
    [Min(0f)] public float futureDistanceWeight = 0.5f;
    [Tooltip("Safe candidate 的 Physical 原地转角成本权重。")]
    [Min(0f)] public float turnCostWeight = 0.35f;

    /// <summary>创建经过约束的独立配置副本，避免搜索期间 Inspector 值变化。</summary>
    public RecoveryHeadingSearchSettings SanitizedCopy()
    {
        return new RecoveryHeadingSearchSettings
        {
            coarseStepDegrees = Mathf.Clamp(coarseStepDegrees, 1f, 180f),
            minimumResolutionDegrees = Mathf.Clamp(minimumResolutionDegrees, 0.1f, 180f),
            refinementFactor = Mathf.Max(1.1f, refinementFactor),
            maximumEvaluations = Mathf.Max(2, maximumEvaluations),
            clearanceNormalizationMeters = Mathf.Max(0.01f, clearanceNormalizationMeters),
            clearanceWeight = Mathf.Max(0f, clearanceWeight),
            futureDistanceWeight = Mathf.Max(0f, futureDistanceWeight),
            turnCostWeight = Mathf.Max(0f, turnCostWeight)
        };
    }
}

/// <summary>保存一次不可变的恢复航向决策；Physical Forward 在恢复期间保持冻结。</summary>
public sealed class RecoveryHeadingPlan
{
    /// <summary>规划是否成功。</summary>
    public bool IsSuccess => Failure == RecoveryHeadingFailure.None;
    /// <summary>规划失败原因。</summary>
    public RecoveryHeadingFailure Failure { get; }
    /// <summary>相对搜索起始 Physical Forward 的有符号偏航角，单位：度。</summary>
    public float RelativeYawDegrees { get; }
    /// <summary>冻结的 Physical Room 目标朝向。</summary>
    public Vector2 SafePhysicalForward { get; }
    /// <summary>终验时该朝向的有效走廊净空，单位：米。</summary>
    public float ClearanceMeters { get; }
    /// <summary>终验时该朝向的安全等级。</summary>
    public HeadingSafety Safety { get; }
    /// <summary>该朝向可继续行走的保守距离，单位：米。</summary>
    public float FutureWalkableDistanceMeters { get; }
    /// <summary>从搜索起始朝向转到目标朝向的物理转角，单位：度。</summary>
    public float PhysicalTurnDegrees { get; }
    /// <summary>用于诊断选择结果的综合评分。</summary>
    public float Score { get; }

    private RecoveryHeadingPlan(RecoveryHeadingFailure failure)
    {
        Failure = failure == RecoveryHeadingFailure.None
            ? RecoveryHeadingFailure.InvalidSearchContext
            : failure;
        RelativeYawDegrees = 0f;
        SafePhysicalForward = Vector2.up;
        ClearanceMeters = 0f;
        Safety = HeadingSafety.Critical;
        FutureWalkableDistanceMeters = 0f;
        PhysicalTurnDegrees = 0f;
        Score = float.NegativeInfinity;
    }

    /// <summary>使用经过独立终验的 Safe Heading 创建冻结恢复计划。</summary>
    public RecoveryHeadingPlan(HeadingSample sample, float score)
    {
        Failure = RecoveryHeadingFailure.None;
        RelativeYawDegrees = Mathf.DeltaAngle(0f, sample.RelativeYawDegrees);
        SafePhysicalForward = sample.PhysicalForward;
        ClearanceMeters = sample.ClearanceMeters;
        Safety = sample.Safety;
        FutureWalkableDistanceMeters = sample.ClearanceMeters;
        PhysicalTurnDegrees = Mathf.Abs(RelativeYawDegrees);
        Score = score;
    }

    /// <summary>创建一条包含明确原因的失败结果。</summary>
    public static RecoveryHeadingPlan Failed(RecoveryHeadingFailure failure)
    {
        return new RecoveryHeadingPlan(failure);
    }
}

/// <summary>保存一次连续恢复搜索的完整只读诊断结果。</summary>
public sealed class RecoveryHeadingSearchResult
{
    /// <summary>搜索最终生成的成功或失败计划。</summary>
    public RecoveryHeadingPlan Plan { get; }
    /// <summary>本次搜索实际查询的唯一角度样本。</summary>
    public IReadOnlyList<HeadingSample> Evaluations { get; }
    /// <summary>本次搜索调用精确评估器的总次数，包含失败查询和独立终验。</summary>
    public int AttemptedEvaluationCount { get; }
    /// <summary>本次搜索成功取得可信样本的评估次数，包含独立终验。</summary>
    public int SuccessfulEvaluationCount { get; }
    /// <summary>完整覆盖全角域后达到的最细角分辨率，单位：度。</summary>
    public float CompletedCoverageResolutionDegrees { get; }
    /// <summary>搜索是否因达到评估预算而停止。</summary>
    public bool BudgetExhausted { get; }
    /// <summary>最佳候选是否完成了独立精确终验。</summary>
    public bool FinalValidationAttempted { get; }
    /// <summary>独立精确终验是否仍为 Safe。</summary>
    public bool FinalValidationPassed { get; }
    /// <summary>可用于结构化日志的稳定失败原因。</summary>
    public string FailureReason { get; }
    /// <summary>本次搜索使用的冻结配置。</summary>
    public RecoveryHeadingSearchSettings Settings { get; }

    /// <summary>创建不可变搜索结果。</summary>
    public RecoveryHeadingSearchResult(
        RecoveryHeadingPlan plan,
        IList<HeadingSample> evaluations,
        int attemptedEvaluationCount,
        int successfulEvaluationCount,
        float completedCoverageResolutionDegrees,
        bool budgetExhausted,
        bool finalValidationAttempted,
        bool finalValidationPassed,
        string failureReason,
        RecoveryHeadingSearchSettings settings)
    {
        Plan = plan ?? RecoveryHeadingPlan.Failed(RecoveryHeadingFailure.InvalidSearchContext);
        Evaluations = Array.AsReadOnly(new List<HeadingSample>(
            evaluations ?? Array.Empty<HeadingSample>()).ToArray());
        AttemptedEvaluationCount = Mathf.Max(0, attemptedEvaluationCount);
        SuccessfulEvaluationCount = Mathf.Clamp(
            successfulEvaluationCount, 0, AttemptedEvaluationCount);
        CompletedCoverageResolutionDegrees = completedCoverageResolutionDegrees;
        BudgetExhausted = budgetExhausted;
        FinalValidationAttempted = finalValidationAttempted;
        FinalValidationPassed = finalValidationPassed;
        FailureReason = failureReason ?? string.Empty;
        Settings = settings != null ? settings.SanitizedCopy() : new RecoveryHeadingSearchSettings();
    }
}

/// <summary>通过全角域分层覆盖、Safe candidate 评分和精确终验搜索恢复航向。</summary>
public sealed class RecoveryHeadingPlanner
{
    private const float AngleKeyScale = 10000f;

    /// <summary>在完整 360 度上逐层加密采样，并返回最佳经过终验的 Safe Heading。</summary>
    public RecoveryHeadingSearchResult Search(
        PhysicalRiskState riskState,
        IPhysicalHeadingEvaluator evaluator,
        RecoveryHeadingSearchSettings settings = null)
    {
        var config = (settings ?? new RecoveryHeadingSearchSettings()).SanitizedCopy();
        var evaluations = new List<HeadingSample>();
        var attemptedEvaluationCount = 0;
        var successfulEvaluationCount = 0;
        if (riskState == null || evaluator == null ||
            riskState.user_physical_forward.sqrMagnitude <= 0.0001f)
        {
            return Failed(RecoveryHeadingFailure.InvalidSearchContext, evaluations,
                attemptedEvaluationCount, successfulEvaluationCount, float.PositiveInfinity,
                false, false, false, "invalid_search_context", config);
        }

        var evaluatedAngles = new HashSet<int>();
        var currentForward = riskState.user_physical_forward.normalized;
        var completedResolution = float.PositiveInfinity;
        var budgetExhausted = false;
        var step = Mathf.Max(config.minimumResolutionDegrees, config.coarseStepDegrees);
        var searchEvaluationBudget = config.maximumEvaluations - 1;

        // 每一层都覆盖完整全角域，避免两个 Unsafe 粗采样点之间的狭窄 Safe 区间被跳过。
        while (true)
        {
            var completedLayer = EvaluateFullCoverageLayer(
                riskState, evaluator, currentForward, step, searchEvaluationBudget,
                evaluatedAngles, evaluations, ref attemptedEvaluationCount,
                ref successfulEvaluationCount, out var actualLayerStep);
            if (!completedLayer)
            {
                budgetExhausted = true;
                break;
            }

            completedResolution = actualLayerStep;
            if (step <= config.minimumResolutionDegrees + 0.0001f)
            {
                break;
            }

            var nextStep = Mathf.Max(
                config.minimumResolutionDegrees,
                step / config.refinementFactor);
            if (Mathf.Abs(nextStep - step) <= 0.0001f)
            {
                break;
            }
            step = nextStep;
        }

        var found = false;
        var best = default(HeadingSample);
        var bestScore = float.NegativeInfinity;
        foreach (var sample in evaluations)
        {
            if (sample.Safety != HeadingSafety.Safe) continue;
            var score = Score(sample, config);
            if (!found || score > bestScore ||
                (Mathf.Approximately(score, bestScore) &&
                 Mathf.Abs(sample.RelativeYawDegrees) < Mathf.Abs(best.RelativeYawDegrees)))
            {
                found = true;
                best = sample;
                bestScore = score;
            }
        }

        if (!found)
        {
            if (attemptedEvaluationCount > 0 && successfulEvaluationCount == 0)
            {
                var unavailableReason = budgetExhausted
                    ? "heading_evaluation_unavailable_within_budget"
                    : "heading_evaluation_unavailable";
                return Failed(RecoveryHeadingFailure.EvaluationUnavailable, evaluations,
                    attemptedEvaluationCount, successfulEvaluationCount, completedResolution,
                    budgetExhausted, false, false, unavailableReason, config);
            }

            var failureReason = budgetExhausted
                ? "no_safe_heading_at_current_resolution_budget_exhausted"
                : "no_safe_heading_at_configured_resolution";
            return Failed(RecoveryHeadingFailure.NoAcceptableHeading, evaluations,
                attemptedEvaluationCount, successfulEvaluationCount, completedResolution,
                budgetExhausted, false, false, failureReason, config);
        }

        // 终验必须重新调用 Unity 精确评估器，不复用搜索缓存中的候选事实。
        attemptedEvaluationCount++;
        var finalEvaluationSucceeded = evaluator.TryEvaluateAbsoluteHeading(
            riskState, best.PhysicalForward, out var validated);
        if (finalEvaluationSucceeded) successfulEvaluationCount++;
        if (!finalEvaluationSucceeded || validated.Safety != HeadingSafety.Safe)
        {
            return Failed(RecoveryHeadingFailure.FinalValidationFailed, evaluations,
                attemptedEvaluationCount, successfulEvaluationCount, completedResolution,
                budgetExhausted, true, false, "best_heading_failed_exact_final_validation", config);
        }

        var plan = new RecoveryHeadingPlan(validated, Score(validated, config));
        return new RecoveryHeadingSearchResult(
            plan, evaluations, attemptedEvaluationCount, successfulEvaluationCount,
            completedResolution, budgetExhausted, true, true, string.Empty, config);
    }

    private static bool EvaluateFullCoverageLayer(
        PhysicalRiskState riskState,
        IPhysicalHeadingEvaluator evaluator,
        Vector2 currentForward,
        float requestedStepDegrees,
        int maximumEvaluations,
        ISet<int> evaluatedAngles,
        ICollection<HeadingSample> evaluations,
        ref int attemptedEvaluationCount,
        ref int successfulEvaluationCount,
        out float actualStep)
    {
        var sampleCount = Mathf.Max(2, Mathf.CeilToInt(360f / requestedStepDegrees));
        actualStep = 360f / sampleCount;
        for (var index = 0; index < sampleCount; index++)
        {
            var yaw = -180f + index * actualStep;
            var key = Mathf.RoundToInt((yaw + 180f) * AngleKeyScale);
            if (evaluatedAngles.Contains(key)) continue;
            if (attemptedEvaluationCount >= maximumEvaluations) return false;
            evaluatedAngles.Add(key);

            var direction = Rotate(currentForward, yaw);
            attemptedEvaluationCount++;
            if (evaluator.TryEvaluateAbsoluteHeading(riskState, direction, out var sample))
            {
                successfulEvaluationCount++;
                evaluations.Add(sample);
            }
        }
        return true;
    }

    private static float Score(HeadingSample sample, RecoveryHeadingSearchSettings settings)
    {
        var normalizedClearance = Mathf.Clamp01(
            sample.ClearanceMeters / settings.clearanceNormalizationMeters);
        var normalizedFutureDistance = normalizedClearance;
        var normalizedTurn = Mathf.Abs(Mathf.DeltaAngle(0f, sample.RelativeYawDegrees)) / 180f;
        return normalizedClearance * settings.clearanceWeight +
               normalizedFutureDistance * settings.futureDistanceWeight -
               normalizedTurn * settings.turnCostWeight;
    }

    private static RecoveryHeadingSearchResult Failed(
        RecoveryHeadingFailure failure,
        IList<HeadingSample> evaluations,
        int attemptedEvaluationCount,
        int successfulEvaluationCount,
        float resolution,
        bool budgetExhausted,
        bool finalValidationAttempted,
        bool finalValidationPassed,
        string failureReason,
        RecoveryHeadingSearchSettings settings)
    {
        return new RecoveryHeadingSearchResult(
            RecoveryHeadingPlan.Failed(failure), evaluations, attemptedEvaluationCount,
            successfulEvaluationCount, resolution, budgetExhausted, finalValidationAttempted,
            finalValidationPassed, failureReason, settings);
    }

    private static Vector2 Rotate(Vector2 forward, float yawDegrees)
    {
        var radians = yawDegrees * Mathf.Deg2Rad;
        var sin = Mathf.Sin(radians);
        var cos = Mathf.Cos(radians);
        return new Vector2(
            forward.x * cos + forward.y * sin,
            -forward.x * sin + forward.y * cos).normalized;
    }
}
