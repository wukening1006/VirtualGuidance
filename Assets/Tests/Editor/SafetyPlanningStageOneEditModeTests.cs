using System;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

/// <summary>
/// 验证连续目标方位与多角度 Physical Heading Profile 的第一阶段契约。
/// </summary>
public sealed class SafetyPlanningStageOneEditModeTests
{
    /// <summary>验证目标方位始终包含连续偏航、距离与位姿修订。</summary>
    [Test]
    public void NavigationTargetManager_ReturnsContinuousBearingDistanceAndPoseRevision()
    {
        var root = new GameObject("ContinuousTargetBearingTest");
        try
        {
            var user = new GameObject("BearingUser");
            user.transform.SetParent(root.transform, false);
            var target = new GameObject("BearingTarget");
            target.transform.SetParent(root.transform, false);
            target.transform.localPosition = new Vector3(0f, 0f, 2f);
            var metadata = target.AddComponent<ExhibitMetadata>();
            metadata.Configure(
                "BearingTarget",
                "Bearing Target",
                Array.Empty<string>(),
                string.Empty,
                "test",
                Array.Empty<string>(),
                target.transform,
                target.transform);
            var pose = root.AddComponent<UserPoseTracker>();
            pose.ConfigureVirtualUserRoot(user.transform);
            var targets = root.AddComponent<NavigationTargetManager>();
            targets.Configure(pose);
            targets.RebuildIndex();

            Assert.That(targets.TryGetTargetBearing("BearingTarget", out var forwardBearing), Is.True);
            Assert.That(forwardBearing.RelativeYawDegrees, Is.EqualTo(0f).Within(0.001f));
            Assert.That(forwardBearing.DistanceMeters, Is.EqualTo(2f).Within(0.001f));
            Assert.That(forwardBearing.DisplayLabel, Is.EqualTo(BearingLabel.Forward));
            Assert.That(forwardBearing.PoseRevision, Is.GreaterThan(0));

            user.transform.forward = Vector3.back;
            Assert.That(targets.TryGetTargetBearing("BearingTarget", out var behindBearing), Is.True);
            Assert.That(Mathf.Abs(behindBearing.RelativeYawDegrees), Is.EqualTo(180f).Within(0.001f));
            Assert.That(behindBearing.DisplayLabel, Is.EqualTo(BearingLabel.Behind));
            Assert.That(behindBearing.PoseRevision, Is.GreaterThan(forwardBearing.PoseRevision));
            Assert.That(targets.GetTargetDirection("BearingTarget"), Is.EqualTo("behind"));
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(root);
        }
    }

    /// <summary>验证 Forward 显示标签使用不同的进入与退出阈值。</summary>
    [Test]
    public void NavigationTargetManager_UsesForwardLabelHysteresis()
    {
        var root = new GameObject("BearingHysteresisTest");
        try
        {
            var targets = root.AddComponent<NavigationTargetManager>();

            Assert.That(
                targets.ClassifyBearingLabel(24f, BearingLabel.Right, true),
                Is.EqualTo(BearingLabel.Forward));
            Assert.That(
                targets.ClassifyBearingLabel(34f, BearingLabel.Forward, true),
                Is.EqualTo(BearingLabel.Forward));
            Assert.That(
                targets.ClassifyBearingLabel(36f, BearingLabel.Forward, true),
                Is.EqualTo(BearingLabel.Right));
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(root);
        }
    }

    /// <summary>验证风险快照发布全部采样角度并保持旧三方向字段一致。</summary>
    [Test]
    public void PhysicalRiskEvaluator_PublishesMultiAngleCorridorHeadingProfile()
    {
        var root = new GameObject("PhysicalHeadingProfileTest");
        try
        {
            CreateCalibrationRig(root, out var pose, out var space, out var calibration);
            calibration.SetCalibrationPoint(new Vector2(2.5f, 2.5f), 0f);
            calibration.UpdateRawTrackingPose(Vector2.zero, 0f);
            Assert.That(calibration.CalibrateCurrentPose(), Is.True);
            var evaluator = root.AddComponent<PhysicalRiskEvaluator>();
            evaluator.Configure(space, pose, calibration);

            var risk = evaluator.EvaluateNow();

            Assert.That(risk, Is.Not.Null);
            Assert.That(risk.heading_profile, Has.Count.EqualTo(10));
            Assert.That(
                risk.heading_profile.Select(sample => sample.RelativeYawDegrees),
                Is.EquivalentTo(new[] { 0f, -30f, 30f, -60f, 60f, -90f, 90f, -135f, 135f, 180f }));
            Assert.That(risk.TryGetNearestHeading(30f, out var diagonalHeading), Is.True);
            Assert.That(diagonalHeading.RelativeYawDegrees, Is.EqualTo(30f));
            Assert.That(diagonalHeading.PhysicalForward.magnitude, Is.EqualTo(1f).Within(0.001f));
            Assert.That(risk.TryGetNearestHeading(0f, out var forwardHeading), Is.True);
            Assert.That(risk.TryGetNearestHeading(-90f, out var leftHeading), Is.True);
            Assert.That(risk.TryGetNearestHeading(90f, out var rightHeading), Is.True);
            Assert.That(risk.clearance_forward, Is.EqualTo(forwardHeading.ClearanceMeters).Within(0.001f));
            Assert.That(risk.clearance_left, Is.EqualTo(leftHeading.ClearanceMeters).Within(0.001f));
            Assert.That(risk.clearance_right, Is.EqualTo(rightHeading.ClearanceMeters).Within(0.001f));
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(root);
        }
    }

    private static void CreateCalibrationRig(
        GameObject root,
        out UserPoseTracker pose,
        out PhysicalSpaceManager space,
        out PhysicalSpaceCalibration calibration)
    {
        pose = root.AddComponent<UserPoseTracker>();
        space = root.AddComponent<PhysicalSpaceManager>();
        space.LoadSpaces();
        Assert.That(space.SetPhysicalSpace("wide_room"), Is.True);
        calibration = root.AddComponent<PhysicalSpaceCalibration>();
        calibration.Configure(space, pose);
    }
}
