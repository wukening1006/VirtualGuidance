using System;
using System.IO;
using UnityEngine;

internal static class SceneSemanticsConfigLoader
{
    internal const string DefaultConfigPath = "Assets/Configs/scene_semantics.json";

    internal static SceneSemanticsConfig Load(string path = DefaultConfigPath)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("找不到场景语义配置。", path);
        }

        SceneSemanticsConfig config = JsonUtility.FromJson<SceneSemanticsConfig>(File.ReadAllText(path));
        if (config == null || config.schema_version != 2 || config.layout == null || config.exhibits == null)
        {
            throw new InvalidDataException("scene_semantics.json 必须使用 schema v2，并包含 layout 与 exhibits。");
        }

        return config;
    }

    internal static void Save(SceneSemanticsConfig config, string path = DefaultConfigPath)
    {
        if (config == null)
        {
            throw new ArgumentNullException(nameof(config));
        }

        File.WriteAllText(path, JsonUtility.ToJson(config, true) + Environment.NewLine);
    }
}

[Serializable]
internal sealed class SceneSemanticsConfig
{
    [SerializeField] internal int schema_version;
    [SerializeField] internal string scene_id;
    [SerializeField] internal string scene_name;
    [SerializeField] internal string coordinate_system;
    [SerializeField] internal GalleryLayoutConfig layout;
    [SerializeField] internal ExhibitSemanticsConfig[] exhibits;
}

[Serializable]
internal sealed class GalleryLayoutConfig
{
    [SerializeField] internal string units;
    [SerializeField] internal float[] floor_origin;
    [SerializeField] internal float[] floor_center;
    [SerializeField] internal GalleryFloorSizeConfig floor_size_m;
    [SerializeField] internal GalleryFloorBoundsConfig floor_bounds;
    [SerializeField] internal string entrance_side;
}

[Serializable]
internal sealed class GalleryFloorSizeConfig
{
    [SerializeField] internal float width;
    [SerializeField] internal float length;
}

[Serializable]
internal sealed class GalleryFloorBoundsConfig
{
    [SerializeField] internal float x_min;
    [SerializeField] internal float x_max;
    [SerializeField] internal float z_min;
    [SerializeField] internal float z_max;
}

[Serializable]
internal sealed class ExhibitSemanticsConfig
{
    [SerializeField] internal string id;
    [SerializeField] internal string name;
    [SerializeField] internal string scene_path;
    [SerializeField] internal string semantic_role;
    [SerializeField] internal float[] position;
    [SerializeField] internal float rotation_y_degrees;
    [SerializeField] internal ExhibitCabinetConfig cabinet;
    [SerializeField] internal float[] viewpoint;
    [SerializeField] internal float[] gaze_target;
    [SerializeField] internal string[] tags;
    [SerializeField] internal string[] related_exhibits;
    [SerializeField] internal string description;
}

[Serializable]
internal sealed class ExhibitCabinetConfig
{
    [SerializeField] internal float width_m;
    [SerializeField] internal float depth_m;
    [SerializeField] internal float height_m;
}
