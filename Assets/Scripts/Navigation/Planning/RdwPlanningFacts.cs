using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

/// <summary>
/// 控制 RDW 路线规划是否参与正式目标选择。
/// </summary>
public enum RdwPlanningMode
{
    /// <summary>完全使用既有 selected_target 链。</summary>
    Legacy,
    /// <summary>生成并记录路线，但不改变正式 selected_target。</summary>
    Shadow,
    /// <summary>由受约束 route_id 选择路线第一步。</summary>
    RdwConstrained
}

/// <summary>
/// 冻结规划器可使用的 Continuous RDW 与 Resetter 能力身份。
/// </summary>
public sealed class RdwCapabilityProfile
{
    /// <summary>当前 Continuous Redirector 类型。</summary>
    public string ContinuousRedirectorType { get; }
    /// <summary>当前 Resetter 类型。</summary>
    public string ResetterType { get; }
    /// <summary>是否允许 Rotation Gain。</summary>
    public bool RotationGainAllowed { get; }
    /// <summary>是否允许 Curvature Gain。</summary>
    public bool CurvatureGainAllowed { get; }
    /// <summary>是否允许 Translation Gain。</summary>
    public bool TranslationGainAllowed { get; }
    /// <summary>当前 Gain 限制配置版本。</summary>
    public string GainLimitsVersion { get; }
    /// <summary>规划代价估计器 ID。</summary>
    public string EstimatorId { get; }
    /// <summary>规划代价估计器版本。</summary>
    public string EstimatorVersion { get; }
    /// <summary>用于缓存失效判断的稳定能力身份。</summary>
    public string Identity { get; }

    /// <summary>
    /// 创建一份不可变 RDW 能力描述。
    /// </summary>
    public RdwCapabilityProfile(
        string continuousRedirectorType,
        string resetterType,
        bool rotationGainAllowed,
        bool curvatureGainAllowed,
        bool translationGainAllowed,
        string gainLimitsVersion,
        string estimatorId,
        string estimatorVersion)
    {
        ContinuousRedirectorType = continuousRedirectorType ?? string.Empty;
        ResetterType = resetterType ?? string.Empty;
        RotationGainAllowed = rotationGainAllowed;
        CurvatureGainAllowed = curvatureGainAllowed;
        TranslationGainAllowed = translationGainAllowed;
        GainLimitsVersion = gainLimitsVersion ?? string.Empty;
        EstimatorId = estimatorId ?? string.Empty;
        EstimatorVersion = estimatorVersion ?? string.Empty;
        Identity = RdwPlanningIdentity.CreateStableId(
            "cap",
            ContinuousRedirectorType,
            ResetterType,
            rotationGainAllowed ? "rotation" : "no_rotation",
            curvatureGainAllowed ? "curvature" : "no_curvature",
            translationGainAllowed ? "translation" : "no_translation",
            GainLimitsVersion,
            EstimatorId,
            EstimatorVersion);
    }
}

/// <summary>
/// 冻结单次规划使用的任务、位姿、风险、空间与能力事实。
/// </summary>
public sealed class RdwPlanningSnapshot
{
    private readonly IReadOnlyList<string> visitedNodeIds;

