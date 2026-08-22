using Newtonsoft.Json.Linq;
using NUnit.Framework;
using TMPro;
using UnityEngine;

/// <summary>OURS Reset 语义旁路的 v2 协议与 Participant UI 回归测试。</summary>
public sealed class RdwOursPlanningTests
{
    /// <summary>语义干预只允许 OURS Profile，并受 Inspector 开关控制。</summary>
    [Test]
    public void ResetSemanticIntervention_RequiresOursProfileAndInspectorSwitch()
    {
        var managerObject = new GameObject("reset-semantic-gate-test");
        try
        {
            var manager = managerObject.AddComponent<ResetSemanticInterventionManager>();
            var b1 = ExperimentConditionMatrix.GetProfile("B1_semantic_only");
            var b2 = ExperimentConditionMatrix.GetProfile("B2_semantic_only_continuous");
            var ours = ExperimentConditionMatrix.GetProfile("ours_rdw_grounded_topk");

            manager.Configure(null, null, null, null, enabled: true);
            Assert.That(manager.IsEnabledForProfile(b1), Is.False);
            Assert.That(manager.IsEnabledForProfile(b2), Is.False);
            Assert.That(manager.IsEnabledForProfile(ours), Is.True);

            manager.Configure(null, null, null, null, enabled: false);
            Assert.That(manager.IsEnabledForProfile(ours), Is.False);
        }
        finally
        {
            Object.DestroyImmediate(managerObject);
        }
    }

    /// <summary>v2 请求只包含语义上下文、稳定标识与去重历史。</summary>
    [Test]
    public void ResetSemanticProtocol_SerializesSemanticContextWithoutPhysicalFields()
    {
        var json = ResetSemanticInterventionProtocol.SerializeRequest(new ResetSemanticContext
        {
            session_id = "session-1",
            reset_id = "reset-1",
            variation_seed = 7,
            current_exhibit_id = "E3",
            current_exhibit_name = "青铜纵目面具",
            next_target_id = "E5",
            next_target_name = "青铜神树",
            current_topic = "祭祀与宇宙观",
            semantic_hint = "青铜纵目面具与青铜神树共享一条主题线索，可以想想其中的联系。",
            semantic_hint_key = "edge:SE08",
            recent_narrations = new[] { "上一条语义话术" },
            recent_content_keys = new[] { "edge:SE07" }
        });

        var payload = JObject.Parse(json);
        Assert.That((string)payload["planning_schema_version"], Is.EqualTo("reset-semantic-intervention-v2"));
        Assert.That((string)payload["reset_id"], Is.EqualTo("reset-1"));
        Assert.That(payload["recent_content_keys"], Has.Count.EqualTo(1));
        Assert.That(payload["reset_angle"], Is.Null);
        Assert.That(payload["reset_reason"], Is.Null);
    }

    /// <summary>Reset 语义响应只允许 narration 与 content_key，拒绝物理控制字段。</summary>
    [Test]
    public void ResetSemanticProtocol_RejectsPhysicalFields()
    {
        const string json =
            "{\"narration\":\"青铜神树的层层枝叶连接着自然意象与古蜀宇宙观。\",\"content_key\":\"edge:SE11\",\"target_heading\":90}";
        Assert.That(
            ResetSemanticInterventionProtocol.TryParseResponse(json, out _, out var reason),
            Is.False);
        Assert.That(reason, Does.Contain("unexpected_field"));
    }

    /// <summary>B1/B2 保留原 Reset 提示，OURS 开启语义开关后才合并新提示与话术。</summary>
    [TestCase(-180f, "向左")]
    [TestCase(180f, "向右")]
    public void ParticipantUi_ResetDirectionDoesNotExposeFixedAngle(
        float relativeYawDegrees,
        string expectedDirection)
    {
        var controllerObject = new GameObject("participant-ui-test");
        var textObject = new GameObject(
            "safety-text-test",
            typeof(RectTransform),
            typeof(CanvasRenderer),
            typeof(TextMeshProUGUI));
        try
        {
            var controller = controllerObject.AddComponent<ParticipantUiController>();
            var safetyText = textObject.GetComponent<TMP_Text>();
            controller.Configure(null, null, null, null, null, safetyText);
            controller.ShowRecoveryStarted(new ResetEventInfo(
                null,
                "test",
                relativeYawOverride: relativeYawDegrees,
                resetId: "reset-1",
                mappingStrategy: RdwResetMappingStrategy.FixedTwoOneTurn));

            Assert.That(safetyText.text, Does.Contain("180"));
            controller.SetResetSemanticInterventionEnabled(true);
            controller.ShowRecoveryStarted(new ResetEventInfo(
                null,
                "test",
                relativeYawOverride: relativeYawDegrees,
                resetId: "reset-2",
                mappingStrategy: RdwResetMappingStrategy.FixedTwoOneTurn));
            Assert.That(safetyText.text, Is.EqualTo(
                expectedDirection == "向左"
                    ? "请稍等一下，先向左环视一周"
                    : "请稍等一下，先向右环视一周"));
            controller.ShowResetNarration("青铜神树的层层枝叶连接着自然意象与古蜀宇宙观。");
            Assert.That(safetyText.text, Does.Contain(expectedDirection));
            Assert.That(safetyText.text, Does.Contain("青铜神树"));
            Assert.That(safetyText.text, Does.Not.Contain("180"));
            Assert.That(safetyText.text, Does.Not.Contain("360"));
            Assert.That(safetyText.text, Does.Not.Contain("转一圈"));
        }
        finally
        {
            Object.DestroyImmediate(textObject);
            Object.DestroyImmediate(controllerObject);
        }
    }
}
