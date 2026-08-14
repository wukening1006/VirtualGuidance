using System;
using UnityEngine;

/// <summary>
/// 定义一次只包含单个身体动作的导览计划。
/// </summary>
public enum GuidanceActionType
{
    /// <summary>停止移动并保持当前姿态。</summary>
    Hold,
    /// <summary>原地向指定方向转向。</summary>
    TurnInPlace,
    /// <summary>面向目标后缓慢向前移动。</summary>
    MoveForward,
    /// <summary>不执行身体动作并等待重新规划。</summary>
    Reevaluate
}

/// <summary>
/// 表示原地转向的左右方向。
/// </summary>
public enum TurnSide
{
    /// <summary>当前动作不包含转向。</summary>
    None,
    /// <summary>向左转。</summary>
    Left,
    /// <summary>向右转。</summary>
    Right
}

/// <summary>
/// 保存本地规划器生成的不可变原子动作。
/// </summary>
public sealed class GuidanceAction
{
    /// <summary>动作类型。</summary>
    public GuidanceActionType Type { get; }
    /// <summary>原地转向方向；非转向动作使用 None。</summary>
    public TurnSide TurnSide { get; }
    /// <summary>相对当前朝向的目标偏航角，单位：度。</summary>
    public float TargetYawDegrees { get; }
    /// <summary>动作完成后是否必须使用最新位姿重新规划。</summary>
    public bool RequiresReevaluation { get; }

    /// <summary>创建一项不可变原子动作。</summary>
    public GuidanceAction(
        GuidanceActionType type,
        TurnSide turnSide = TurnSide.None,
        float targetYawDegrees = 0f,
        bool requiresReevaluation = false)
    {
        Type = type;
        TurnSide = turnSide;
        TargetYawDegrees = Mathf.DeltaAngle(0f, targetYawDegrees);
        RequiresReevaluation = requiresReevaluation;
    }
}

/// <summary>
/// 将目标连续偏航转换成原地转向或面向后的缓慢前进。
/// </summary>
public sealed class GuidanceActionPlanner
{
    private readonly float turnThresholdDegrees;

    /// <summary>
    /// 创建动作规划器。
    /// </summary>
    /// <param name="turnThresholdDegrees">超过该绝对偏航后先原地转向，单位：度。</param>
    public GuidanceActionPlanner(float turnThresholdDegrees = 25f)
    {
        this.turnThresholdDegrees = Mathf.Clamp(turnThresholdDegrees, 1f, 89f);
    }

    /// <summary>
    /// 为目标生成一个原子动作；目标位于身后时只生成转向，不直接前进。
    /// </summary>
    public GuidanceAction Plan(TargetBearing bearing)
    {
        if (bearing == null)
        {
            return new GuidanceAction(
                GuidanceActionType.Reevaluate,
                requiresReevaluation: true);
        }

        var yaw = Mathf.DeltaAngle(0f, bearing.RelativeYawDegrees);
        if (Mathf.Abs(yaw) > turnThresholdDegrees)
        {
            return new GuidanceAction(
                GuidanceActionType.TurnInPlace,
                yaw < 0f ? TurnSide.Left : TurnSide.Right,
                yaw,
                true);
        }

        return new GuidanceAction(GuidanceActionType.MoveForward);
    }
}

/// <summary>
/// 使用 Unity 精确 Physical Heading Evaluation 判断原子动作当前能否执行。
/// </summary>
public sealed class GuidanceActionSafetyEvaluator
{
    /// <summary>
    /// 判断动作当前是否可执行，并返回可记录的阻塞原因。
    /// </summary>
    public CandidateExecutionState Evaluate(
        GuidanceAction action,
        PhysicalRiskState riskState,
        out string reason)
    {
        if (action == null || riskState == null)
        {
            reason = "action_or_risk_unavailable";
            return CandidateExecutionState.Unknown;
        }

        switch (action.Type)
        {
            case GuidanceActionType.MoveForward:
                return EvaluateHeading(action, riskState, null, 0f, true, out reason);
            case GuidanceActionType.TurnInPlace:
                return EvaluateHeading(action, riskState, null, action.TargetYawDegrees, false, out reason);
            case GuidanceActionType.Hold:
            case GuidanceActionType.Reevaluate:
                reason = "action_requires_reevaluation";
                return CandidateExecutionState.TemporarilyBlocked;
            default:
                reason = "unknown_action";
                return CandidateExecutionState.Unknown;
        }
    }