    /// <summary>单次规划事实的唯一 ID。</summary>
    public string SnapshotId { get; }
    /// <summary>最近有效 RDW Pose 序号。</summary>
    public long PoseSequence { get; }
    /// <summary>创建快照的 tracking pose 是否有效。</summary>
    public bool PoseValid { get; }
    /// <summary>位姿采样单调时间戳，单位：秒。</summary>
    public double Timestamp { get; }
    /// <summary>用户 Physical XZ 位置，单位：米。</summary>
    public Vector2 PhysicalPositionXZ { get; }
    /// <summary>用户 Physical XZ 单位前向。</summary>
    public Vector2 PhysicalForwardXZ { get; }
    /// <summary>用户 Virtual XZ 位置，单位：米。</summary>
    public Vector2 VirtualPositionXZ { get; }
    /// <summary>用户 Virtual XZ 单位前向。</summary>
    public Vector2 VirtualForwardXZ { get; }
    /// <summary>Physical Calibration ID。</summary>
    public string CalibrationId { get; }
    /// <summary>Physical Space ID。</summary>
    public string PhysicalSpaceId { get; }
    /// <summary>Unity Risk Snapshot ID。</summary>
    public string RiskSnapshotId { get; }
    /// <summary>执行相关风险修订号。</summary>
    public int RiskRevision { get; }
    /// <summary>当前任务节点 ID。</summary>
    public string CurrentNodeId { get; }
    /// <summary>任务最终目标 ID。</summary>
    public string FinalTargetId { get; }
    /// <summary>任务状态版本。</summary>
    public int TaskStateVersion { get; }
    /// <summary>场景 Target Registry 修订号。</summary>
    public int RegistryRevision { get; }
    /// <summary>规划创建时已访问节点的只读副本。</summary>
    public IReadOnlyList<string> VisitedNodeIds => visitedNodeIds;
    /// <summary>本次规划冻结的 Physical Space 几何。</summary>
    public RdwGeometrySnapshot Geometry { get; }
    /// <summary>Exact Direction Safety 使用的只读风险快照。</summary>
    public PhysicalRiskState RiskState { get; }
    /// <summary>本次规划使用的 RDW 能力。</summary>
    public RdwCapabilityProfile Capability { get; }
    /// <summary>快照是否具备规划所需的完整身份与几何。</summary>
    public bool IsValid =>
        !string.IsNullOrWhiteSpace(SnapshotId) &&
        PoseValid &&
        PoseSequence > 0 &&
        !string.IsNullOrWhiteSpace(CalibrationId) &&
        !string.IsNullOrWhiteSpace(PhysicalSpaceId) &&
        !string.IsNullOrWhiteSpace(RiskSnapshotId) &&
        !string.IsNullOrWhiteSpace(CurrentNodeId) &&
        RiskState != null &&
        Capability != null &&
        Geometry.IsValid;

    /// <summary>
    /// 创建不可变 Planning Snapshot；所有候选边必须共享同一实例。
    /// </summary>
    public RdwPlanningSnapshot(
        string snapshotId,
        long poseSequence,
        double timestamp,
        Vector2 physicalPositionXZ,
        Vector2 physicalForwardXZ,
        Vector2 virtualPositionXZ,
        Vector2 virtualForwardXZ,
        string calibrationId,
        string physicalSpaceId,
        string riskSnapshotId,
        int riskRevision,
        string currentNodeId,
        string finalTargetId,
        int taskStateVersion,
        int registryRevision,
        IEnumerable<string> visitedNodes,
        in RdwGeometrySnapshot geometry,
        PhysicalRiskState riskState,
        RdwCapabilityProfile capability,
        bool poseValid = true)
    {
        SnapshotId = snapshotId ?? string.Empty;
        PoseSequence = Math.Max(0L, poseSequence);
        PoseValid = poseValid;
        Timestamp = Math.Max(0d, timestamp);
        PhysicalPositionXZ = physicalPositionXZ;
        PhysicalForwardXZ = Normalize(physicalForwardXZ);
        VirtualPositionXZ = virtualPositionXZ;
        VirtualForwardXZ = Normalize(virtualForwardXZ);
        CalibrationId = calibrationId ?? string.Empty;
        PhysicalSpaceId = physicalSpaceId ?? string.Empty;
        RiskSnapshotId = riskSnapshotId ?? string.Empty;
        RiskRevision = Math.Max(0, riskRevision);
        CurrentNodeId = currentNodeId ?? string.Empty;
        FinalTargetId = finalTargetId ?? string.Empty;
        TaskStateVersion = Math.Max(0, taskStateVersion);
        RegistryRevision = Math.Max(0, registryRevision);
        visitedNodeIds = Array.AsReadOnly(new List<string>(visitedNodes ?? Array.Empty<string>()).ToArray());
        Geometry = geometry;
        RiskState = riskState;
        Capability = capability;
    }

    /// <summary>创建新的唯一 Planning Snapshot ID。</summary>
    public static string NewSnapshotId() => $"snapshot-{Guid.NewGuid():N}";

    private static Vector2 Normalize(Vector2 value)
    {
        return value.sqrMagnitude > 0.000001f ? value.normalized : Vector2.up;
    }
}

