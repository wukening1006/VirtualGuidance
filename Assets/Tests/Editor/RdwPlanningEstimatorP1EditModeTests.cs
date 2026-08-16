using System;
using NUnit.Framework;
using UnityEngine;

/// <summary>验证 Step 2E/2F 规划估算器绑定、纯查询边界和事实身份。</summary>
public sealed class RdwPlanningEstimatorP1EditModeTests
{
    /// <summary>验证三种启动模式绑定到真实对应的估算器身份。</summary>
    [TestCase(ContinuousRedirectorMode.S2C, "S2C", "s2c-planning")]
    [TestCase(ContinuousRedirectorMode.S2O, "S2O", "s2o-planning")]
    [TestCase(ContinuousRedirectorMode.ThomasAPF, "ThomasAPF", "thomas-apf-planning")]
    public void EstimatorFactory_BindsRedirectorAndCapability(
        ContinuousRedirectorMode mode,
        string redirectorType,
        string estimatorId)
    {
        var estimator = RdwPlanningCostEstimatorFactory.Create(
            mode,
            true,
            new RdwPlanningFixedDirectionEvaluator(3f, HeadingSafety.Safe),
            10f,
            1.5f,
            0.25f,
            12f,
            ThomasApfForceSettings.Default);
        var capability = new RdwCapabilityProfile(
            redirectorType,
            "SafeHeadingMapping",
            true,
            true,
            false,
            "test-limits",
            estimator.EstimatorId,
            estimator.EstimatorVersion);

        Assert.That(estimator.EstimatorId, Is.EqualTo(estimatorId));
        Assert.That(RdwPlanningCostEstimatorFactory.RedirectorType(mode, true), Is.EqualTo(redirectorType));
        Assert.That(capability.ContinuousRedirectorType, Is.EqualTo(redirectorType));
        Assert.That(capability.EstimatorId, Is.EqualTo(estimator.EstimatorId));
        Assert.That(capability.EstimatorVersion, Is.EqualTo(estimator.EstimatorVersion));
        Assert.That(capability.Identity, Is.Not.Empty);
    }

    /// <summary>验证相同冻结快照下三个估算器均为确定性纯查询。</summary>
    [TestCase(ContinuousRedirectorMode.S2C)]
    [TestCase(ContinuousRedirectorMode.S2O)]
    [TestCase(ContinuousRedirectorMode.ThomasAPF)]
    public void Estimators_AreDeterministicAndDoNotMutateRuntimeState(ContinuousRedirectorMode mode)
    {
        var evaluator = new RdwPlanningFixedDirectionEvaluator(3f, HeadingSafety.Safe);
        var estimator = RdwPlanningCostEstimatorFactory.Create(
            mode,
            true,
            evaluator,
            10f,
            1.5f,
            0.25f,
            12f,
            new ThomasApfForceSettings(
                boundaryInfluenceRadiusMeters: 1.5f,
                obstacleInfluenceRadiusMeters: 2f,
                userRadiusMeters: 0.25f,
                safetyMarginMeters: 0.15f));
        var snapshot = RdwPlanningTestFactory.CreateSnapshot(
            estimator.EstimatorId,
            estimator.EstimatorVersion,
            physicalPosition: new Vector2(2f, 0f),
            physicalForward: Vector2.up,
            obstacles: new[]
            {
                new PhysicalRiskObstacleSnapshot("obstacle", new Vector2(3f, 0f), 0.25f)
            });
        var traversal = RdwPlanningTraversalState.FromSnapshot(snapshot);
        var from = RdwPlanningTestFactory.Node("start", Vector2.zero);
        var to = RdwPlanningTestFactory.Node("target", new Vector2(2f, 3f));
        var redirector = new SteerToOrbitRedirector();
        var beforeSide = redirector.ActiveOrbitSide;
        var beforeDiagnostics = redirector.LastDiagnostics;

        var first = estimator.EvaluateEdge(snapshot, traversal, from, to, snapshot.Capability);
        var second = estimator.EvaluateEdge(snapshot, traversal, from, to, snapshot.Capability);

        Assert.That(second.RelativeYawDegrees, Is.EqualTo(first.RelativeYawDegrees).Within(0.0001f));
        Assert.That(second.RdwBenefit, Is.EqualTo(first.RdwBenefit).Within(0.0001f));
        Assert.That(second.GainCost, Is.EqualTo(first.GainCost).Within(0.0001f));
        Assert.That(second.ResetPressure, Is.EqualTo(first.ResetPressure).Within(0.0001f));
        Assert.That(redirector.ActiveOrbitSide, Is.EqualTo(beforeSide));
        Assert.That(redirector.LastDiagnostics.RedirectorType, Is.EqualTo(beforeDiagnostics.RedirectorType));
        Assert.That(evaluator.CallCount, Is.EqualTo(2));
    }

