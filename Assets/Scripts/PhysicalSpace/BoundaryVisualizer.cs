using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 在虚拟展厅旁绘制 physical space 边界、障碍物、用户物理位姿和安全方向。
/// </summary>
public sealed class BoundaryVisualizer : MonoBehaviour
{
    [Header("数据来源")]
    [Tooltip("提供当前 physical space 边界和障碍物配置的管理器；未配置时自动查找。")]
    [SerializeField] private PhysicalSpaceManager physicalSpaceManager;
    [Tooltip("提供用户 physical pose 的追踪器；未配置时自动查找。")]
    [SerializeField] private UserPoseTracker userPoseTracker;
    [Tooltip("提供方向安全/危险标签的计算器；未配置时自动查找。")]
    [SerializeField] private PhysicalRiskEvaluator riskEvaluator;

    [Header("可视化参数")]
    [Tooltip("physical debug view 在 Unity 世界中的起点，单位：米；用于把调试图放在展厅旁边。")]
    [SerializeField] private Vector3 visualizationOrigin = new Vector3(7f, 0.04f, 0f);
    [Tooltip("forward/left/right 方向箭头长度，单位：米。")]
    [SerializeField] private float directionArrowLength = 0.9f;
    [Tooltip("边界线和方向线宽度，单位：米。")]
    [SerializeField] private float lineWidth = 0.04f;

    // 三个方向线随 risk state 改色，使用字典可避免每帧按名称查找对象。
    private readonly Dictionary<string, LineRenderer> directionLines = new Dictionary<string, LineRenderer>();

    // 障碍物随 physical space 切换重建，需要记录实例以便清理旧空间内容。
    private readonly List<GameObject> obstacleObjects = new List<GameObject>();
    private Transform visualizationRoot;
    private LineRenderer boundaryLine;
    private GameObject userMarker;
    private Material lineMaterial;

    private void Awake()
    {
        ResolveReferences();
        EnsureObjects();
    }

    private void OnEnable()
    {
        ResolveReferences();
        if (physicalSpaceManager != null)
        {
            physicalSpaceManager.CurrentSpaceChanged += HandleSpaceChanged;
        }

        if (riskEvaluator != null)
        {
            riskEvaluator.RiskStateChanged += HandleRiskStateChanged;
        }

        RebuildSpace();
        RefreshPoseAndDirections();
    }

    private void OnDisable()
    {
        if (physicalSpaceManager != null)
        {
            physicalSpaceManager.CurrentSpaceChanged -= HandleSpaceChanged;
        }

        if (riskEvaluator != null)
        {
            riskEvaluator.RiskStateChanged -= HandleRiskStateChanged;
        }
    }

    private void Update()
    {
        RefreshPoseAndDirections();
    }

    /// <summary>
    /// 中文说明：注入 physical space、pose 和 risk 依赖，并重建当前调试可视化。
    /// </summary>
    /// <param name="spaceManager">提供当前 physical space 边界和障碍物配置的管理器。</param>
    /// <param name="poseTracker">提供用户 physical pose 的追踪器。</param>
    /// <param name="evaluator">提供方向风险标签的计算器。</param>
    public void Configure(PhysicalSpaceManager spaceManager, UserPoseTracker poseTracker, PhysicalRiskEvaluator evaluator)
    {
        if (physicalSpaceManager != null)
        {
            physicalSpaceManager.CurrentSpaceChanged -= HandleSpaceChanged;
        }

        if (riskEvaluator != null)
        {
            riskEvaluator.RiskStateChanged -= HandleRiskStateChanged;
        }

        physicalSpaceManager = spaceManager;
        userPoseTracker = poseTracker;
        riskEvaluator = evaluator;

        if (isActiveAndEnabled && physicalSpaceManager != null)
        {
            physicalSpaceManager.CurrentSpaceChanged += HandleSpaceChanged;
        }

        if (isActiveAndEnabled && riskEvaluator != null)
        {
            riskEvaluator.RiskStateChanged += HandleRiskStateChanged;
        }

        EnsureObjects();
        RebuildSpace();
        RefreshPoseAndDirections();
    }

