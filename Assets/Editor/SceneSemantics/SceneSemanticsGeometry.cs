using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;

internal sealed class SceneSemanticsValidationReport
{
    internal readonly List<string> errors = new List<string>();
    internal readonly List<string> warnings = new List<string>();
    internal readonly List<string> measurements = new List<string>();
    internal bool IsValid => errors.Count == 0;
}

internal readonly struct GalleryFootprint
{
    internal GalleryFootprint(string id, Vector2 center, Vector2 axisX, Vector2 axisZ, Vector2 halfSize)
    {
        Id = id;
        Center = center;
        AxisX = axisX.normalized;
        AxisZ = axisZ.normalized;
        HalfSize = halfSize;
    }

    internal string Id { get; }
    internal Vector2 Center { get; }
    internal Vector2 AxisX { get; }
    internal Vector2 AxisZ { get; }
    internal Vector2 HalfSize { get; }

    internal Vector2[] GetCorners()
    {
        Vector2 xOffset = AxisX * HalfSize.x;
        Vector2 zOffset = AxisZ * HalfSize.y;
        return new[]
        {
            Center - xOffset - zOffset,
            Center + xOffset - zOffset,
            Center + xOffset + zOffset,
            Center - xOffset + zOffset
        };
    }
}

internal static class SceneSemanticsGeometry
{
    internal const float PositionTolerance = 0.01f;
    internal const float RotationTolerance = 0.1f;
    internal const float SizeTolerance = 0.02f;
    internal const float MinimumCabinetClearance = 1.4f;
    internal const float MinimumMainAisleClearance = 2f;
    internal const float ViewPointClearanceRadius = 0.75f;

    internal static Bounds MeasureRendererBoundsInRootSpace(Transform visualContent, Transform exhibitRoot)
    {
        Renderer[] renderers = visualContent.GetComponentsInChildren<Renderer>(true)
            .Where(renderer => renderer.enabled && renderer.gameObject.activeInHierarchy)
            .ToArray();
        if (renderers.Length == 0)
        {
            return new Bounds(Vector3.zero, Vector3.zero);
        }

        bool initialized = false;
        Bounds bounds = default;
        foreach (Renderer renderer in renderers)
        {
            Bounds localBounds = renderer.localBounds;
            Vector3 min = localBounds.min;
            Vector3 max = localBounds.max;
            for (int xIndex = 0; xIndex < 2; xIndex++)
            {
                for (int yIndex = 0; yIndex < 2; yIndex++)
                {
                    for (int zIndex = 0; zIndex < 2; zIndex++)
                    {
                        Vector3 localCorner = new Vector3(
                            xIndex == 0 ? min.x : max.x,
                            yIndex == 0 ? min.y : max.y,
                            zIndex == 0 ? min.z : max.z);
                        Vector3 rootCorner = exhibitRoot.InverseTransformPoint(
                            renderer.transform.TransformPoint(localCorner));
                        if (!initialized)
                        {
                            bounds = new Bounds(rootCorner, Vector3.zero);
                            initialized = true;
                        }
                        else
                        {
                            bounds.Encapsulate(rootCorner);
                        }
                    }
                }
            }
        }

        return bounds;
    }

    internal static GalleryFootprint CreateFootprint(string id, Transform exhibitRoot, BoxCollider envelope)
    {
        Vector3 worldCenter = envelope.transform.TransformPoint(envelope.center);
        Vector3 worldSize = Vector3.Scale(envelope.size, envelope.transform.lossyScale);
        Vector3 worldRight = exhibitRoot.right;
        Vector3 worldForward = exhibitRoot.forward;
        return new GalleryFootprint(
            id,
            new Vector2(worldCenter.x, worldCenter.z),
            new Vector2(worldRight.x, worldRight.z),
            new Vector2(worldForward.x, worldForward.z),
            new Vector2(Mathf.Abs(worldSize.x) * 0.5f, Mathf.Abs(worldSize.z) * 0.5f));
    }

