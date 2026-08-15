using Newtonsoft.Json;
using NUnit.Framework;
using UnityEngine;

/// <summary>
/// 验证 route/plan identity、PlanCache、PlanningCallGate 与严格跨端协议。
/// </summary>
public sealed class RdwPlanLifecycleEditModeTests
{
    /// <summary>验证单飞门拒绝旧 generation，并在失效后生成新 plan_id。</summary>
    [Test]
    public void PlanningCallGate_RejectsStaleResponseAfterInvalidation()
    {
        var gate = new PlanningCallGate();
        var snapshot = RdwPlanningTestFactory.CreateSnapshot("fake", "1");
        var first = gate.TryBegin(snapshot);

        gate.Invalidate();
        var second = gate.TryBegin(snapshot);

        Assert.That(gate.Matches(first.PlanId, first.RequestId, first.SnapshotId, first.Generation), Is.False);
        Assert.That(second.PlanId, Is.Not.EqualTo(first.PlanId));
        Assert.That(second.Generation, Is.GreaterThan(first.Generation));
    }

    /// <summary>验证 Reset 失效后只能由 Fresh Snapshot 获得新的 plan_id。</summary>
    [Test]
    public void ResetInvalidation_RequiresFreshSnapshotAndNewPlanIdentity()
    {
        var gate = new PlanningCallGate();
        var cache = new PlanCache();
        var beforeReset = RdwPlanningTestFactory.CreateSnapshot(
            "fake",
            "1",
            snapshotId: "snapshot-before-reset");
        var oldIdentity = gate.TryBegin(beforeReset);
        cache.Store(RoutePlan.FromSelection(
            oldIdentity,
            CreateRoute(beforeReset),
            beforeReset,
            4,
            "路线讲解"));

        gate.Invalidate();
        cache.Invalidate(RoutePlanInvalidationReason.ResetCompleted);
        var afterReset = RdwPlanningTestFactory.CreateSnapshot(
            "fake",
            "1",
            snapshotId: "snapshot-after-reset");
        var freshIdentity = gate.TryBegin(afterReset);

        Assert.That(cache.ActivePlan, Is.Null);
        Assert.That(afterReset.SnapshotId, Is.Not.EqualTo(beforeReset.SnapshotId));
        Assert.That(freshIdentity.PlanId, Is.Not.EqualTo(oldIdentity.PlanId));
        Assert.That(freshIdentity.Generation, Is.GreaterThan(oldIdentity.Generation));
        Assert.That(gate.Matches(
            oldIdentity.PlanId,
            oldIdentity.RequestId,
            oldIdentity.SnapshotId,
            oldIdentity.Generation), Is.False);
    }

    /// <summary>验证缓存只在预期到达后推进，并可由 Reset 确定性失效。</summary>
    [Test]
    public void PlanCache_AdvancesOnlyAfterArrivalAndInvalidatesForReset()
    {
        var snapshot = RdwPlanningTestFactory.CreateSnapshot("fake", "1");
        var route = CreateRoute(snapshot);
        var identity = new PlanningCallIdentity("plan-1", "request-1", snapshot.SnapshotId, 1);
        var cache = new PlanCache();
        cache.Store(RoutePlan.FromSelection(identity, route, snapshot, 4, "路线讲解"));

        Assert.That(cache.ConfirmStepCompleted("wrong", 5), Is.False);
        Assert.That(cache.ConfirmStepCompleted("a", 5), Is.True);
        Assert.That(cache.CommitNextStep("b", 6), Is.True);

        cache.Invalidate(RoutePlanInvalidationReason.ResetStarted);
        Assert.That(cache.ActivePlan, Is.Null);
        Assert.That(cache.LastInvalidationReason, Is.EqualTo(RoutePlanInvalidationReason.ResetStarted));
    }