    /// <summary>验证 S2C、S2O、Thomas APF 不再共享同一套 S2C cost。</summary>
    [Test]
    public void Estimators_ProduceAlgorithmSpecificCosts()
    {
        var s2c = Evaluate(ContinuousRedirectorMode.S2C);
        var s2o = Evaluate(ContinuousRedirectorMode.S2O);
        var apf = Evaluate(ContinuousRedirectorMode.ThomasAPF);

        Assert.That(
            Mathf.Abs(s2c.RdwBenefit - s2o.RdwBenefit) +
            Mathf.Abs(s2o.RdwBenefit - apf.RdwBenefit) +
            Mathf.Abs(s2c.GainCost - apf.GainCost),
            Is.GreaterThan(0.01f));
    }

    /// <summary>验证所有估算器产生的 Unsafe/Critical 边都仍由 Hard Filter 拒绝。</summary>
    [TestCase(ContinuousRedirectorMode.S2C, HeadingSafety.Unsafe)]
    [TestCase(ContinuousRedirectorMode.S2C, HeadingSafety.Critical)]
    [TestCase(ContinuousRedirectorMode.S2O, HeadingSafety.Unsafe)]
    [TestCase(ContinuousRedirectorMode.S2O, HeadingSafety.Critical)]
    [TestCase(ContinuousRedirectorMode.ThomasAPF, HeadingSafety.Unsafe)]
    [TestCase(ContinuousRedirectorMode.ThomasAPF, HeadingSafety.Critical)]
    public void HardSafety_RemainsAuthoritative(ContinuousRedirectorMode mode, HeadingSafety safety)
    {
        var estimator = RdwPlanningCostEstimatorFactory.Create(
            mode,
            true,
            new RdwPlanningFixedDirectionEvaluator(3f, safety),
            10f,
            1.5f,
            0.25f,
            12f,
            ThomasApfForceSettings.Default);
        var snapshot = RdwPlanningTestFactory.CreateSnapshot(estimator.EstimatorId, estimator.EstimatorVersion);
        var evaluation = estimator.EvaluateEdge(
            snapshot,
            RdwPlanningTraversalState.FromSnapshot(snapshot),
            RdwPlanningTestFactory.Node("start", Vector2.zero),
            RdwPlanningTestFactory.Node("target", new Vector2(2f, 2f)),
            snapshot.Capability);

        Assert.That(evaluation.Feasible, Is.False);
        Assert.That(
            new RdwHardSafetyFilter(1.2f, 1f).Allows(
                new PlanningEdge("start", "target", 2f, true),
                evaluation,
                out _),
            Is.False);
    }