    internal static float DistanceBetween(GalleryFootprint first, GalleryFootprint second)
    {
        Vector2[] firstCorners = first.GetCorners();
        Vector2[] secondCorners = second.GetCorners();
        if (Overlaps(first, second))
        {
            return 0f;
        }

        float minimum = float.PositiveInfinity;
        for (int firstIndex = 0; firstIndex < 4; firstIndex++)
        {
            Vector2 firstStart = firstCorners[firstIndex];
            Vector2 firstEnd = firstCorners[(firstIndex + 1) % 4];
            for (int secondIndex = 0; secondIndex < 4; secondIndex++)
            {
                Vector2 secondStart = secondCorners[secondIndex];
                Vector2 secondEnd = secondCorners[(secondIndex + 1) % 4];
                minimum = Mathf.Min(minimum, SegmentDistance(firstStart, firstEnd, secondStart, secondEnd));
            }
        }

        return minimum;
    }

    internal static float DistanceToPoint(GalleryFootprint footprint, Vector2 point)
    {
        Vector2 relative = point - footprint.Center;
        float localX = Vector2.Dot(relative, footprint.AxisX);
        float localZ = Vector2.Dot(relative, footprint.AxisZ);
        float outsideX = Mathf.Max(Mathf.Abs(localX) - footprint.HalfSize.x, 0f);
        float outsideZ = Mathf.Max(Mathf.Abs(localZ) - footprint.HalfSize.y, 0f);
        return Mathf.Sqrt(outsideX * outsideX + outsideZ * outsideZ);
    }

    private static bool Overlaps(GalleryFootprint first, GalleryFootprint second)
    {
        Vector2[] axes = { first.AxisX, first.AxisZ, second.AxisX, second.AxisZ };
        Vector2[] firstCorners = first.GetCorners();
        Vector2[] secondCorners = second.GetCorners();
        foreach (Vector2 axis in axes)
        {
            Project(firstCorners, axis, out float firstMin, out float firstMax);
            Project(secondCorners, axis, out float secondMin, out float secondMax);
            if (firstMax < secondMin || secondMax < firstMin)
            {
                return false;
            }
        }

        return true;
    }

    private static void Project(IEnumerable<Vector2> corners, Vector2 axis, out float minimum, out float maximum)
    {
        minimum = float.PositiveInfinity;
        maximum = float.NegativeInfinity;
        foreach (Vector2 corner in corners)
        {
            float value = Vector2.Dot(corner, axis);
            minimum = Mathf.Min(minimum, value);
            maximum = Mathf.Max(maximum, value);
        }
    }

    private static float SegmentDistance(Vector2 firstStart, Vector2 firstEnd, Vector2 secondStart, Vector2 secondEnd)
    {
        return Mathf.Min(
            PointSegmentDistance(firstStart, secondStart, secondEnd),
            PointSegmentDistance(firstEnd, secondStart, secondEnd),
            PointSegmentDistance(secondStart, firstStart, firstEnd),
            PointSegmentDistance(secondEnd, firstStart, firstEnd));
    }

    private static float PointSegmentDistance(Vector2 point, Vector2 start, Vector2 end)
    {
        Vector2 segment = end - start;
        float denominator = segment.sqrMagnitude;
        if (denominator <= Mathf.Epsilon)
        {
            return Vector2.Distance(point, start);
        }

        float ratio = Mathf.Clamp01(Vector2.Dot(point - start, segment) / denominator);
        return Vector2.Distance(point, start + segment * ratio);
    }
}

