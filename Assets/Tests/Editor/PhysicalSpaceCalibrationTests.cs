using System.IO;
using NUnit.Framework;
using UnityEngine;

/// <summary>
/// 验证物理空间校准配置、安全门和 Tracking-to-Room 变换。
/// </summary>
public sealed class PhysicalSpaceCalibrationTests
{
    private static readonly string[] ConfigFileNames =
    {
        "physical_wide_room.json",
        "physical_narrow_room.json",
        "physical_obstacle_room.json",
        "physical_l_shaped_room.json"
    };

    /// <summary>四种正式物理空间都必须提供有限的专属校准位姿。</summary>
    [Test]
    public void PhysicalSpaceConfigs_ContainCalibrationPose()
    {
        foreach (var fileName in ConfigFileNames)
        {
            var path = Path.Combine(Application.dataPath, "Configs", fileName);
            Assert.That(File.Exists(path), Is.True, path);
            Assert.That(
                PhysicalSpaceConfigParser.TryParse(File.ReadAllText(path), out var config, out var error),
                Is.True,
                error);
            Assert.That(config.calibration, Is.Not.Null, fileName);
            Assert.That(float.IsNaN(config.calibration.point.x), Is.False, fileName);
            Assert.That(float.IsNaN(config.calibration.point.y), Is.False, fileName);
            Assert.That(float.IsNaN(config.calibration.forwardYawDegrees), Is.False, fileName);
        }
    }

    /// <summary>校准成功后，当前 Raw Pose 应与当前空间的已知 Room Pose 完全对齐。</summary>
    [Test]
    public void CalibrateCurrentPose_AlignsRawPoseToSpaceCalibrationPose()
    {
        var fixture = CreateFixture();
        try
        {
            Assert.That(fixture.Manager.SetPhysicalSpace("narrow_room"), Is.True);
            fixture.Calibration.UpdateRawTrackingPose(new Vector2(3.2f, -1.4f), 35f);

            Assert.That(fixture.Calibration.CalibrateCurrentPose(), Is.True);
            Assert.That(
                fixture.Tracker.PhysicalPositionXZ.x,
                Is.EqualTo(fixture.Manager.CurrentSpace.calibration.point.x).Within(0.0001f));
            Assert.That(
                fixture.Tracker.PhysicalPositionXZ.y,
                Is.EqualTo(fixture.Manager.CurrentSpace.calibration.point.y).Within(0.0001f));
            Assert.That(fixture.Tracker.PhysicalForwardXZ.x, Is.EqualTo(0f).Within(0.0001f));
            Assert.That(fixture.Tracker.PhysicalForwardXZ.y, Is.EqualTo(1f).Within(0.0001f));
        }
        finally
        {
            Object.DestroyImmediate(fixture.Root);
        }
    }

    /// <summary>PhysicalWalking 应将真实 HMD 局部位姿经校准后写入 Physical Room Pose。</summary>
    [Test]
    public void PhysicalWalking_AppliesCalibratedHeadsetPose()
    {
        var fixture = CreateFixture();
        try
        {
            var adapter = fixture.Root.AddComponent<XRTrackingAdapter>();
            adapter.Configure(fixture.Tracker, fixture.Calibration);
            adapter.SetInputMode(TrackingInputMode.PhysicalWalking);
            Assert.That(fixture.Manager.SetPhysicalSpace("narrow_room"), Is.True);
            var initialPosition = new Vector3(3.2f, 1.65f, -1.4f);
            var initialRotation = Quaternion.Euler(0f, 35f, 0f);

            Assert.That(adapter.ApplyTrackedPose(initialPosition, initialRotation), Is.True);
            Assert.That(adapter.ApplyTrackedPose(initialPosition, initialRotation), Is.True);
            Assert.That(adapter.IsTrackingActive, Is.True);
            Assert.That(fixture.Calibration.CalibrateCurrentPose(), Is.True);

            var nextPosition = initialPosition + new Vector3(0.4f, 0f, 0.25f);
            Assert.That(adapter.ApplyTrackedPose(nextPosition, initialRotation), Is.True);
            var expected = fixture.Calibration.TransformPositionToRoom(new Vector2(nextPosition.x, nextPosition.z));
            Assert.That(fixture.Tracker.PhysicalPositionXZ.x, Is.EqualTo(expected.x).Within(0.0001f));
            Assert.That(fixture.Tracker.PhysicalPositionXZ.y, Is.EqualTo(expected.y).Within(0.0001f));
        }
        finally
        {
            Object.DestroyImmediate(fixture.Root);
        }
    }

    /// <summary>校准点进入用户扩张后的边界或障碍物区域时必须拒绝校准。</summary>
    [TestCase(0.1f, 1f)]
    [TestCase(2f, 2.2f)]
    public void CalibrateCurrentPose_RejectsUnsafeCalibrationPoint(float pointX, float pointZ)
    {
        var fixture = CreateFixture();
        try
        {
            Assert.That(fixture.Manager.SetPhysicalSpace("obstacle_room"), Is.True);
            fixture.Manager.CurrentSpace.calibration.point = new Vector2(pointX, pointZ);
            fixture.Calibration.UpdateRawTrackingPose(Vector2.zero, 0f);

            Assert.That(fixture.Calibration.CalibrateCurrentPose(), Is.False);
            Assert.That(fixture.Calibration.IsCalibrationValid, Is.False);
        }
        finally
        {
            Object.DestroyImmediate(fixture.Root);
        }
    }

    private static CalibrationFixture CreateFixture()
    {
        var root = new GameObject("physical-calibration-tests");
        var manager = root.AddComponent<PhysicalSpaceManager>();
        var tracker = root.AddComponent<UserPoseTracker>();
        var calibration = root.AddComponent<PhysicalSpaceCalibration>();
        var evaluator = root.AddComponent<PhysicalRiskEvaluator>();
        calibration.Configure(manager, tracker, evaluator.RequiredPhysicalClearanceMeters);
        evaluator.Configure(manager, tracker, calibration);
        return new CalibrationFixture(root, manager, tracker, calibration);
    }

    private readonly struct CalibrationFixture
    {
        internal GameObject Root { get; }
        internal PhysicalSpaceManager Manager { get; }
        internal UserPoseTracker Tracker { get; }
        internal PhysicalSpaceCalibration Calibration { get; }

        internal CalibrationFixture(
            GameObject root,
            PhysicalSpaceManager manager,
            UserPoseTracker tracker,
            PhysicalSpaceCalibration calibration)
        {
            Root = root;
            Manager = manager;
            Tracker = tracker;
            Calibration = calibration;
        }
    }
}