    private void RebuildSpace()
    {
        EnsureObjects();
        ClearObstacles();

        var space = physicalSpaceManager != null ? physicalSpaceManager.CurrentSpace : null;
        if (space == null)
        {
            boundaryLine.positionCount = 0;
            return;
        }

        boundaryLine.positionCount = space.boundary.Length + 1;
        for (var i = 0; i < space.boundary.Length; i++)
        {
            // physical 点映射到虚拟展厅旁的调试区域，避免覆盖真实虚拟展品布局。
            boundaryLine.SetPosition(i, ToWorld(space.boundary[i]));
        }

        boundaryLine.SetPosition(space.boundary.Length, ToWorld(space.boundary[0]));

        if (space.obstacles == null)
        {
            return;
        }

        foreach (var obstacle in space.obstacles)
        {
            // 障碍物使用薄圆柱体表达 footprint，便于从 Scene View 俯视检查阻挡方向。
            var obstacleObject = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            obstacleObject.name = $"PhysicalObstacle_{obstacle.id}";
            obstacleObject.transform.SetParent(visualizationRoot, false);
            obstacleObject.transform.position = ToWorld(obstacle.center) + Vector3.up * 0.02f;
            obstacleObject.transform.localScale = new Vector3(obstacle.radius * 2f, 0.04f, obstacle.radius * 2f);
            var renderer = obstacleObject.GetComponent<Renderer>();
            if (renderer != null)
            {
                renderer.sharedMaterial = CreateColorMaterial(new Color(0.9f, 0.18f, 0.12f, 0.65f));
            }

            obstacleObjects.Add(obstacleObject);
        }
    }

    private void RefreshPoseAndDirections()
    {
        EnsureObjects();
        if (userPoseTracker == null)
        {
            return;
        }

        var position = userPoseTracker.PhysicalPositionXZ;
        var forward = userPoseTracker.PhysicalForwardXZ;
        if (forward.sqrMagnitude < 0.0001f)
        {
            // 输入缺失时按实验初始朝向 +Z 处理，保持调试视图可读。
            forward = Vector2.up;
        }

        var left = new Vector2(-forward.y, forward.x);
        var right = new Vector2(forward.y, -forward.x);

        userMarker.transform.position = ToWorld(position) + Vector3.up * 0.04f;
        UpdateDirectionLine("forward", position, forward);
        UpdateDirectionLine("left", position, left);
        UpdateDirectionLine("right", position, right);
    }

    private void UpdateDirectionLine(string directionName, Vector2 origin, Vector2 direction)
    {
        var line = directionLines[directionName];
        line.positionCount = 2;
        line.SetPosition(0, ToWorld(origin) + Vector3.up * 0.05f);
        line.SetPosition(1, ToWorld(origin + direction.normalized * directionArrowLength) + Vector3.up * 0.05f);
        line.startColor = DirectionColor(directionName);
        line.endColor = line.startColor;
    }

    private Color DirectionColor(string directionName)
    {
        var state = riskEvaluator != null ? riskEvaluator.CurrentRiskState : null;
        if (state == null)
        {
            return Color.gray;
        }

        if (Contains(state.unsafe_directions, directionName))
        {
            // 红色表示 verifier 不应允许该方向的自然导览指令。
            return new Color(0.95f, 0.15f, 0.1f);
        }

        if (Contains(state.safe_directions, directionName))
        {
            // 绿色表示该方向可作为 semantic redirection 的候选方向。
            return new Color(0.1f, 0.8f, 0.25f);
        }

        return new Color(1f, 0.68f, 0.1f);
    }