    /// <summary>验证 APF 使用共享空间、校准和参与者半径事实，而影响余量不改变 Safety。</summary>
    [Test]
    public void Apf_UsesSharedGeometryFactsAndKeepsTuningSeparateFromSafety()
    {
        var obstacle = new PhysicalRiskObstacleSnapshot("center", new Vector2(3f, 0f), 0.25f);
        var snapshot = RdwPlanningTestFactory.CreateSnapshot(
            "thomas-apf-planning",
            "1.0.0",
            physicalPosition: new Vector2(2f, 0f),
            physicalForward: Vector2.up,
            obstacles: new[] { obstacle });
        Assert.That(snapshot.Geometry.PhysicalSpaceId, Is.EqualTo(snapshot.RiskState.physical_space));
        Assert.That(snapshot.Geometry.CalibrationId, Is.EqualTo(snapshot.RiskState.calibration_id));
        Assert.That(snapshot.Geometry.UserRadiusMeters, Is.EqualTo(snapshot.RiskState.user_radius_meters));
        Assert.That(snapshot.Geometry.Obstacles.Count, Is.EqualTo(snapshot.RiskState.obstacle_context.Count));

        var evaluator = new RdwPlanningFixedDirectionEvaluator(2f, HeadingSafety.Safe);
        var lowMargin = new ThomasApfPlanningCostEstimator(
            evaluator,
            new ThomasApfForceSettings(
                boundaryInfluenceRadiusMeters: 0f,
                obstacleInfluenceRadiusMeters: 2f,
                userRadiusMeters: 0.99f,
                safetyMarginMeters: 0f));
        var highMargin = new ThomasApfPlanningCostEstimator(
            evaluator,
            new ThomasApfForceSettings(
                boundaryInfluenceRadiusMeters: 0f,
                obstacleInfluenceRadiusMeters: 2f,
                userRadiusMeters: 0.01f,
                safetyMarginMeters: 0.5f));
        var from = RdwPlanningTestFactory.Node("start", Vector2.zero);
        var to = RdwPlanningTestFactory.Node("target", new Vector2(2f, 1f));
        var traversal = RdwPlanningTraversalState.FromSnapshot(snapshot);
        var first = lowMargin.EvaluateEdge(snapshot, traversal, from, to, snapshot.Capability);
        var second = highMargin.EvaluateEdge(snapshot, traversal, from, to, snapshot.Capability);

        Assert.That(first.SafetyLevel, Is.EqualTo(HeadingSafety.Safe));
        Assert.That(second.SafetyLevel, Is.EqualTo(HeadingSafety.Safe));
    }

    /// <summary>验证 Redirector/Estimator capability 变化会使旧 RoutePlan 失效。</summary>
    [Test]
    public void CapabilityChange_InvalidatesExistingPlan()
    {
        var s2cSnapshot = RdwPlanningTestFactory.CreateSnapshot("s2c-planning", "1.0.0");
        var s2oSnapshot = RdwPlanningTestFactory.CreateSnapshot("s2o-planning", "1.0.0");
        var route = new RouteCandidate(
            "route-test",
            new[] { "goal" },
            new[] { new RdwEdgeEvaluation(0f, Vector2.up, Vector2.up, 3f, HeadingSafety.Safe, 0.5f, 0.2f, true, string.Empty, 0.1f) },
            1f,
            InterventionLevel.Low,
            s2cSnapshot.SnapshotId,
            s2cSnapshot.Capability.EstimatorId,
            s2cSnapshot.Capability.EstimatorVersion);
        var cache = new PlanCache();
        cache.Store(RoutePlan.FromSelection(
            new PlanningCallIdentity("plan", "request", s2cSnapshot.SnapshotId, 1),
            route,
            s2cSnapshot,
            s2cSnapshot.TaskStateVersion,
            string.Empty));

        Assert.That(cache.TryGet(s2oSnapshot, s2oSnapshot.TaskStateVersion, out _), Is.False);
        Assert.That(cache.LastInvalidationReason, Is.EqualTo(RoutePlanInvalidationReason.CapabilityChanged));
    }

    private static RdwEdgeEvaluation Evaluate(ContinuousRedirectorMode mode)
    {
        var estimator = RdwPlanningCostEstimatorFactory.Create(
            mode,
            true,
            new RdwPlanningFixedDirectionEvaluator(3f, HeadingSafety.Safe),
            10f,
            1.5f,
            0.25f,
            12f,
            new ThomasApfForceSettings(
                boundaryInfluenceRadiusMeters: 0f,
                obstacleInfluenceRadiusMeters: 2f,
                userRadiusMeters: 0.22f,
                safetyMarginMeters: 0.15f));
        var snapshot = RdwPlanningTestFactory.CreateSnapshot(
            estimator.EstimatorId,
            estimator.EstimatorVersion,
            physicalPosition: new Vector2(2f, 0f),
            physicalForward: Vector2.up,
            obstacles: new[]
            {
                new PhysicalRiskObstacleSnapshot("obstacle", new Vector2(3f, 0f), 0.25f)
            });
        return estimator.EvaluateEdge(
            snapshot,
            RdwPlanningTraversalState.FromSnapshot(snapshot),
            RdwPlanningTestFactory.Node("start", Vector2.zero),
            RdwPlanningTestFactory.Node("target", new Vector2(2f, 3f)),
            snapshot.Capability);
    }
}