    /// <summary>使用指定 Unity 精确评估器评估任意真实动作方向。</summary>
    public CandidateExecutionState Evaluate(
        GuidanceAction action,
        PhysicalRiskState riskState,
        IPhysicalHeadingEvaluator evaluator,
        out string reason)
    {
        if (action == null || riskState == null || evaluator == null)
        {
            reason = "exact_heading_evaluator_unavailable";
            return CandidateExecutionState.Unknown;
        }

        switch (action.Type)
        {
            case GuidanceActionType.MoveForward:
                return EvaluateHeading(action, riskState, evaluator, 0f, true, out reason);
            case GuidanceActionType.TurnInPlace:
                return EvaluateHeading(action, riskState, evaluator, action.TargetYawDegrees, false, out reason);
            case GuidanceActionType.Hold:
            case GuidanceActionType.Reevaluate:
                reason = "action_requires_reevaluation";
                return CandidateExecutionState.TemporarilyBlocked;
            default:
                reason = "unknown_action";
                return CandidateExecutionState.Unknown;
        }
    }

    private static CandidateExecutionState EvaluateHeading(
        GuidanceAction action,
        PhysicalRiskState riskState,
        IPhysicalHeadingEvaluator evaluator,
        float relativeYawDegrees,
        bool requireMovementSafe,
        out string reason)
    {
        var currentForward = riskState.user_physical_forward.sqrMagnitude > 0.0001f
            ? riskState.user_physical_forward.normalized
            : Vector2.up;
        var radians = relativeYawDegrees * Mathf.Deg2Rad;
        var sin = Mathf.Sin(radians);
        var cos = Mathf.Cos(radians);
        var physicalDirection = new Vector2(
            currentForward.x * cos + currentForward.y * sin,
            -currentForward.x * sin + currentForward.y * cos).normalized;

        HeadingSample heading;
        if (evaluator != null)
        {
            if (!evaluator.TryEvaluateAbsoluteHeading(riskState, physicalDirection, out heading))
            {
                reason = "exact_heading_evaluation_failed";
                return CandidateExecutionState.Unknown;
            }
        }
        else if (Mathf.Abs(Mathf.DeltaAngle(0f, relativeYawDegrees)) <= 0.001f)
        {
            heading = riskState.current_heading;
        }
        else
        {
            reason = "exact_heading_evaluator_required";
            return CandidateExecutionState.Unknown;
        }

        var executable = requireMovementSafe
            ? PhysicalRiskEvaluator.IsMovementSafe(heading)
            : PhysicalRiskEvaluator.IsRecoveryHeadingAcceptable(heading);
        if (executable)
        {
            reason = string.Empty;
            return CandidateExecutionState.Executable;
        }

        reason = heading.Safety == HeadingSafety.Unknown
            ? "heading_requires_hold"
            : "heading_clearance_blocked";
        return heading.Safety == HeadingSafety.Unknown
            ? CandidateExecutionState.TemporarilyBlocked
            : CandidateExecutionState.Blocked;
    }
}

/// <summary>
/// 将内部原子动作映射到迁移期继续使用的 movement_instruction。
/// </summary>
public static class GuidanceActionProtocolMapper
{
    /// <summary>返回旧协议可识别的移动指令。</summary>
    public static string ToMovementInstruction(GuidanceAction action)
    {
        if (action == null)
        {
            return "pause_and_wait";
        }

        switch (action.Type)
        {
            case GuidanceActionType.MoveForward:
                return "move_forward_slowly";
            case GuidanceActionType.TurnInPlace:
                return action.TurnSide == TurnSide.Left
                    ? "turn_left_slowly"
                    : "turn_right_slowly";
            default:
                return "pause_and_wait";
        }
    }
}
