using NUnit.Framework;

/// <summary>
/// 验证四任务动态起点和固定三件中间展品的运行状态契约。
/// </summary>
public sealed class DynamicTaskStartupTests
{
    /// <summary>四个正式任务映射到计划中的最终目标和自然语言任务。</summary>
    [TestCase("T1_overview", "E8")]
    [TestCase("T2_main_target", "E5")]
    [TestCase("T3_compare", "E6")]
    [TestCase("T4_next_step", "E3")]
    public void Matrix_MapsFormalTaskToFinalTarget(string taskId, string finalTargetId)
    {
        var spec = ExperimentConditionMatrix.Build(0, System.Array.IndexOf(ExperimentConditionMatrix.TaskIds, taskId), 0, "P001");

        Assert.That(ExperimentConditionMatrix.IsFormalTaskSupported(taskId), Is.True);
        Assert.That(spec.finalTargetId, Is.EqualTo(finalTargetId));
        Assert.That(spec.startExhibitId, Is.Empty);
        Assert.That(spec.routeTargetIds, Is.Empty);
        Assert.That(spec.minimumIntermediateVisits, Is.EqualTo(3));
        Assert.That(spec.maximumIntermediateVisits, Is.EqualTo(3));
        Assert.That(spec.userTask, Is.Not.Empty);
    }

    /// <summary>空启动状态不记录起点；第三个中间展品到达后立即锁定最终目标。</summary>
    [Test]
    public void RuntimeState_StartsEmptyAndLocksFinalAfterThreeIntermediates()
    {
        var state = new GuidanceRuntimeState();
        state.Begin("T2_main_target", string.Empty, string.Empty, "E5");
        state.ConfigureIntermediateVisitPolicy("task", 3, 3);

        Assert.That(state.currentExhibitId, Is.Empty);
        Assert.That(state.visitedExhibitIds, Is.Empty);
        Assert.That(state.intermediateVisitCount, Is.EqualTo(0));

        state.AdvanceSemanticTo("E1");
        state.AdvanceSemanticTo("E2");
        state.AdvanceSemanticTo("E4");

        Assert.That(state.intermediateVisitCount, Is.EqualTo(3));
        Assert.That(state.taskStage, Is.EqualTo(GuidanceTaskStage.FinalApproach));
        Assert.That(state.nextTargetId, Is.EqualTo("E5"));
        Assert.That(state.visitedExhibitIds, Is.EqualTo(new[] { "E1", "E2", "E4" }));
    }
}
