using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

public sealed class SceneSemanticsEditorTests
{
    private const string MainScenePath = "Assets/Scenes/MainScene.unity";

    [Test]
    public void Config_IsSchemaV2AndContainsExactExhibitContract()
    {
        SceneSemanticsConfig config = SceneSemanticsConfigLoader.Load();

        Assert.That(config.schema_version, Is.EqualTo(2));
        Assert.That(config.layout.floor_size_m.width, Is.EqualTo(12f));
        Assert.That(config.layout.floor_size_m.length, Is.EqualTo(14f));
        Assert.That(config.exhibits.Select(exhibit => exhibit.id), Is.EqualTo(
            new[] { "E1", "E2", "E3", "E4", "E5", "E6", "E7", "E8" }));
        Assert.That(config.exhibits.All(exhibit =>
            exhibit.tags.Length > 0
            && !string.IsNullOrWhiteSpace(exhibit.semantic_role)
            && !string.IsNullOrWhiteSpace(exhibit.description)), Is.True);
    }

    [Test]
    public void Geometry_SeparatedCollinearEdgesReturnPositiveClearance()
    {
        var left = new GalleryFootprint(
            "left",
            new Vector2(-4.35f, 3.8f),
            Vector2.up,
            Vector2.right,
            new Vector2(0.9f, 0.55f));
        var right = new GalleryFootprint(
            "right",
            new Vector2(4.35f, 3.8f),
            Vector2.down,
            Vector2.left,
            new Vector2(0.9f, 0.55f));

        Assert.That(SceneSemanticsGeometry.DistanceBetween(left, right), Is.EqualTo(7.6f).Within(0.001f));
    }

    [Test]
    public void MainScene_PassesLayoutAndSemanticValidation()
    {
        Scene scene = EditorSceneManager.OpenScene(MainScenePath, OpenSceneMode.Single);
        SceneSemanticsValidationReport report =
            SceneSemanticsValidator.Validate(scene, SceneSemanticsConfigLoader.Load());

        Assert.That(report.errors, Is.Empty, string.Join(Environment.NewLine, report.errors));
        Assert.That(report.warnings, Is.Empty, string.Join(Environment.NewLine, report.warnings));
    }

    [Test]
    public void MainScene_ContainsActiveFinalEnvironmentAndExactMetadataContract()
    {
        Scene scene = EditorSceneManager.OpenScene(MainScenePath, OpenSceneMode.Single);
        Transform packageRoot = SceneSemanticsValidator.FindRoot(scene, "VirtualGuidance_B_Root");
        Assert.That(packageRoot, Is.Not.Null);
        Assert.That(packageRoot.Find("Environment/EffectiveFloor_12x14").gameObject.activeInHierarchy, Is.True);
        Assert.That(packageRoot.Find("Environment/Walls").gameObject.activeInHierarchy, Is.True);
        Assert.That(packageRoot.Find("Environment/Walls/Ceiling").gameObject.activeInHierarchy, Is.True);

        ExhibitMetadata[] metadata = scene.GetRootGameObjects()
            .SelectMany(root => root.GetComponentsInChildren<ExhibitMetadata>(true))
            .Where(item => item.gameObject.activeInHierarchy)
            .OrderBy(item => item.Id)
            .ToArray();
        Assert.That(metadata.Select(item => item.Id), Is.EqualTo(
            new[] { "E1", "E2", "E3", "E4", "E5", "E6", "E7", "E8" }));
        Assert.That(metadata.All(item =>
            item.transform.localScale == Vector3.one
            && item.ViewPoint != null
            && item.GazeTarget != null
            && item.GetComponents<ExhibitMetadata>().Length == 1), Is.True);
    }

    [Test]
    public void MainScene_RegistersHighlightsAndLooksAtAllExhibits()
    {
        Scene scene = EditorSceneManager.OpenScene(MainScenePath, OpenSceneMode.Single);
        var harness = new GameObject("SceneContractHarness");
        try
        {
            var pose = harness.AddComponent<UserPoseTracker>();
            var user = new GameObject("SceneContractUser");
            user.transform.SetParent(harness.transform, false);
            pose.ConfigureVirtualUserRoot(user.transform);

            var targets = harness.AddComponent<NavigationTargetManager>();
            targets.Configure(pose);
            targets.RebuildIndex();
            string[] ids = { "E1", "E2", "E3", "E4", "E5", "E6", "E7", "E8" };
            Assert.That(targets.GetRegisteredTargetIds(), Is.EqualTo(ids));

            var highlighter = harness.AddComponent<ExhibitHighlightManager>();
            highlighter.RebuildIndex();
            FieldInfo highlightedField = typeof(ExhibitHighlightManager).GetField(
                "highlightedRenderers",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(highlightedField, Is.Not.Null);
            foreach (string id in ids)
            {
                highlighter.Highlight(id);
                var highlighted = (List<Renderer>)highlightedField.GetValue(highlighter);
                Assert.That(highlighted, Is.Not.Empty, $"{id} has no renderer under its Metadata root.");
                highlighter.Clear();
            }

            Assert.That(targets.TryGetTarget("E5", out ExhibitMetadata e5), Is.True);
            var avatar = new GameObject("SceneContractAvatar");
            avatar.transform.SetParent(harness.transform, false);
            avatar.transform.position = new Vector3(0f, 1f, 0f);
            var pointer = new GameObject("SceneContractPointer");
            pointer.transform.SetParent(avatar.transform, false);
            var controller = harness.AddComponent<GuideAvatarController>();
            controller.Configure(avatar.transform, pointer.transform);
            controller.Apply("look_at_target", "E5", string.Empty);

            Vector3 expected = Vector3.ProjectOnPlane(
                e5.GazeTarget.position - avatar.transform.position,
                Vector3.up).normalized;
            Assert.That(Vector3.Dot(avatar.transform.forward, expected), Is.GreaterThan(0.999f));
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(harness);
        }
    }

    [Test]
    public void ApplyAndExport_PreserveSemanticTextAndRestoreConfiguredLayout()
    {
        Scene scene = EditorSceneManager.OpenScene(MainScenePath, OpenSceneMode.Single);
        SceneSemanticsConfig config = SceneSemanticsConfigLoader.Load();
        string[] semanticSnapshot = config.exhibits
            .Select(exhibit => string.Join("|",
                exhibit.id,
                exhibit.name,
                exhibit.semantic_role,
                exhibit.description,
                string.Join(",", exhibit.tags),
                string.Join(",", exhibit.related_exhibits)))
            .ToArray();

        Transform e1 = SceneSemanticsValidator.FindPath(scene, config.exhibits[0].scene_path);
        Assert.That(e1, Is.Not.Null);
        e1.position += new Vector3(1f, 0f, 1f);

        SceneSemanticsTools.ApplyConfigToScene(scene, config);
        Assert.That(e1.position.x, Is.EqualTo(config.exhibits[0].position[0]).Within(0.001f));
        Assert.That(e1.position.z, Is.EqualTo(config.exhibits[0].position[2]).Within(0.001f));

        SceneSemanticsTools.PopulateLayoutFromScene(scene, config);
        string[] semanticAfterExport = config.exhibits
            .Select(exhibit => string.Join("|",
                exhibit.id,
                exhibit.name,
                exhibit.semantic_role,
                exhibit.description,
                string.Join(",", exhibit.tags),
                string.Join(",", exhibit.related_exhibits)))
            .ToArray();

        Assert.That(semanticAfterExport, Is.EqualTo(semanticSnapshot));
    }
}