    private void EnsureObjects()
    {
        if (visualizationRoot == null)
        {
            // 所有调试对象统一挂在组件下，便于场景层级管理和后续整体隐藏。
            var rootObject = new GameObject("PhysicalSpaceDebugView");
            rootObject.transform.SetParent(transform, false);
            visualizationRoot = rootObject.transform;
        }

        if (lineMaterial == null)
        {
            // LineRenderer 优先使用 Sprites/Default，确保颜色在调试视图中直接可见。
            var shader = Shader.Find("Sprites/Default") != null ? Shader.Find("Sprites/Default") : Shader.Find("Standard");
            lineMaterial = new Material(shader);
        }

        if (boundaryLine == null)
        {
            boundaryLine = CreateLine("PhysicalBoundary", Color.white);
        }

        EnsureDirectionLine("forward");
        EnsureDirectionLine("left");
        EnsureDirectionLine("right");

        if (userMarker == null)
        {
            userMarker = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            userMarker.name = "PhysicalUserPose";
            userMarker.transform.SetParent(visualizationRoot, false);
            userMarker.transform.localScale = new Vector3(0.18f, 0.05f, 0.18f);
            var renderer = userMarker.GetComponent<Renderer>();
            if (renderer != null)
            {
                renderer.sharedMaterial = CreateColorMaterial(new Color(0.12f, 0.5f, 1f, 0.85f));
            }
        }
    }

    private void EnsureDirectionLine(string directionName)
    {
        if (!directionLines.ContainsKey(directionName))
        {
            directionLines[directionName] = CreateLine($"Direction_{directionName}", Color.gray);
        }
    }

    private LineRenderer CreateLine(string lineName, Color color)
    {
        var lineObject = new GameObject(lineName);
        lineObject.transform.SetParent(visualizationRoot, false);
        var line = lineObject.AddComponent<LineRenderer>();
        line.useWorldSpace = true;
        line.sharedMaterial = lineMaterial;
        line.startWidth = lineWidth;
        line.endWidth = lineWidth;
        line.numCapVertices = 4;
        line.startColor = color;
        line.endColor = color;
        return line;
    }

    private Material CreateColorMaterial(Color color)
    {
        var material = new Material(Shader.Find("Standard"));
        material.color = color;
        if (color.a < 1f)
        {
            // 半透明障碍物需要显式设置 Standard shader 的混合参数，否则 alpha 不会生效。
            material.SetFloat("_Mode", 3f);
            material.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            material.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            material.SetInt("_ZWrite", 0);
            material.EnableKeyword("_ALPHABLEND_ON");
            material.renderQueue = 3000;
        }

        return material;
    }

    private void ClearObstacles()
    {
        foreach (var obstacleObject in obstacleObjects)
        {
            if (obstacleObject != null)
            {
                Destroy(obstacleObject);
            }
        }

        obstacleObjects.Clear();
    }

    private Vector3 ToWorld(Vector2 physicalPoint)
    {
        return visualizationOrigin + new Vector3(physicalPoint.x, 0f, physicalPoint.y);
    }

    private void ResolveReferences()
    {
        if (physicalSpaceManager == null)
        {
            physicalSpaceManager = FindObjectOfType<PhysicalSpaceManager>();
        }

        if (userPoseTracker == null)
        {
            userPoseTracker = FindObjectOfType<UserPoseTracker>();
        }

        if (riskEvaluator == null)
        {
            riskEvaluator = FindObjectOfType<PhysicalRiskEvaluator>();
        }
    }

    private void HandleSpaceChanged(PhysicalSpaceConfig _)
    {
        RebuildSpace();
    }

    private void HandleRiskStateChanged(PhysicalRiskState _)
    {
        RefreshPoseAndDirections();
    }

    private static bool Contains(string[] values, string target)
    {
        if (values == null)
        {
            return false;
        }

        foreach (var value in values)
        {
            if (value == target)
            {
                return true;
            }
        }

        return false;
    }
}
