using NUnit.Framework;

/// <summary>OURS Reset 语义旁路的协议与等级回归测试。</summary>
public sealed class RdwOursPlanningTests
{
    /// <summary>小于 60 度的 Reset 只使用轻量干预。</summary>
    [Test]
    public void ResetSemanticIntervention_ClassifiesLowAngleAsLow()
    {
        var manager = new UnityEngine.GameObject("reset-semantic-test").AddComponent<ResetSemanticInterventionManager>();
        try
        {
            Assert.That(manager.Classify(59.999f), Is.EqualTo(ResetSemanticInterventionLevel.Low));
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(manager.gameObject);
        }
    }

    /// <summary>60 度包含在 Medium 区间，120 度进入 High 区间。</summary>
    [Test]
    public void ResetSemanticIntervention_ClassifiesConfiguredRanges()
    {
        var manager = new UnityEngine.GameObject("reset-semantic-test").AddComponent<ResetSemanticInterventionManager>();
        try
        {
            Assert.That(manager.Classify(60f), Is.EqualTo(ResetSemanticInterventionLevel.Medium));
            Assert.That(manager.Classify(119.999f), Is.EqualTo(ResetSemanticInterventionLevel.Medium));
            Assert.That(manager.Classify(120f), Is.EqualTo(ResetSemanticInterventionLevel.High));
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(manager.gameObject);
        }
    }

    /// <summary>Reset 语义响应只允许等级和 narration，拒绝物理控制字段。</summary>
    [Test]
    public void ResetSemanticProtocol_RejectsPhysicalFields()
    {
        var json = "{\"intervention_level\":\"high\",\"narration\":\"过渡话术\",\"target_heading\":90}";
        Assert.That(
            ResetSemanticInterventionProtocol.TryParseResponse(json, out _, out var reason),
            Is.False);
        Assert.That(reason, Does.Contain("unexpected_field"));
    }
}
