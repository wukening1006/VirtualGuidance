using System;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

/// <summary>
/// 验证连续目标方位与任意角度 Physical Heading 评估的第一阶段契约。
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

    /// <summary>验证风险快照只发布当前真实 Physical Forward 的精确安全事实。</summary>
    [Test]
    public void PhysicalRiskEvaluator_PublishesCurrentExactHeadingWithoutFixedProfile()
    {
        var root = new GameObject("PhysicalContinuousHeadingTest");
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
            Assert.That(risk.current_heading.RelativeYawDegrees, Is.EqualTo(0f).Within(0.001f));
            Assert.That(risk.current_heading.PhysicalForward.magnitude, Is.EqualTo(1f).Within(0.001f));
            Assert.That(risk.current_heading.ClearanceMeters, Is.GreaterThan(0f));
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(root);
        }
    }

    /// <summary>验证任意连续 physical direction 均直接返回精确净空，不经过最近 Profile 采样。</summary>
    [Test]
    public void PhysicalRiskEvaluator_EvaluatesArbitraryContinuousDirections()
    {
        var root = new GameObject("ExactDirectionQueryTest");
        try
        {
            CreateCalibrationRig(root, out var pose, out var space, out var calibration);
            calibration.SetCalibrationPoint(new Vector2(2.5f, 2.5f), 0f);
            calibration.UpdateRawTrackingPose(Vector2.zero, 0f);
            Assert.That(calibration.CalibrateCurrentPose(), Is.True);

            var evaluator = root.AddComponent<PhysicalRiskEvaluator>();
            evaluator.Configure(space, pose, calibration);
            var risk = evaluator.CurrentRiskState ?? evaluator.EvaluateNow();
            Assert.That(risk, Is.Not.Null);

            foreach (var yaw in new[] { 17.3f, 47.5f, 83.2f, -41.6f })
            {
                var direction = Rotate(Vector2.up, yaw);
                Assert.That(
                    evaluator.TryEvaluateAbsoluteHeading(risk, direction, out var sample),
                    Is.True,
                    $"exact heading {yaw} should be queryable");
                Assert.That(sample.RelativeYawDegrees, Is.EqualTo(yaw).Within(0.01f));
                Assert.That(sample.ClearanceMeters, Is.GreaterThan(0f));
            }
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(root);
        }
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
