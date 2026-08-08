using System.Reflection;
using NUnit.Framework;
using UnityEngine;

/// <summary>
/// 验证 Step 1 的水平到达判定及其与任务预留的集成语义。
/// </summary>
public sealed class Step1ArrivalEditModeTests
{
    /// <summary>验证 HMD 高度差不会阻塞同一 XZ 位置的到达。</summary>
    [Test]
    public void ArrivalDetector_HmdHeightDoesNotBlockArrival()
    {
        using (var harness = new ArrivalHarness("ArrivalHeight", new Vector3(2f, 0f, 3f)))
        {
            harness.User.position = new Vector3(2f, 1.65f, 3f);

            Assert.That(harness.DetectArrival(), Is.True);
        }
    }

    /// <summary>验证 XZ 在半径内时，即使三维距离超过半径仍可到达。</summary>
    [Test]
    public void ArrivalDetector_XzNearButThreeDimensionalDistanceFarArrives()
    {
        using (var harness = new ArrivalHarness("ArrivalPlanarNear", new Vector3(4f, 0f, 5f)))
        {
            harness.User.position = new Vector3(4.5f, 1.7f, 5f);

            Assert.That(Vector3.Distance(harness.User.position, harness.TargetPosition), Is.GreaterThan(0.75f));
            Assert.That(harness.DetectArrival(), Is.True);
        }
    }

    /// <summary>验证 XZ 超出到达半径时不会触发到达。</summary>
    [Test]
    public void ArrivalDetector_XzOutsideRadiusDoesNotArrive()
    {
        using (var harness = new ArrivalHarness("ArrivalPlanarFar", Vector3.zero))
        {
            harness.User.position = new Vector3(0.76f, 0f, 0f);

            Assert.That(harness.DetectArrival(), Is.False);
        }
    }

    /// <summary>验证旧代际 Arrival 不得推进当前 E5 预留。</summary>
    [Test]
    public void ArrivalDetector_StaleReservationCannotAdvanceCurrentTask()
    {
        using (var harness = new TaskArrivalHarness())
        {
            Assert.That(harness.Task.TryReserveGuidanceTarget(
                new GuidanceTarget("E5", NavigationTargetRole.FinalTarget), "decision-old"), Is.True);
            var staleReservation = harness.Task.RuntimeState.ActiveReservation;
            Assert.That(harness.Task.AbandonReservation(), Is.True);
            Assert.That(harness.Task.TryReserveGuidanceTarget(
                new GuidanceTarget("E5", NavigationTargetRole.FinalTarget), "decision-current"), Is.True);
            var currentReservation = harness.Task.RuntimeState.ActiveReservation;
            Assert.That(harness.Task.CommitReservation(currentReservation), Is.True);

            harness.Detector.WatchReservation(staleReservation);
            harness.InvokeDetectorUpdate();

            Assert.That(harness.Task.RuntimeState.currentExhibitId, Is.EqualTo("E1"));
            Assert.That(harness.Task.RuntimeState.ActiveReservation, Is.SameAs(currentReservation));
            Assert.That(harness.Task.RuntimeState.IsCompleted, Is.False);
        }
    }

    /// <summary>验证可导航绕行点到达只完成绕行，不会完成最终目标 E5。</summary>
    [Test]
    public void ArrivalDetector_DetourArrivalDoesNotCompleteFinalTarget()
    {
        using (var harness = new TaskArrivalHarness())
        {
            Assert.That(harness.Task.TryReserveGuidanceTarget(
                new GuidanceTarget("E3", NavigationTargetRole.NavigableDetour), "decision-detour"), Is.True);
            var reservation = harness.Task.RuntimeState.ActiveReservation;
            Assert.That(harness.Task.CommitReservation(reservation), Is.True);

            harness.MoveUserTo("E3");
            harness.Detector.WatchReservation(reservation);
            harness.InvokeDetectorUpdate();

            Assert.That(harness.Task.RuntimeState.currentExhibitId, Is.EqualTo("E3"));
            Assert.That(harness.Task.RuntimeState.finalTargetId, Is.EqualTo("E5"));
            Assert.That(harness.Task.RuntimeState.IsCompleted, Is.False);
            Assert.That(harness.Task.RuntimeState.HasVisitedDetour("E3"), Is.True);
        }
    }