internal static class SceneSemanticsValidator
{
    internal static SceneSemanticsValidationReport Validate(Scene scene, SceneSemanticsConfig config)
    {
        var report = new SceneSemanticsValidationReport();
        if (!scene.IsValid() || !scene.isLoaded)
        {
            report.errors.Add("当前场景无效或未加载。");
            return report;
        }

        ValidateConfig(config, report);
        Transform packageRoot = FindRoot(scene, "VirtualGuidance_B_Root");
        if (packageRoot == null)
        {
            report.errors.Add("缺少激活的 VirtualGuidance_B_Root。");
            return report;
        }

        if (!packageRoot.gameObject.activeInHierarchy)
        {
            report.errors.Add("VirtualGuidance_B_Root 未启用。");
        }

        ExhibitMetadata[] activeMetadata = scene.GetRootGameObjects()
            .SelectMany(root => root.GetComponentsInChildren<ExhibitMetadata>(true))
            .Where(component => component != null && component.gameObject.activeInHierarchy)
            .ToArray();
        if (activeMetadata.Length != 8)
        {
            report.errors.Add($"激活的 ExhibitMetadata 应为 8 个，实际为 {activeMetadata.Length} 个。");
        }

        var footprints = new List<GalleryFootprint>();
        foreach (ExhibitSemanticsConfig exhibit in config.exhibits ?? Array.Empty<ExhibitSemanticsConfig>())
        {
            ValidateExhibit(scene, exhibit, report, footprints);
        }

        ValidateFootprints(config, footprints, report);
        ValidateEnvironment(packageRoot, report);
        ValidateLegacyRoots(scene, report);
        return report;
    }

    private static void ValidateConfig(SceneSemanticsConfig config, SceneSemanticsValidationReport report)
    {
        if (config == null || config.schema_version != 2)
        {
            report.errors.Add("语义配置不是 schema v2。");
            return;
        }

        if (config.exhibits == null || config.exhibits.Length != 8)
        {
            report.errors.Add("JSON exhibits 必须恰好包含 8 项。");
            return;
        }

        string[] ids = config.exhibits.Select(exhibit => exhibit.id).ToArray();
        if (ids.Any(string.IsNullOrWhiteSpace) || ids.Distinct(StringComparer.Ordinal).Count() != 8)
        {
            report.errors.Add("JSON 展品 ID 存在空值或重复。");
        }

        var validIds = new HashSet<string>(ids, StringComparer.Ordinal);
        foreach (ExhibitSemanticsConfig exhibit in config.exhibits)
        {
            if (exhibit.related_exhibits == null
                || exhibit.related_exhibits.Any(related => !validIds.Contains(related)))
            {
                report.errors.Add($"{exhibit.id} 的 related_exhibits 包含无效 ID。");
            }
        }
    }

