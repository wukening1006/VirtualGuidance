using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 表示 Reset 完成后继续导航的可行性等级。
/// </summary>
public enum PostResetContinuationLevel
{
    /// <summary>没有可以主动恢复导航的方式。</summary>
    HoldOnly = 0,
    /// <summary>可以通过受控转向或 RDW 规划继续恢复。</summary>
    RdwRecovery = 1,
    /// <summary>存在安全语义绕行目标。</summary>
    SafeReroute = 2,
    /// <summary>当前任务目标可以直接继续。</summary>
    DirectTarget = 3
}

/// <summary>
/// 保存单个 Safe Heading 在 Reset 后的导航延续评估。
/// </summary>
public readonly struct PostResetContinuationAssessment
{
    /// <summary>该朝向对应的导航可行性等级。</summary>
    public PostResetContinuationLevel Level { get; }
    /// <summary>可继续导航时对应的目标 ID；RDW 恢复或 Hold 时为空。</summary>
    public string TargetId { get; }
    /// <summary>候选方向是否通过 Physical Safety Authority 的精确评估。</summary>
    public bool IsPhysicalSafe { get; }
    /// <summary>用于同等级排序的评分。</summary>
    public float Score { get; }
    /// <summary>稳定诊断原因。</summary>
    public string Reason { get; }

    /// <summary>创建一项不可变的 Reset 后延续评估。</summary>
    public PostResetContinuationAssessment(
        PostResetContinuationLevel level,
        string targetId,
        bool isPhysicalSafe,
        float score,
        string reason)
    {
        Level = level;
        TargetId = targetId ?? string.Empty;
        IsPhysicalSafe = isPhysicalSafe;
        Score = score;
        Reason = reason ?? string.Empty;
    }
}

/// <summary>
/// 保存一个 Recovery Heading 的物理安全与 Reset 后导航延续诊断。
/// </summary>
public sealed class PostResetHeadingSelectionRecord
{
    /// <summary>经过精确评估的物理朝向。</summary>
    public HeadingSample Heading { get; }
    /// <summary>该朝向对应的 Reset 后导航延续评估。</summary>
    public PostResetContinuationAssessment Continuation { get; }
    /// <summary>该朝向是否最终被选为 Recovery Reset Heading。</summary>
    public bool Selected { get; }

    /// <summary>创建一条不可变的 Recovery Heading 选择记录。</summary>
    public PostResetHeadingSelectionRecord(
        HeadingSample heading,
        PostResetContinuationAssessment continuation,
        bool selected)
    {
        Heading = heading;
        Continuation = continuation;
        Selected = selected;
    }
}

/// <summary>
/// 保存 Navigation Recovery 状态机事件及可选的 Heading 选择明细。
/// </summary>
public sealed class NavigationRecoveryDiagnosticInfo
{
    /// <summary>稳定的状态机事件名。</summary>
    public string EventName { get; }
    /// <summary>触发 Recovery 时使用的规划事实指纹。</summary>
    public string PlanningFingerprint { get; }
    /// <summary>当前 Initial Reset 后已经启动的 Recovery Reset 次数。</summary>
    public int RecoveryResetAttempts { get; }
    /// <summary>本次 Heading 选择检查的完整明细。</summary>
    public IReadOnlyList<PostResetHeadingSelectionRecord> HeadingSelections { get; }

    /// <summary>创建一条不可变的 Navigation Recovery 诊断事件。</summary>
    public NavigationRecoveryDiagnosticInfo(
        string eventName,
        string planningFingerprint,
        int recoveryResetAttempts,
        IList<PostResetHeadingSelectionRecord> headingSelections = null)
    {
        EventName = eventName ?? string.Empty;
        PlanningFingerprint = planningFingerprint ?? string.Empty;
        RecoveryResetAttempts = Math.Max(0, recoveryResetAttempts);
        HeadingSelections = Array.AsReadOnly(new List<PostResetHeadingSelectionRecord>(
            headingSelections ?? Array.Empty<PostResetHeadingSelectionRecord>()).ToArray());
    }
}