/// <summary>
/// 保存 Beam Search 单条分支的预测 Virtual/Physical 状态，不写回运行时对象。
/// </summary>
public readonly struct RdwPlanningTraversalState
{
    /// <summary>预测 Virtual 位置。</summary>
    public Vector2 VirtualPositionXZ { get; }
    /// <summary>预测 Virtual 前向。</summary>
    public Vector2 VirtualForwardXZ { get; }
    /// <summary>预测 Physical 位置。</summary>
    public Vector2 PhysicalPositionXZ { get; }
    /// <summary>预测 Physical 前向。</summary>
    public Vector2 PhysicalForwardXZ { get; }

    /// <summary>创建一份纯值类型的搜索分支状态。</summary>
    public RdwPlanningTraversalState(
        Vector2 virtualPositionXZ,
        Vector2 virtualForwardXZ,
        Vector2 physicalPositionXZ,
        Vector2 physicalForwardXZ)
    {
        VirtualPositionXZ = virtualPositionXZ;
        VirtualForwardXZ = Normalize(virtualForwardXZ);
        PhysicalPositionXZ = physicalPositionXZ;
        PhysicalForwardXZ = Normalize(physicalForwardXZ);
    }

    /// <summary>从根 Planning Snapshot 创建初始搜索状态。</summary>
    public static RdwPlanningTraversalState FromSnapshot(RdwPlanningSnapshot snapshot)
    {
        return new RdwPlanningTraversalState(
            snapshot.VirtualPositionXZ,
            snapshot.VirtualForwardXZ,
            snapshot.PhysicalPositionXZ,
            snapshot.PhysicalForwardXZ);
    }

    private static Vector2 Normalize(Vector2 value)
    {
        return value.sqrMagnitude > 0.000001f ? value.normalized : Vector2.up;
    }
}

/// <summary>
/// 统一表达一条局部导航边的 Exact Safety 与标准化 RDW 代价。
/// </summary>
public sealed class RdwEdgeEvaluation
{
    /// <summary>相对预测 Virtual Forward 的连续偏航角，单位：度。</summary>
    public float RelativeYawDegrees { get; }
    /// <summary>预测 Physical 单位前向。</summary>
    public Vector2 PredictedPhysicalHeading { get; }
    /// <summary>预测边末端 Physical 位置，单位：米。</summary>
    public Vector2 PredictedPhysicalPositionXZ { get; }
    /// <summary>Exact Direction Safety 净空，单位：米。</summary>
    public float ClearanceMeters { get; }
    /// <summary>Unity Exact Direction Safety 等级。</summary>
    public HeadingSafety SafetyLevel { get; }
    /// <summary>当前 Continuous RDW 对该边的归一化收益，范围 0-1。</summary>
    public float RdwBenefit { get; }
    /// <summary>预计感知与重定向代价，范围 0-1。</summary>
    public float GainCost { get; }
    /// <summary>该边是否通过估计器的基础可计算检查。</summary>
    public bool Feasible { get; }
    /// <summary>不可行时的稳定原因码。</summary>
    public string RejectionReason { get; }
    /// <summary>标准化未来恢复压力，范围 0-1。</summary>
    public float ResetPressure { get; }

    /// <summary>创建一项不可变 Edge Evaluation。</summary>
    public RdwEdgeEvaluation(
        float relativeYawDegrees,
        Vector2 predictedPhysicalHeading,
        Vector2 predictedPhysicalPositionXZ,
        float clearanceMeters,
        HeadingSafety safetyLevel,
        float rdwBenefit,
        float gainCost,
        bool feasible,
        string rejectionReason,
        float resetPressure)
    {
        RelativeYawDegrees = Mathf.DeltaAngle(0f, relativeYawDegrees);
        PredictedPhysicalHeading = predictedPhysicalHeading.sqrMagnitude > 0.000001f
            ? predictedPhysicalHeading.normalized
            : Vector2.up;
        PredictedPhysicalPositionXZ = predictedPhysicalPositionXZ;
        ClearanceMeters = Mathf.Max(0f, clearanceMeters);
        SafetyLevel = safetyLevel;
        RdwBenefit = Mathf.Clamp01(rdwBenefit);
        GainCost = Mathf.Clamp01(gainCost);
        Feasible = feasible;
        RejectionReason = rejectionReason ?? string.Empty;
        ResetPressure = Mathf.Clamp01(resetPressure);
    }
}

/// <summary>
/// 为 snapshot、route 与 capability 生成稳定且可比较的短身份。
/// </summary>
public static class RdwPlanningIdentity
{
    /// <summary>以有序字段的 SHA-256 前缀创建稳定 ID。</summary>
    public static string CreateStableId(string prefix, params string[] values)
    {
        using (var sha256 = SHA256.Create())
        {
            var payload = string.Join("\u001f", values ?? Array.Empty<string>());
            var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(payload));
            var builder = new StringBuilder(24);
            for (var index = 0; index < 8; index++)
            {
                builder.Append(hash[index].ToString("x2"));
            }
            return $"{prefix}-{builder}";
        }
    }
}