    private static void ValidateExhibit(
        Scene scene,
        ExhibitSemanticsConfig exhibit,
        SceneSemanticsValidationReport report,
        ICollection<GalleryFootprint> footprints)
    {
        Transform root = FindPath(scene, exhibit.scene_path);
        if (root == null)
        {
            report.errors.Add($"找不到场景路径：{exhibit.scene_path}");
            return;
        }

        if (!root.gameObject.activeInHierarchy)
        {
            report.errors.Add($"{exhibit.id} 未激活。");
        }

        if ((root.localScale - Vector3.one).sqrMagnitude > 0.000001f)
        {
            report.errors.Add($"{exhibit.id} 根节点 Scale 必须为 (1,1,1)。");
        }

        Vector3 expectedPosition = ToVector3(exhibit.position);
        if (Vector3.Distance(root.position, expectedPosition) > SceneSemanticsGeometry.PositionTolerance)
        {
            report.errors.Add($"{exhibit.id} 位置与 JSON 不一致。");
        }

        if (Mathf.Abs(Mathf.DeltaAngle(root.eulerAngles.y, exhibit.rotation_y_degrees))
            > SceneSemanticsGeometry.RotationTolerance)
        {
            report.errors.Add($"{exhibit.id} Yaw 与 JSON 不一致。");
        }

        ExhibitMetadata[] metadataComponents = root.GetComponents<ExhibitMetadata>();
        if (metadataComponents.Length != 1)
        {
            report.errors.Add($"{exhibit.id} 必须且只能挂载一个 ExhibitMetadata。");
            return;
        }

        ExhibitMetadata metadata = metadataComponents[0];
        if (metadata.Id != exhibit.id
            || metadata.ExhibitName != exhibit.name
            || metadata.Description != exhibit.description
            || metadata.SemanticRole != exhibit.semantic_role
            || !SequenceEqual(metadata.Tags, exhibit.tags)
            || !SequenceEqual(metadata.RelatedExhibits, exhibit.related_exhibits))
        {
            report.errors.Add($"{exhibit.id} 的 Metadata 与 JSON 不一致。");
        }

        if (metadata.ViewPoint == null || metadata.GazeTarget == null)
        {
            report.errors.Add($"{exhibit.id} 的 ViewPoint 或 GazeTarget 未绑定。");
        }
        else
        {
            if (Vector3.Distance(metadata.ViewPoint.position, ToVector3(exhibit.viewpoint))
                > SceneSemanticsGeometry.PositionTolerance)
            {
                report.errors.Add($"{exhibit.id} ViewPoint 与 JSON 不一致。");
            }

            if (Vector3.Distance(metadata.GazeTarget.position, ToVector3(exhibit.gaze_target))
                > SceneSemanticsGeometry.PositionTolerance)
            {
                report.errors.Add($"{exhibit.id} GazeTarget 与 JSON 不一致。");
            }
        }

        Transform visualContent = root.Find("VisualContent");
        BoxCollider envelope = root.Find("CabinetEnvelope")?.GetComponent<BoxCollider>();
        if (visualContent == null || envelope == null)
        {
            report.errors.Add($"{exhibit.id} 缺少 VisualContent 或 CabinetEnvelope。");
            return;
        }

        Vector3 expectedSize = new Vector3(
            exhibit.cabinet.width_m,
            exhibit.cabinet.height_m,
            exhibit.cabinet.depth_m);
        if (Vector3.Distance(envelope.size, expectedSize) > SceneSemanticsGeometry.SizeTolerance)
        {
            report.errors.Add($"{exhibit.id} 柜体外廓与 JSON 目标尺寸不一致。");
        }

        Bounds visualBounds = SceneSemanticsGeometry.MeasureRendererBoundsInRootSpace(visualContent, root);
        if (visualBounds.size.x > expectedSize.x + SceneSemanticsGeometry.SizeTolerance
            || visualBounds.size.y > expectedSize.y + SceneSemanticsGeometry.SizeTolerance
            || visualBounds.size.z > expectedSize.z + SceneSemanticsGeometry.SizeTolerance)
        {
            report.errors.Add($"{exhibit.id} 视觉内容超出柜体外廓。");
        }

        Transform showcaseVisual = visualContent.Find("ShowcaseVisual");
        if (showcaseVisual == null)
        {
            report.errors.Add($"{exhibit.id} 缺少 ShowcaseVisual。");
            return;
        }

        Bounds showcaseBounds =
            SceneSemanticsGeometry.MeasureRendererBoundsInRootSpace(showcaseVisual, root);
        if (Mathf.Abs(showcaseBounds.size.x - expectedSize.x) > SceneSemanticsGeometry.SizeTolerance
            || Mathf.Abs(showcaseBounds.size.y - expectedSize.y) > SceneSemanticsGeometry.SizeTolerance
            || Mathf.Abs(showcaseBounds.size.z - expectedSize.z) > SceneSemanticsGeometry.SizeTolerance)
        {
            report.errors.Add($"{exhibit.id} 实测展柜渲染外廓与 JSON 目标尺寸不一致。");
        }

        GalleryFootprint footprint = SceneSemanticsGeometry.CreateFootprint(exhibit.id, root, envelope);
        footprints.Add(footprint);
        report.measurements.Add(
            $"{exhibit.id}: position={Format(root.position)}, yaw={root.eulerAngles.y:F2}, "
            + $"target={expectedSize.x:F2}x{expectedSize.z:F2}x{expectedSize.y:F2}, "
            + $"showcase={showcaseBounds.size.x:F2}x{showcaseBounds.size.z:F2}x{showcaseBounds.size.y:F2}, "
            + $"content={visualBounds.size.x:F2}x{visualBounds.size.z:F2}x{visualBounds.size.y:F2}");
    }