    /// <summary>验证 E5 的有效 Arrival 会令任务真正进入 Completed。</summary>
    [Test]
    public void ArrivalDetector_E5FinalArrivalCompletesTask()
    {
        using (var harness = new TaskArrivalHarness())
        {
            Assert.That(harness.Task.TryReserveGuidanceTarget(
                new GuidanceTarget("E5", NavigationTargetRole.FinalTarget), "decision-final"), Is.True);
            var reservation = harness.Task.RuntimeState.ActiveReservation;
            Assert.That(harness.Task.CommitReservation(reservation), Is.True);

            harness.MoveUserTo("E5", 1.65f);
            harness.Detector.WatchReservation(reservation);
            harness.InvokeDetectorUpdate();

            Assert.That(harness.Task.RuntimeState.currentExhibitId, Is.EqualTo("E5"));
            Assert.That(harness.Task.RuntimeState.phase, Is.EqualTo(GuidanceRuntimePhase.Completed));
        }
    }

    private sealed class ArrivalHarness : System.IDisposable
    {
        private readonly GameObject root;
        private readonly TargetReservation reservation;
        private bool arrived;

        public Transform User { get; }
        public Vector3 TargetPosition { get; }
        public ArrivalDetector Detector { get; }

        public ArrivalHarness(string targetId, Vector3 targetPosition)
        {
            root = new GameObject($"Step1Arrival_{targetId}");
            User = new GameObject("VirtualUser").transform;
            User.SetParent(root.transform, false);
            TargetPosition = targetPosition;
            CreateExhibit(root.transform, targetId, targetPosition);

            var pose = root.AddComponent<UserPoseTracker>();
            pose.ConfigureVirtualUserRoot(User);
            var targets = root.AddComponent<NavigationTargetManager>();
            targets.Configure(pose);
            targets.RebuildIndex();
            Detector = root.AddComponent<ArrivalDetector>();
            Detector.Configure(pose, targets);
            Detector.TargetArrived += _ => arrived = true;
            reservation = new TargetReservation(
                targetId, NavigationTargetRole.FinalTarget, "arrival-test", 1, 1);
        }

        public bool DetectArrival()
        {
            arrived = false;
            Detector.WatchReservation(reservation);
            InvokeUpdate(Detector);
            return arrived;
        }

        public void Dispose()
        {
            Object.DestroyImmediate(root);
        }
    }

    private sealed class TaskArrivalHarness : System.IDisposable
    {
        private readonly GameObject root;
        private readonly Transform user;
        private readonly NavigationTargetManager targets;

        public GuidanceTaskController Task { get; }
        public ArrivalDetector Detector { get; }

        public TaskArrivalHarness()
        {
            root = new GameObject("Step1TaskArrivalHarness");
            user = new GameObject("VirtualUser").transform;
            user.SetParent(root.transform, false);
            CreateExhibit(root.transform, "E3", new Vector3(1f, 0f, 0f));
            CreateExhibit(root.transform, "E5", new Vector3(2f, 0f, 0f));

            var pose = root.AddComponent<UserPoseTracker>();
            pose.ConfigureVirtualUserRoot(user);
            targets = root.AddComponent<NavigationTargetManager>();
            targets.Configure(pose);
            targets.RebuildIndex();
            Detector = root.AddComponent<ArrivalDetector>();
            Detector.Configure(pose, targets);

            Task = root.AddComponent<GuidanceTaskController>();
            Task.ConfigureTask(new GuidanceTaskDefinition
            {
                condition = "ours_full",
                startExhibitId = "E1",
                finalTargetId = "E5",
                routeTargetIds = new[] {"E1", "E5"},
                maxConsecutiveDetours = 2
            });
            Task.StartTask();

            var coordinator = root.AddComponent<GuidanceCoordinator>();
            coordinator.Configure(null, null, Task, targets, null, Detector, null, null, null, null, null);
        }

        public void MoveUserTo(string targetId, float height = 0f)
        {
            Assert.That(targets.TryGetTarget(targetId, out var target), Is.True);
            var position = target.ViewPoint != null ? target.ViewPoint.position : target.transform.position;
            user.position = new Vector3(position.x, height, position.z);
        }

        public void InvokeDetectorUpdate()
        {
            InvokeUpdate(Detector);
        }

        public void Dispose()
        {
            Object.DestroyImmediate(root);
        }
    }

    private static ExhibitMetadata CreateExhibit(Transform parent, string id, Vector3 position)
    {
        var exhibitObject = new GameObject(id);
        exhibitObject.transform.SetParent(parent, false);
        exhibitObject.transform.position = position;
        var metadata = exhibitObject.AddComponent<ExhibitMetadata>();
        metadata.Configure(
            id, id, null, string.Empty, "main_target", null,
            exhibitObject.transform, exhibitObject.transform);
        return metadata;
    }

    private static void InvokeUpdate(ArrivalDetector detector)
    {
        var update = typeof(ArrivalDetector).GetMethod("Update", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.That(update, Is.Not.Null);
        update.Invoke(detector, null);
    }
}
