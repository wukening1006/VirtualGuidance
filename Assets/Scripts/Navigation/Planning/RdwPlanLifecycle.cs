using System;
using System.Collections.Generic;

/// <summary>
/// 统一记录 RoutePlan 失效原因，便于日志与回归测试。
/// </summary>
public enum RoutePlanInvalidationReason
{
    /// <summary>没有失效。</summary>
    None,
    /// <summary>Reset 已开始。</summary>
    ResetStarted,
    /// <summary>Reset 已完成，必须 Fresh Planning。</summary>
    ResetCompleted,
    /// <summary>任务状态发生非预期变化。</summary>
    TaskStateChanged,
    /// <summary>Calibration identity 变化。</summary>
    CalibrationChanged,
    /// <summary>Physical Space identity 变化。</summary>
    PhysicalSpaceChanged,
    /// <summary>最终任务目标变化。</summary>
    FinalTargetChanged,
    /// <summary>RDW capability 变化。</summary>
    CapabilityChanged,
    /// <summary>Target Registry 变化。</summary>
    RegistryChanged,
    /// <summary>下一步 Exact Safety 复验失败。</summary>
    FirstStepRejected,
    /// <summary>路线已耗尽。</summary>
    RouteExhausted,
    /// <summary>活动 Guidance 被取消或软重规划。</summary>
    GuidanceCancelled
}

/// <summary>
/// 保存一次后端路线选择请求的 plan_id、generation 与 snapshot identity。
/// </summary>
public sealed class PlanningCallIdentity
{
    /// <summary>计划尝试 ID。</summary>
    public string PlanId { get; }
    /// <summary>HTTP 请求 ID。</summary>
    public string RequestId { get; }
    /// <summary>Planning Snapshot ID。</summary>
    public string SnapshotId { get; }
    /// <summary>单调规划代际。</summary>
    public long Generation { get; }

    /// <summary>创建不可变 Planning Call identity。</summary>
    public PlanningCallIdentity(string planId, string requestId, string snapshotId, long generation)
    {
        PlanId = planId ?? string.Empty;
        RequestId = requestId ?? string.Empty;
        SnapshotId = snapshotId ?? string.Empty;
        Generation = Math.Max(0L, generation);
    }
}

/// <summary>
/// 保存模型已选择路线及当前执行步的不可变计划。
/// </summary>
public sealed class RoutePlan
{
    private readonly IReadOnlyList<string> remainingSteps;

    /// <summary>实际采用计划的唯一 ID。</summary>
    public string PlanId { get; }
    /// <summary>被模型选择的 route_id。</summary>
    public string SelectedRouteId { get; }
    /// <summary>创建计划的 snapshot_id。</summary>
    public string PlanningSnapshotId { get; }
    /// <summary>计划代际。</summary>
    public long Generation { get; }
    /// <summary>计划绑定的任务状态版本。</summary>
    public int TaskStateVersion { get; }
    /// <summary>计划绑定的 Calibration ID。</summary>
    public string CalibrationId { get; }
    /// <summary>计划绑定的 Physical Space ID。</summary>
    public string PhysicalSpaceId { get; }
    /// <summary>计划创建时的风险快照 ID。</summary>
    public string RiskSnapshotId { get; }
    /// <summary>计划创建时的风险修订号。</summary>
    public int RiskRevision { get; }
    /// <summary>计划绑定的最终目标。</summary>
    public string FinalTargetId { get; }
    /// <summary>计划绑定的 capability identity。</summary>
    public string CapabilityIdentity { get; }
    /// <summary>计划绑定的 Target Registry 修订号。</summary>
    public int RegistryRevision { get; }
    /// <summary>后端为已选路线生成的叙事文本，不包含执行或安全命令。</summary>
    public string Narration { get; }
    /// <summary>当前正在执行、尚未确认到达的节点 ID。</summary>
    public string ActiveStepId { get; }
    /// <summary>当前步骤之后尚未提交的路线节点。</summary>
    public IReadOnlyList<string> RemainingSteps => remainingSteps;
    /// <summary>是否存在可在到达后继续提交的下一步。</summary>
    public bool HasRemainingSteps => remainingSteps.Count > 0;