    private static void ValidateFootprints(
        SceneSemanticsConfig config,
        IReadOnlyList<GalleryFootprint> footprints,
        SceneSemanticsValidationReport report)
    {
        GalleryFloorBoundsConfig bounds = config.layout.floor_bounds;
        foreach (GalleryFootprint footprint in footprints)
        {
            float minimumBoundaryDistance = float.PositiveInfinity;
            foreach (Vector2 corner in footprint.GetCorners())
            {
                if (corner.x < bounds.x_min - SceneSemanticsGeometry.PositionTolerance
                    || corner.x > bounds.x_max + SceneSemanticsGeometry.PositionTolerance
                    || corner.y < bounds.z_min - SceneSemanticsGeometry.PositionTolerance
                    || corner.y > bounds.z_max + SceneSemanticsGeometry.PositionTolerance)
                {
                    report.errors.Add($"{footprint.Id} 柜体超出 12m x 14m 有效地面。");
                    break;
                }

                minimumBoundaryDistance = Mathf.Min(
                    minimumBoundaryDistance,
                    corner.x - bounds.x_min,
                    bounds.x_max - corner.x,
                    corner.y - bounds.z_min,
                    bounds.z_max - corner.y);
            }

            float requiredBoundary = 0.8f;
            if (minimumBoundaryDistance + SceneSemanticsGeometry.PositionTolerance < requiredBoundary)
            {
                report.errors.Add($"{footprint.Id} 到地面边界的距离低于 {requiredBoundary:F1}m。");
            }
        }

        for (int firstIndex = 0; firstIndex < footprints.Count; firstIndex++)
        {
            for (int secondIndex = firstIndex + 1; secondIndex < footprints.Count; secondIndex++)
            {
                float distance = SceneSemanticsGeometry.DistanceBetween(
                    footprints[firstIndex],
                    footprints[secondIndex]);
                report.measurements.Add(
                    $"{footprints[firstIndex].Id}-{footprints[secondIndex].Id} clearance={distance:F2}m");
                if (distance + SceneSemanticsGeometry.PositionTolerance
                    < SceneSemanticsGeometry.MinimumCabinetClearance)
                {
                    report.errors.Add(
                        $"{footprints[firstIndex].Id} 与 {footprints[secondIndex].Id} 净距 {distance:F2}m 低于 1.40m。");
                }
            }
        }

        ValidateMainAisle(footprints, report);

        foreach (ExhibitSemanticsConfig exhibit in config.exhibits)
        {
            Vector3 viewPoint = ToVector3(exhibit.viewpoint);
            foreach (GalleryFootprint footprint in footprints)
            {
                float distance = SceneSemanticsGeometry.DistanceToPoint(
                    footprint,
                    new Vector2(viewPoint.x, viewPoint.z));
                if (distance + SceneSemanticsGeometry.PositionTolerance
                    < SceneSemanticsGeometry.ViewPointClearanceRadius)
                {
                    report.errors.Add(
                        $"{exhibit.id} ViewPoint 距 {footprint.Id} 柜体仅 {distance:F2}m，低于 0.75m。");
                }
            }
        }
    }

    private static void ValidateMainAisle(
        IReadOnlyList<GalleryFootprint> footprints,
        SceneSemanticsValidationReport report)
    {
        foreach (string[] pair in new[]
                 {
                     new[] { "E1", "E2" },
                     new[] { "E2", "E5" },
                     new[] { "E5", "E8" }
                 })
        {
            GalleryFootprint first = footprints.FirstOrDefault(item => item.Id == pair[0]);
            GalleryFootprint second = footprints.FirstOrDefault(item => item.Id == pair[1]);
            if (string.IsNullOrEmpty(first.Id) || string.IsNullOrEmpty(second.Id))
            {
                continue;
            }

            float clearance = SceneSemanticsGeometry.DistanceBetween(first, second);
            report.measurements.Add($"{first.Id}-{second.Id} main_aisle={clearance:F2}m");
            if (clearance + SceneSemanticsGeometry.PositionTolerance
                < SceneSemanticsGeometry.MinimumMainAisleClearance)
            {
                report.errors.Add(
                    $"{first.Id}-{second.Id} main aisle {clearance:F2}m is below 2.00m.");
            }
        }
    }

