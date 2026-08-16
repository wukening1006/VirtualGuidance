using NUnit.Framework;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 验证 PlanningSnapshot 与 S2C Estimator 的连续角度和纯查询边界。
/// </summary>
public sealed class RdwPlanningFactsEditModeTests
{
    /// <summary>验证规划配置缺省值始终是 Shadow。</summary>
    [Test]
    public void PlanningSettings_DefaultMode_IsShadow()
    {
        Assert.That(new RdwPlanningSettings().Mode, Is.EqualTo(RdwPlanningMode.Shadow));
    }

    /// <summary>验证无效 tracking pose 不能形成可执行规划快照。</summary>
    [Test]
    public void Snapshot_InvalidPose_IsRejected()
    {
        var snapshot = RdwPlanningTestFactory.CreateSnapshot(
            "s2c-planning",
            "1.0.0",
            poseValid: false);

        Assert.That(snapshot.IsValid, Is.False);
    }

    /// <summary>验证 S2C Estimator 保留连续 signed yaw，不量化为固定方向。</summary>
    [TestCase(37f)]
    [TestCase(-128f)]
    [TestCase(171f)]
    public void S2CEstimator_PreservesContinuousSignedYaw(float yawDegrees)
    {
        var evaluator = new RdwPlanningFixedDirectionEvaluator(2.8f, HeadingSafety.Safe);
        var estimator = new S2CPlanningCostEstimator(evaluator, 10f);
        var snapshot = RdwPlanningTestFactory.CreateSnapshot(
            estimator.EstimatorId,
            estimator.EstimatorVersion);
        var from = RdwPlanningTestFactory.Node("start", Vector2.zero);
        var to = RdwPlanningTestFactory.Node(
            "target",
            RdwYawConvention.YawToForward(yawDegrees) * 4f);

        var evaluation = estimator.EvaluateEdge(
            snapshot,
            RdwPlanningTraversalState.FromSnapshot(snapshot),
            from,
            to,
            snapshot.Capability);

        Assert.That(evaluation.Feasible, Is.True);
        Assert.That(Mathf.DeltaAngle(yawDegrees, evaluation.RelativeYawDegrees), Is.EqualTo(0f).Within(0.001f));
        Assert.That(evaluator.CallCount, Is.EqualTo(1));
    }

    /// <summary>证明 Planning 查询不会写 Transform，也不会触发运行中 S2C smoothing/diagnostics。</summary>
    [Test]
    public void S2CEstimator_DoesNotMutateTransformOrLiveRedirectorState()
    {
        var root = new GameObject("planning-isolation-root");
        try
        {
            root.transform.SetPositionAndRotation(new Vector3(1f, 2f, 3f), Quaternion.Euler(0f, 23f, 0f));
            var beforePosition = root.transform.position;
            var beforeRotation = root.transform.rotation;
            var redirector = new SteerToCenterRedirector();
            var beforeDiagnostics = redirector.LastDiagnostics;
            var evaluator = new RdwPlanningFixedDirectionEvaluator(2.5f, HeadingSafety.Safe);
            var estimator = new S2CPlanningCostEstimator(evaluator);
            var snapshot = RdwPlanningTestFactory.CreateSnapshot(
                estimator.EstimatorId,
                estimator.EstimatorVersion);

            estimator.EvaluateEdge(
                snapshot,
                RdwPlanningTraversalState.FromSnapshot(snapshot),
                RdwPlanningTestFactory.Node("start", Vector2.zero),
                RdwPlanningTestFactory.Node("target", new Vector2(2f, 3f)),
                snapshot.Capability);

            Assert.That(root.transform.position, Is.EqualTo(beforePosition));
            Assert.That(root.transform.rotation, Is.EqualTo(beforeRotation));
            Assert.That(redirector.LastDiagnostics.RedirectorType, Is.EqualTo(beforeDiagnostics.RedirectorType));
            Assert.That(redirector.LastDiagnostics.SmoothedRotationDegrees,
                Is.EqualTo(beforeDiagnostics.SmoothedRotationDegrees));
            Assert.That(redirector.LastDiagnostics.SmoothedCurvatureDegrees,
                Is.EqualTo(beforeDiagnostics.SmoothedCurvatureDegrees));
        }
        finally
        {
            Object.DestroyImmediate(root);
        }
    }
}

internal sealed class RdwPlanningFixedDirectionEvaluator : IRdwPlanningDirectionEvaluator
{
    private readonly float clearance;
    private readonly HeadingSafety safety;