    /// <summary>创建不可变 RoutePlan。</summary>
    public RoutePlan(
        string planId,
        string selectedRouteId,
        string planningSnapshotId,
        long generation,
        int taskStateVersion,
        string calibrationId,
        string physicalSpaceId,
        string riskSnapshotId,
        int riskRevision,
        string finalTargetId,
        string capabilityIdentity,
        int registryRevision,
        string narration,
        string activeStepId,
        IEnumerable<string> remainingNodeIds)
    {
        PlanId = planId ?? string.Empty;
        SelectedRouteId = selectedRouteId ?? string.Empty;
        PlanningSnapshotId = planningSnapshotId ?? string.Empty;
        Generation = Math.Max(0L, generation);
        TaskStateVersion = Math.Max(0, taskStateVersion);
        CalibrationId = calibrationId ?? string.Empty;
        PhysicalSpaceId = physicalSpaceId ?? string.Empty;
        RiskSnapshotId = riskSnapshotId ?? string.Empty;
        RiskRevision = Math.Max(0, riskRevision);
        FinalTargetId = finalTargetId ?? string.Empty;
        CapabilityIdentity = capabilityIdentity ?? string.Empty;
        RegistryRevision = Math.Max(0, registryRevision);
        Narration = narration ?? string.Empty;
        ActiveStepId = activeStepId ?? string.Empty;
        remainingSteps = Array.AsReadOnly(new List<string>(remainingNodeIds ?? Array.Empty<string>()).ToArray());
    }

    /// <summary>由已选择 RouteCandidate 创建正在执行第一步的计划。</summary>
    public static RoutePlan FromSelection(
        PlanningCallIdentity identity,
        RouteCandidate route,
        RdwPlanningSnapshot snapshot,
        int committedTaskStateVersion,
        string narration)
    {
        var remaining = new List<string>();
        for (var index = 1; route != null && index < route.OrderedNodeIds.Count; index++)
        {
            remaining.Add(route.OrderedNodeIds[index]);
        }
        return new RoutePlan(
            identity?.PlanId,
            route?.RouteId,
            snapshot?.SnapshotId,
            identity?.Generation ?? 0L,
            committedTaskStateVersion,
            snapshot?.CalibrationId,
            snapshot?.PhysicalSpaceId,
            snapshot?.RiskSnapshotId,
            snapshot?.RiskRevision ?? 0,
            snapshot?.FinalTargetId,
            snapshot?.Capability?.Identity,
            snapshot?.RegistryRevision ?? 0,
            narration,
            route?.FirstTargetId,
            remaining);
    }

    /// <summary>检查计划是否仍匹配最新任务、空间和能力身份。</summary>
    public bool Matches(RdwPlanningSnapshot snapshot, int currentTaskStateVersion)
    {
        return snapshot != null &&
               TaskStateVersion == currentTaskStateVersion &&
               string.Equals(CalibrationId, snapshot.CalibrationId, StringComparison.Ordinal) &&
               string.Equals(PhysicalSpaceId, snapshot.PhysicalSpaceId, StringComparison.Ordinal) &&
               string.Equals(FinalTargetId, snapshot.FinalTargetId, StringComparison.Ordinal) &&
               string.Equals(CapabilityIdentity, snapshot.Capability?.Identity, StringComparison.Ordinal) &&
               RegistryRevision == snapshot.RegistryRevision;
    }

    /// <summary>确认当前活动步骤已到达，并把计划重绑到预期任务版本。</summary>
    public RoutePlan ConfirmStepCompleted(string arrivedNodeId, int nextTaskStateVersion)
    {
        if (string.IsNullOrWhiteSpace(ActiveStepId) ||
            !string.Equals(ActiveStepId, arrivedNodeId, StringComparison.Ordinal))
        {
            return null;
        }
        return Copy(nextTaskStateVersion, string.Empty, remainingSteps);
    }

    /// <summary>提交 RemainingSteps 的第一项成为活动步骤。</summary>
    public RoutePlan CommitNextStep(string expectedNodeId, int nextTaskStateVersion)
    {
        if (!string.IsNullOrWhiteSpace(ActiveStepId) || remainingSteps.Count == 0 ||
            !string.Equals(remainingSteps[0], expectedNodeId, StringComparison.Ordinal))
        {
            return null;
        }
        var nextRemaining = new List<string>();
        for (var index = 1; index < remainingSteps.Count; index++)
        {
            nextRemaining.Add(remainingSteps[index]);
        }
        return Copy(nextTaskStateVersion, expectedNodeId, nextRemaining);
    }

    private RoutePlan Copy(int stateVersion, string activeStepId, IEnumerable<string> remaining)
    {
        return new RoutePlan(
            PlanId,
            SelectedRouteId,
            PlanningSnapshotId,
            Generation,
            stateVersion,
            CalibrationId,
            PhysicalSpaceId,
            RiskSnapshotId,
            RiskRevision,
            FinalTargetId,
            CapabilityIdentity,
            RegistryRevision,
            Narration,
            activeStepId,
            remaining);
    }
}

/// <summary>
/// 缓存已选择路线的剩余步骤；缓存命中永远不代表安全许可。
/// </summary>
public sealed class PlanCache
{
    /// <summary>当前活动计划；不存在时为 null。</summary>
    public RoutePlan ActivePlan { get; private set; }
    /// <summary>最近一次失效原因。</summary>
    public RoutePlanInvalidationReason LastInvalidationReason { get; private set; }

    /// <summary>保存新的活动计划。</summary>
    public void Store(RoutePlan plan)
    {
        ActivePlan = plan;
        LastInvalidationReason = RoutePlanInvalidationReason.None;
    }