    private static void ValidateEnvironment(
        Transform packageRoot,
        SceneSemanticsValidationReport report)
    {
        Transform environment = packageRoot.Find("Environment");
        Transform floor = environment?.Find("EffectiveFloor_12x14");
        Transform walls = environment?.Find("Walls");
        Transform ceiling = walls?.Find("Ceiling");
        if (environment == null || floor == null || walls == null || ceiling == null)
        {
            report.errors.Add("Gallery environment is missing floor, walls, or ceiling.");
            return;
        }

        if (!environment.gameObject.activeInHierarchy
            || !floor.gameObject.activeInHierarchy
            || !walls.gameObject.activeInHierarchy
            || !ceiling.gameObject.activeInHierarchy)
        {
            report.errors.Add("Gallery floor, walls, and ceiling must be active for final acceptance.");
        }

        BoxCollider floorCollider = floor.GetComponent<BoxCollider>();
        if (floorCollider == null)
        {
            report.errors.Add("EffectiveFloor_12x14 must have a BoxCollider.");
            return;
        }

        Vector3 floorSize = Vector3.Scale(floorCollider.size, floor.lossyScale);
        Vector3 floorCenter = floor.TransformPoint(floorCollider.center);
        report.measurements.Add(
            $"floor={floorSize.x:F2}x{floorSize.z:F2}m center=({floorCenter.x:F2},{floorCenter.z:F2})");
        if (Mathf.Abs(floorSize.x - 12f) > SceneSemanticsGeometry.SizeTolerance
            || Mathf.Abs(floorSize.z - 14f) > SceneSemanticsGeometry.SizeTolerance
            || Mathf.Abs(floorCenter.x) > SceneSemanticsGeometry.PositionTolerance
            || Mathf.Abs(floorCenter.z - 7f) > SceneSemanticsGeometry.PositionTolerance)
        {
            report.errors.Add("Effective floor must be 12m x 14m and centered at (0, 0, 7).");
        }
    }

    private static void ValidateLegacyRoots(Scene scene, SceneSemanticsValidationReport report)
    {
        foreach (string rootName in new[] { "Environment", "Exhibits" })
        {
            Transform root = FindRoot(scene, rootName);
            if (root != null && root.gameObject.activeSelf)
            {
                report.errors.Add($"旧根节点 {rootName} 必须保持停用。");
            }
        }
    }

    internal static Transform FindPath(Scene scene, string scenePath)
    {
        if (string.IsNullOrWhiteSpace(scenePath))
        {
            return null;
        }

        string[] segments = scenePath.Split('/');
        Transform current = FindRoot(scene, segments[0]);
        for (int index = 1; current != null && index < segments.Length; index++)
        {
            current = current.Find(segments[index]);
        }

        return current;
    }

    internal static Transform FindRoot(Scene scene, string rootName)
    {
        return scene.GetRootGameObjects()
            .FirstOrDefault(root => root.name == rootName)
            ?.transform;
    }

    internal static Vector3 ToVector3(float[] values)
    {
        return values != null && values.Length == 3
            ? new Vector3(values[0], values[1], values[2])
            : Vector3.zero;
    }

    private static bool SequenceEqual(string[] first, string[] second)
    {
        return (first ?? Array.Empty<string>()).SequenceEqual(second ?? Array.Empty<string>());
    }

    private static string Format(Vector3 value)
    {
        return $"({value.x:F2},{value.y:F2},{value.z:F2})";
    }
}