/// <summary>
/// 预测某个 Safe Heading 在 Reset 完成后是否还能继续导航。
/// Physical Safety 仍由注入的 IPhysicalHeadingEvaluator 权威裁决。
/// </summary>
public sealed class PostResetContinuationEvaluator
{
    /// <summary>
    /// 评估一个恢复计划；候选方向使用当前 Virtual Pose 的相对偏航预测 Reset 后的 Physical Heading。
    /// </summary>
    public PostResetContinuationAssessment Evaluate(
        PhysicalRiskState riskState,
        RecoveryHeadingPlan recoveryPlan,
        NavigationCandidateSet candidateSet,
        IPhysicalHeadingEvaluator physicalEvaluator,
        bool hasExecutableRdwRecovery = false)
    {
        if (recoveryPlan == null || !recoveryPlan.IsSuccess || physicalEvaluator == null)
        {
            return Hold("recovery_heading_unavailable");
        }

        return EvaluateHeading(
            riskState,
            recoveryPlan.SafePhysicalForward,
            recoveryPlan.Score,
            candidateSet != null ? candidateSet.Candidates : null,
            physicalEvaluator,
            hasExecutableRdwRecovery);
    }

    /// <summary>
    /// 评估一个已知 Safe Heading，供 Coordinator 对全角域 Safe 样本排序。
    /// </summary>
    public PostResetContinuationAssessment EvaluateHeading(
        PhysicalRiskState riskState,
        Vector2 safePhysicalForward,
        float baseScore,
        IEnumerable<NavigationCandidate> candidates,
        IPhysicalHeadingEvaluator physicalEvaluator,
        bool hasExecutableRdwRecovery = false)
    {
        if (riskState == null || physicalEvaluator == null || safePhysicalForward.sqrMagnitude <= 0.0001f)
        {
            return Hold("post_reset_facts_unavailable");
        }

        var bestLevel = hasExecutableRdwRecovery
            ? PostResetContinuationLevel.RdwRecovery
            : PostResetContinuationLevel.HoldOnly;
        var bestTargetId = string.Empty;
        var bestScore = baseScore;
        var inspectedCandidate = false;
        foreach (var candidate in candidates ?? Array.Empty<NavigationCandidate>())
        {
            if (candidate == null || string.IsNullOrWhiteSpace(candidate.TargetId))
            {
                continue;
            }

            inspectedCandidate = true;
            var predictedDirection = Rotate(safePhysicalForward, candidate.RelativeYawDegrees);
            if (!physicalEvaluator.TryEvaluateAbsoluteHeading(riskState, predictedDirection, out var heading) ||
                !PhysicalRiskEvaluator.IsMovementSafe(heading))
            {
                continue;
            }

            var candidateLevel = candidate.TargetRole == NavigationTargetRole.FinalTarget
                ? PostResetContinuationLevel.DirectTarget
                : PostResetContinuationLevel.SafeReroute;
            if (candidateLevel > bestLevel ||
                (candidateLevel == bestLevel && baseScore > bestScore))
            {
                bestLevel = candidateLevel;
                bestTargetId = candidate.TargetId;
                bestScore = baseScore;
            }
        }

        if (bestLevel == PostResetContinuationLevel.HoldOnly)
        {
            return Hold(
                inspectedCandidate
                    ? "safe_heading_has_no_executable_continuation"
                    : "no_continuation_candidate_and_rdw_unverified",
                true);
        }

        var reason = bestLevel == PostResetContinuationLevel.RdwRecovery
            ? "rdw_recovery_executable"
            : bestLevel == PostResetContinuationLevel.DirectTarget
                ? "direct_target_executable_after_reset"
                : "safe_reroute_executable_after_reset";
        return new PostResetContinuationAssessment(
            bestLevel,
            bestTargetId,
            true,
            bestScore,
            reason);
    }

    private static PostResetContinuationAssessment Hold(string reason, bool physicalSafe = false)
    {
        return new PostResetContinuationAssessment(
            PostResetContinuationLevel.HoldOnly,
            string.Empty,
            physicalSafe,
            float.NegativeInfinity,
            reason);
    }

    private static Vector2 Rotate(Vector2 forward, float yawDegrees)
    {
        forward = forward.sqrMagnitude > 0.0001f ? forward.normalized : Vector2.up;
        var radians = yawDegrees * Mathf.Deg2Rad;
        var sin = Mathf.Sin(radians);
        var cos = Mathf.Cos(radians);
        return new Vector2(
            forward.x * cos + forward.y * sin,
            -forward.x * sin + forward.y * cos).normalized;
    }
}