    /// <summary>按最新身份读取缓存；调用方仍必须重新执行 Exact Safety。</summary>
    public bool TryGet(
        RdwPlanningSnapshot snapshot,
        int currentTaskStateVersion,
        out RoutePlan plan)
    {
        plan = ActivePlan;
        if (plan == null || !plan.Matches(snapshot, currentTaskStateVersion))
        {
            if (plan != null) Invalidate(ResolveMismatch(plan, snapshot, currentTaskStateVersion));
            plan = null;
            return false;
        }
        return true;
    }

    /// <summary>确认当前活动步骤到达。</summary>
    public bool ConfirmStepCompleted(string arrivedNodeId, int nextTaskStateVersion)
    {
        var next = ActivePlan?.ConfirmStepCompleted(arrivedNodeId, nextTaskStateVersion);
        if (next == null) return false;
        ActivePlan = next;
        return true;
    }

    /// <summary>提交缓存下一步；提交前的 Exact Safety 由调用方负责。</summary>
    public bool CommitNextStep(string expectedNodeId, int nextTaskStateVersion)
    {
        var next = ActivePlan?.CommitNextStep(expectedNodeId, nextTaskStateVersion);
        if (next == null) return false;
        ActivePlan = next;
        return true;
    }

    /// <summary>确定性废弃活动计划。</summary>
    public void Invalidate(RoutePlanInvalidationReason reason)
    {
        ActivePlan = null;
        LastInvalidationReason = reason;
    }

    private static RoutePlanInvalidationReason ResolveMismatch(
        RoutePlan plan,
        RdwPlanningSnapshot snapshot,
        int taskStateVersion)
    {
        if (snapshot == null || plan.TaskStateVersion != taskStateVersion)
            return RoutePlanInvalidationReason.TaskStateChanged;
        if (!string.Equals(plan.CalibrationId, snapshot.CalibrationId, StringComparison.Ordinal))
            return RoutePlanInvalidationReason.CalibrationChanged;
        if (!string.Equals(plan.PhysicalSpaceId, snapshot.PhysicalSpaceId, StringComparison.Ordinal))
            return RoutePlanInvalidationReason.PhysicalSpaceChanged;
        if (!string.Equals(plan.FinalTargetId, snapshot.FinalTargetId, StringComparison.Ordinal))
            return RoutePlanInvalidationReason.FinalTargetChanged;
        if (!string.Equals(plan.CapabilityIdentity, snapshot.Capability?.Identity, StringComparison.Ordinal))
            return RoutePlanInvalidationReason.CapabilityChanged;
        return RoutePlanInvalidationReason.RegistryChanged;
    }
}

/// <summary>
/// 门控单个在途路线选择请求，并拒绝旧 plan_id、snapshot_id 或 generation 响应。
/// </summary>
public sealed class PlanningCallGate
{
    private long generation;

    /// <summary>当前是否有在途路线选择请求。</summary>
    public bool RequestInFlight => ActiveIdentity != null;
    /// <summary>当前在途请求身份。</summary>
    public PlanningCallIdentity ActiveIdentity { get; private set; }
    /// <summary>最近分配或失效后的规划代际。</summary>
    public long Generation => generation;

    /// <summary>为新 snapshot 创建唯一 plan_id 和 request_id；已有请求时返回 null。</summary>
    public PlanningCallIdentity TryBegin(RdwPlanningSnapshot snapshot)
    {
        if (snapshot == null || !snapshot.IsValid || RequestInFlight) return null;
        generation++;
        ActiveIdentity = new PlanningCallIdentity(
            $"plan-{Guid.NewGuid():N}",
            $"planning-request-{Guid.NewGuid():N}",
            snapshot.SnapshotId,
            generation);
        return ActiveIdentity;
    }

    /// <summary>检查响应是否属于当前唯一在途请求。</summary>
    public bool Matches(string planId, string requestId, string snapshotId, long responseGeneration)
    {
        return ActiveIdentity != null &&
               ActiveIdentity.Generation == responseGeneration &&
               string.Equals(ActiveIdentity.PlanId, planId, StringComparison.Ordinal) &&
               string.Equals(ActiveIdentity.RequestId, requestId, StringComparison.Ordinal) &&
               string.Equals(ActiveIdentity.SnapshotId, snapshotId, StringComparison.Ordinal);
    }

    /// <summary>完成匹配的在途请求。</summary>
    public bool Complete(string planId, string requestId, string snapshotId, long responseGeneration)
    {
        if (!Matches(planId, requestId, snapshotId, responseGeneration)) return false;
        ActiveIdentity = null;
        return true;
    }

    /// <summary>废弃在途请求并推进代际，使旧响应永久过期。</summary>
    public void Invalidate()
    {
        generation++;
        ActiveIdentity = null;
    }
}