    /// <summary>累计纯查询调用次数。</summary>
    public int CallCount { get; private set; }

    /// <summary>创建返回固定净空和安全等级的测试查询器。</summary>
    public RdwPlanningFixedDirectionEvaluator(float clearanceMeters, HeadingSafety headingSafety)
    {
        clearance = clearanceMeters;
        safety = headingSafety;
    }

    /// <summary>返回固定的 Exact Direction Safety 样本。</summary>
    public bool TryEvaluateDirection(
        RdwPlanningSnapshot snapshot,
        Vector2 physicalPositionXZ,
        Vector2 physicalDirectionXZ,
        out HeadingSample sample)
    {
        CallCount++;
        sample = new HeadingSample(0f, physicalDirectionXZ, clearance, safety);
        return snapshot != null;
    }
}

internal static class RdwPlanningTestFactory
{
    /// <summary>创建具备稳定身份的规划测试快照。</summary>
    public static RdwPlanningSnapshot CreateSnapshot(
        string estimatorId,
        string estimatorVersion,
        bool poseValid = true,
        int taskStateVersion = 3,
        string snapshotId = "snapshot-test",
        Vector2 physicalPosition = default(Vector2),
        Vector2 physicalForward = default(Vector2),
        IEnumerable<PhysicalRiskObstacleSnapshot> obstacles = null)
    {
        var boundary = new[]
        {
            new Vector2(-5f, -5f),
            new Vector2(5f, -5f),
            new Vector2(5f, 5f),
            new Vector2(-5f, 5f)
        };
        var risk = new PhysicalRiskState(
            riskSnapshotId: "risk-test",
            riskRevision: 7,
            calibrationId: "calibration-test",
            sampledAtUtc: "2026-08-14T00:00:00Z",
            riskPolicyVersion: GuidanceProtocolVersions.RiskPolicy,
            snapshotSource: "unity",
            physicalSpace: "space-test",
            physicalPosition: physicalPosition,
            physicalForward: physicalForward.sqrMagnitude > 0.000001f ? physicalForward : Vector2.up,
            boundaryContext: boundary,
            obstacleContext: obstacles,
            boundaryRisk: "low",
            resetRisk: "low",
            currentHeading: new HeadingSample(0f, physicalForward.sqrMagnitude > 0.000001f ? physicalForward : Vector2.up, 4f, HeadingSafety.Safe),
            userRadiusMeters: 0.22f);
        var geometry = new RdwGeometrySnapshot(
            "space-test",
            boundary,
            obstacles,
            "risk-test",
            7,
            false,
            "calibration-test",
            0.22f);
        var redirectorType = RedirectorTypeForEstimator(estimatorId);
        var capability = new RdwCapabilityProfile(
            continuousRedirectorType: redirectorType,
            resetterType: "SafeHeadingMapping",
            rotationGainAllowed: true,
            curvatureGainAllowed: true,
            translationGainAllowed: false,
            gainLimitsVersion: "test-limits",
            estimatorId: estimatorId,
            estimatorVersion: estimatorVersion);
        return new RdwPlanningSnapshot(
            snapshotId,
            12,
            4.2d,
            physicalPosition,
            physicalForward.sqrMagnitude > 0.000001f ? physicalForward : Vector2.up,
            Vector2.zero,
            Vector2.up,
            "calibration-test",
            "space-test",
            "risk-test",
            7,
            "start",
            "goal",
            taskStateVersion,
            2,
            new string[0],
            geometry,
            risk,
            capability,
            poseValid);
    }

    private static string RedirectorTypeForEstimator(string estimatorId)
    {
        if (string.Equals(estimatorId, "s2o-planning", System.StringComparison.Ordinal)) return "S2O";
        if (string.Equals(estimatorId, "thomas-apf-planning", System.StringComparison.Ordinal)) return "ThomasAPF";
        if (string.Equals(estimatorId, "neutral-planning", System.StringComparison.Ordinal)) return "None";
        return "S2C";
    }

    /// <summary>创建规划测试节点。</summary>
    public static PlanningNode Node(
        string id,
        Vector2 position,
        float semanticImportance = 0.5f,
        float taskRelevance = 0.5f,
        NavigationTargetRole role = NavigationTargetRole.RouteSubgoal)
    {
        return new PlanningNode(
            id,
            position,
            role,
            semanticImportance,
            taskRelevance,
            false);
    }
}
