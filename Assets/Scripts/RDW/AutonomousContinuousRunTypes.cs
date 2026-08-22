using System;
using UnityEngine;

/// <summary>
/// 标识碰撞预测对应的物理来源。
/// </summary>
public enum CollisionPredictionSourceType
{
    /// <summary>Physical Boundary。</summary>
    Boundary,
    /// <summary>静态物理障碍物。</summary>
    StaticObstacle,
    /// <summary>动态障碍物或其他参与者。</summary>
    DynamicObstacle,
    /// <summary>其他参与者 Avatar。</summary>
    Avatar
}

/// <summary>
/// 描述自动连续运行内部的物理恢复阶段；不会替代实验任务状态。
/// </summary>
public enum PhysicalRuntimeState
{
    /// <summary>尚未开始物理执行。</summary>
    Inactive,
    /// <summary>正在执行当前锁定目标。</summary>
    Executing,
    /// <summary>已收到新的碰撞预测。</summary>
    ResetRequired,
    /// <summary>正在执行独立物理 Reset。</summary>
    Resetting,
    /// <summary>Reset 完成后等待位姿稳定。</summary>
    Stabilizing,
    /// <summary>正在刷新物理事实与预测基线。</summary>
    RevalidatingPhysicalState,
    /// <summary>正在恢复同一个锁定目标。</summary>
    ResumingLockedTarget,
    /// <summary>Tracking 有效时处理越界事实。</summary>
    BoundaryRecovery,
    /// <summary>等待 Tracking 或 Calibration 自动恢复。</summary>
    TrackingRecovery,
    /// <summary>最终目标已完成。</summary>
    Completed
}

/// <summary>
/// 保存一条不可变的物理碰撞预测；只有新的有效预测可以启动自动 Reset。
/// </summary>
public sealed class CollisionPrediction
{
    /// <summary>本次预测的唯一 ID。</summary>
    public string PredictionId { get; }
    /// <summary>用于同一 Reset 生命周期内去重的稳定指纹。</summary>
    public string Fingerprint { get; }
    /// <summary>关联的物理风险修订号。</summary>
    public int RiskRevision { get; }
    /// <summary>关联的 Physical Space ID。</summary>
    public string PhysicalSpaceId { get; }
    /// <summary>碰撞来源类型。</summary>
    public CollisionPredictionSourceType SourceType { get; }
    /// <summary>碰撞来源 ID。</summary>
    public string SourceId { get; }
    /// <summary>预计到碰撞面的移动距离，单位：米。</summary>
    public float PredictedDistanceMeters { get; }
    /// <summary>预计到碰撞的时间，单位：秒。</summary>
    public float PredictedTimeSeconds { get; }
    /// <summary>预计碰撞点，Physical XZ，单位：米。</summary>
    public Vector2 CollisionPoint { get; }
    /// <summary>预测时的用户 Physical XZ 位置，单位：米。</summary>
    public Vector2 PhysicalPosition { get; }
    /// <summary>预测使用的实际 Physical 运动方向。</summary>
    public Vector2 PhysicalForward { get; }
    /// <summary>是否为当前预测基线之后的新预测。</summary>
    public bool IsNewPrediction { get; }
    /// <summary>预测是否具备启动自动 Reset 的完整身份。</summary>
    public bool IsValid => IsNewPrediction &&
                           !string.IsNullOrWhiteSpace(PredictionId) &&
                           !string.IsNullOrWhiteSpace(Fingerprint) &&
                           !string.IsNullOrWhiteSpace(PhysicalSpaceId) &&
                           !string.IsNullOrWhiteSpace(SourceId);

    /// <summary>创建一条不可变碰撞预测。</summary>
    public CollisionPrediction(
        string predictionId,
        string fingerprint,
        int riskRevision,
        string physicalSpaceId,
        CollisionPredictionSourceType sourceType,
        string sourceId,
        float predictedDistanceMeters,
        float predictedTimeSeconds,
        Vector2 collisionPoint,
        Vector2 physicalPosition,
        Vector2 physicalForward,
        bool isNewPrediction)
    {
        PredictionId = predictionId ?? string.Empty;
        Fingerprint = fingerprint ?? string.Empty;
        RiskRevision = Math.Max(0, riskRevision);
        PhysicalSpaceId = physicalSpaceId ?? string.Empty;
        SourceType = sourceType;
        SourceId = sourceId ?? string.Empty;
        PredictedDistanceMeters = Mathf.Max(0f, predictedDistanceMeters);
        PredictedTimeSeconds = Mathf.Max(0f, predictedTimeSeconds);
        CollisionPoint = collisionPoint;
        PhysicalPosition = physicalPosition;
        PhysicalForward = physicalForward.sqrMagnitude > 0.0001f
            ? physicalForward.normalized
            : Vector2.up;
        IsNewPrediction = isNewPrediction;
    }
}

/// <summary>
/// 保存 Reset 前后必须保持不变的任务目标身份。
/// </summary>
public sealed class LockedTargetContext
{
    /// <summary>整个任务期间不可变的最终目标 ID。</summary>
    public string FinalTargetId { get; }
    /// <summary>当前正在执行且 Reset 不得替换的目标 ID。</summary>
    public string CurrentTargetId { get; }
    /// <summary>当前目标在导航系统中的职责。</summary>
    public NavigationTargetRole CurrentTargetRole { get; }
    /// <summary>当前目标 Reservation 的决策 ID。</summary>
    public string ReservationId { get; }
    /// <summary>当前 Guidance 请求身份；技术重试可以替换该值。</summary>
    public string GuidanceRequestId { get; }
    /// <summary>当前目标经历的 Reset 代际。</summary>
    public int ResetGeneration { get; }

    /// <summary>创建一份不可变目标锁快照。</summary>
    public LockedTargetContext(
        string finalTargetId,
        string currentTargetId,
        NavigationTargetRole currentTargetRole,
        string reservationId,
        string guidanceRequestId,
        int resetGeneration)
    {
        FinalTargetId = finalTargetId ?? string.Empty;
        CurrentTargetId = currentTargetId ?? string.Empty;
        CurrentTargetRole = currentTargetRole;
        ReservationId = reservationId ?? string.Empty;
        GuidanceRequestId = guidanceRequestId ?? string.Empty;
        ResetGeneration = Math.Max(0, resetGeneration);
    }
}