    /// <summary>验证 Unity 仅接受冻结候选中的 route_id，且拒绝夹带 gain/reset/safety 字段。</summary>
    [Test]
    public void RouteSelectionProtocol_IsMembershipConstrainedAndForbidsExtraControlFields()
    {
        var snapshot = RdwPlanningTestFactory.CreateSnapshot("fake", "1");
        var route = CreateRoute(snapshot);
        var result = new RdwRoutePlanningResult(
            snapshot,
            new LocalNavigationGraph(snapshot.SnapshotId, null, null),
            new[] { route },
            null);
        var identity = new PlanningCallIdentity("plan-1", "request-1", snapshot.SnapshotId, 1);
        var request = RdwRouteSelectionRequest.From("session-1", identity, result);
        var validJson = JsonConvert.SerializeObject(new
        {
            planning_schema_version = RdwPlanningProtocolVersions.RouteSelectionSchema,
            session_id = "session-1",
            request_id = "request-1",
            plan_id = "plan-1",
            snapshot_id = snapshot.SnapshotId,
            generation = 1,
            route_id = route.RouteId,
            narration = "继续参观。",
            verifier = new { approved = true, reason = "membership verified" }
        });

        Assert.That(RdwRouteSelectionProtocol.TryParseResponse(
            validJson, request, out _, out _), Is.True);

        foreach (var forbiddenField in new[] { "gain", "reset", "safety" })
        {
            var forbiddenJson = validJson.TrimEnd('}') + $",\"{forbiddenField}\":true}}";
            Assert.That(RdwRouteSelectionProtocol.TryParseResponse(
                forbiddenJson, request, out _, out var extraReason), Is.False);
            Assert.That(extraReason, Does.Contain($"unexpected_response_field:{forbiddenField}"));
        }

        var requestDto = RdwRouteSelectionRequest.From("session-1", identity, result);
        Assert.That(requestDto.route_candidates[0].edges[0].from_node_id, Is.EqualTo(snapshot.CurrentNodeId));
    }

    /// <summary>验证 Unity 不接受依赖 JSON.NET 宽松转换的错误标量类型。</summary>
    [Test]
    public void RouteSelectionProtocol_RejectsWrongScalarTypes()
    {
        var snapshot = RdwPlanningTestFactory.CreateSnapshot("fake", "1");
        var result = new RdwRoutePlanningResult(
            snapshot,
            new LocalNavigationGraph(snapshot.SnapshotId, null, null),
            new[] { CreateRoute(snapshot) },
            null);
        var request = RdwRouteSelectionRequest.From(
            "session-1",
            new PlanningCallIdentity("plan-1", "request-1", snapshot.SnapshotId, 1),
            result);
        var wrongTypeJson = JsonConvert.SerializeObject(new
        {
            planning_schema_version = RdwPlanningProtocolVersions.RouteSelectionSchema,
            session_id = "session-1",
            request_id = "request-1",
            plan_id = "plan-1",
            snapshot_id = snapshot.SnapshotId,
            generation = "1",
            route_id = "route-test",
            narration = "继续参观。",
            verifier = new { approved = "true", reason = "membership verified" }
        });

        Assert.That(RdwRouteSelectionProtocol.TryParseResponse(
            wrongTypeJson, request, out _, out var failureReason), Is.False);
        Assert.That(failureReason, Does.StartWith("invalid_response_field_type:generation"));
    }

    private static RouteCandidate CreateRoute(RdwPlanningSnapshot snapshot)
    {
        var evaluations = new[]
        {
            Evaluation(new Vector2(0f, 1f)),
            Evaluation(new Vector2(1f, 0f))
        };
        return new RouteCandidate(
            "route-test",
            new[] { "a", "b" },
            evaluations,
            1.2f,
            InterventionLevel.Medium,
            snapshot.SnapshotId,
            "fake",
            "1");
    }

    private static RdwEdgeEvaluation Evaluation(Vector2 heading)
    {
        return new RdwEdgeEvaluation(
            0f,
            heading,
            heading,
            3f,
            HeadingSafety.Safe,
            0.5f,
            0.2f,
            true,
            string.Empty,
            0.1f);
    }
}
