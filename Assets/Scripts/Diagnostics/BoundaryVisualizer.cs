using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 在虚拟展厅旁绘制 physical space、当前朝向和 Continuous Recovery Search 诊断。
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
    [Tooltip("提供最近一次 Continuous Recovery Heading Search；未配置时自动查找。")]
    [SerializeField] private GuidanceCoordinator guidanceCoordinator;
    [Tooltip("提供固定校准点和校准状态；未校准时隐藏 Physical User Marker。")]
    [SerializeField] private PhysicalSpaceCalibration physicalSpaceCalibration;

    [Header("可视化参数")]
    [Tooltip("physical debug view 在 Unity 世界中的起点，单位：米；用于把调试图放在展厅旁边。")]
    [SerializeField] private Vector3 visualizationOrigin = new Vector3(7f, 0.04f, 0f);
    [Tooltip("当前朝向和搜索射线的最大显示长度，单位：米。")]
    [SerializeField] private float directionArrowLength = 0.9f;
    [Tooltip("边界线和方向线宽度，单位：米。")]
    [SerializeField] private float lineWidth = 0.04f;
    [Tooltip("研究员专用调试 Layer；运行时生成的所有可视化对象都会使用该 Layer。")]
    [SerializeField] private string researcherOnlyLayerName = "ResearcherOnly";

    // 搜索射线仅在新结果到达时调整数量，普通帧只更新位姿。
    private readonly List<LineRenderer> searchLines = new List<LineRenderer>();

    // 障碍物随 physical space 切换重建，需要记录实例以便清理旧空间内容。
    private readonly List<GameObject> obstacleObjects = new List<GameObject>();
    private Transform visualizationRoot;
    private LineRenderer boundaryLine;
    private LineRenderer currentHeadingLine;
    private LineRenderer bestHeadingLine;
    private GameObject calibrationPointMarker;
    private GameObject userMarker;
    private Material lineMaterial;
    private Vector2 physicalSpaceCenter;
    private RecoveryHeadingSearchResult latestSearchResult;

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
        if (guidanceCoordinator != null)
        {
            guidanceCoordinator.RecoverySearchCompleted += HandleRecoverySearchCompleted;
            latestSearchResult = guidanceCoordinator.LatestRecoverySearchResult;
        }

        if (physicalSpaceCalibration != null)
        {
            physicalSpaceCalibration.StateChanged += HandleCalibrationStateChanged;
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
        if (guidanceCoordinator != null)
        {
            guidanceCoordinator.RecoverySearchCompleted -= HandleRecoverySearchCompleted;
        }

        if (physicalSpaceCalibration != null)
        {
            physicalSpaceCalibration.StateChanged -= HandleCalibrationStateChanged;
        }
    }

    private void Update()
    {
        RefreshPoseAndDirections();
    }

    /// <summary>
    /// 注入 physical space、pose 和 risk 依赖，并重建当前调试可视化。
    /// </summary>
    /// <param name="spaceManager">提供当前 physical space 边界和障碍物配置的管理器。</param>
    /// <param name="poseTracker">提供用户 physical pose 的追踪器。</param>
    /// <param name="evaluator">提供方向风险标签的计算器。</param>
    public void Configure(
        PhysicalSpaceManager spaceManager,
        UserPoseTracker poseTracker,
        PhysicalRiskEvaluator evaluator,
        PhysicalSpaceCalibration calibration = null,
        GuidanceCoordinator coordinator = null)
    {
        if (physicalSpaceManager != null)
        {
            physicalSpaceManager.CurrentSpaceChanged -= HandleSpaceChanged;
        }

        if (riskEvaluator != null)
        {
            riskEvaluator.RiskStateChanged -= HandleRiskStateChanged;
        }
        if (guidanceCoordinator != null)
        {
            guidanceCoordinator.RecoverySearchCompleted -= HandleRecoverySearchCompleted;
        }

        if (physicalSpaceCalibration != null)
        {
            physicalSpaceCalibration.StateChanged -= HandleCalibrationStateChanged;
        }

        physicalSpaceManager = spaceManager;
        userPoseTracker = poseTracker;
        riskEvaluator = evaluator;
        if (calibration != null) physicalSpaceCalibration = calibration;
        if (coordinator != null) guidanceCoordinator = coordinator;

        if (isActiveAndEnabled && physicalSpaceManager != null)
        {
            physicalSpaceManager.CurrentSpaceChanged += HandleSpaceChanged;
        }

        if (isActiveAndEnabled && riskEvaluator != null)
        {
            riskEvaluator.RiskStateChanged += HandleRiskStateChanged;
        }
        if (isActiveAndEnabled && guidanceCoordinator != null)
        {
            guidanceCoordinator.RecoverySearchCompleted += HandleRecoverySearchCompleted;
            latestSearchResult = guidanceCoordinator.LatestRecoverySearchResult;
        }

        if (isActiveAndEnabled && physicalSpaceCalibration != null)
        {
            physicalSpaceCalibration.StateChanged += HandleCalibrationStateChanged;
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
            physicalSpaceCenter = Vector2.zero;
            boundaryLine.positionCount = 0;
            return;
        }

        physicalSpaceCenter = ComputeBoundaryCenter(space.boundary);
        RefreshCalibrationPoint();

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
            ApplyResearcherLayer(obstacleObject);
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
        RefreshCalibrationPoint();
        if (userPoseTracker == null)
        {
            return;
        }

        var showPhysicalUser = physicalSpaceCalibration == null || physicalSpaceCalibration.IsCalibrationValid;
        userMarker.SetActive(showPhysicalUser);
        currentHeadingLine.gameObject.SetActive(showPhysicalUser);
        bestHeadingLine.gameObject.SetActive(showPhysicalUser && latestSearchResult?.Plan?.IsSuccess == true);
        foreach (var line in searchLines) line.gameObject.SetActive(showPhysicalUser);
        if (!showPhysicalUser)
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

        userMarker.transform.position = ToWorld(position) + Vector3.up * 0.04f;
        UpdateHeadingLine(
            currentHeadingLine,
            position,
            forward,
            directionArrowLength,
            CurrentHeadingColor());
        RefreshSearchLines(position);
    }

    private void UpdateHeadingLine(
        LineRenderer line,
        Vector2 origin,
        Vector2 direction,
        float length,
        Color color)
    {
        line.positionCount = 2;
        line.SetPosition(0, ToWorld(origin) + Vector3.up * 0.05f);
        line.SetPosition(1, ToWorld(origin + direction.normalized * length) + Vector3.up * 0.05f);
        line.startColor = color;
        line.endColor = color;
    }

    private void RefreshSearchLines(Vector2 origin)
    {
        var evaluations = latestSearchResult?.Evaluations;
        var required = evaluations != null ? evaluations.Count : 0;
        EnsureSearchLineCount(required);
        for (var index = 0; index < searchLines.Count; index++)
        {
            var active = index < required;
            var line = searchLines[index];
            line.gameObject.SetActive(active);
            if (!active) continue;

            var sample = evaluations[index];
            var length = Mathf.Clamp(sample.ClearanceMeters, 0.12f, directionArrowLength);
            UpdateHeadingLine(line, origin, sample.PhysicalForward, length, SafetyColor(sample.Safety));
        }

        var plan = latestSearchResult?.Plan;
        if (plan != null && plan.IsSuccess)
        {
            bestHeadingLine.gameObject.SetActive(true);
            UpdateHeadingLine(
                bestHeadingLine,
                origin,
                plan.SafePhysicalForward,
                directionArrowLength,
                new Color(0.1f, 1f, 0.85f));
        }
        else
        {
            bestHeadingLine.gameObject.SetActive(false);
        }
    }

    private Color CurrentHeadingColor()
    {
        var state = riskEvaluator != null ? riskEvaluator.CurrentRiskState : null;
        return state != null ? SafetyColor(state.current_heading.Safety) : Color.gray;
    }

    private static Color SafetyColor(HeadingSafety safety)
    {
        switch (safety)
        {
            case HeadingSafety.Safe: return new Color(0.1f, 0.8f, 0.25f);
            case HeadingSafety.Unknown: return new Color(1f, 0.68f, 0.1f);
            case HeadingSafety.Unsafe: return new Color(1f, 0.3f, 0.08f);
            default: return new Color(0.95f, 0.1f, 0.1f);
        }
    }

    private void EnsureObjects()
    {
        if (visualizationRoot == null)
        {
            // 所有调试对象统一挂在组件下，便于场景层级管理和后续整体隐藏。
            var rootObject = new GameObject("PhysicalSpaceDebugView");
            ApplyResearcherLayer(rootObject);
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

        if (calibrationPointMarker == null)
        {
            calibrationPointMarker = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            calibrationPointMarker.name = "CalibrationPoint";
            ApplyResearcherLayer(calibrationPointMarker);
            calibrationPointMarker.transform.SetParent(visualizationRoot, false);
            calibrationPointMarker.transform.localScale = new Vector3(0.24f, 0.025f, 0.24f);
            var renderer = calibrationPointMarker.GetComponent<Renderer>();
            if (renderer != null)
            {
                renderer.sharedMaterial = CreateColorMaterial(new Color(1f, 0.82f, 0.12f, 0.9f));
            }
        }

        if (currentHeadingLine == null)
        {
            currentHeadingLine = CreateLine("CurrentPhysicalHeading", Color.gray);
        }
        if (bestHeadingLine == null)
        {
            bestHeadingLine = CreateLine("BestSafeRecoveryHeading", new Color(0.1f, 1f, 0.85f));
        }

        if (userMarker == null)
        {
            userMarker = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            userMarker.name = "PhysicalUserMarker";
            ApplyResearcherLayer(userMarker);
            userMarker.transform.SetParent(visualizationRoot, false);
            userMarker.transform.localScale = new Vector3(0.18f, 0.05f, 0.18f);
            var renderer = userMarker.GetComponent<Renderer>();
            if (renderer != null)
            {
                renderer.sharedMaterial = CreateColorMaterial(new Color(0.12f, 0.5f, 1f, 0.85f));
            }
        }
    }

    private void EnsureSearchLineCount(int required)
    {
        while (searchLines.Count < required)
        {
            searchLines.Add(CreateLine($"RecoverySearch_{searchLines.Count:000}", Color.gray));
        }
    }

    private LineRenderer CreateLine(string lineName, Color color)
    {
        var lineObject = new GameObject(lineName);
        ApplyResearcherLayer(lineObject);
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

    private void ApplyResearcherLayer(GameObject target)
    {
        if (target == null)
        {
            return;
        }

        var layer = LayerMask.NameToLayer(researcherOnlyLayerName);
        if (layer >= 0)
        {
            target.layer = layer;
        }
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

    private void RefreshCalibrationPoint()
    {
        if (calibrationPointMarker == null)
        {
            return;
        }

        var point = physicalSpaceCalibration != null
            ? physicalSpaceCalibration.CalibrationPointInRoom
            : Vector2.zero;
        var yaw = physicalSpaceCalibration != null
            ? physicalSpaceCalibration.CalibrationForwardYawDegrees
            : 0f;
        calibrationPointMarker.transform.position = ToWorld(point) + Vector3.up * 0.025f;
        calibrationPointMarker.transform.rotation = Quaternion.Euler(0f, yaw, 0f);
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
        var centered = physicalPoint - physicalSpaceCenter;
        return visualizationOrigin + new Vector3(centered.x, 0f, centered.y);
    }

    private static Vector2 ComputeBoundaryCenter(Vector2[] boundary)
    {
        if (boundary == null || boundary.Length == 0)
        {
            return Vector2.zero;
        }

        var min = boundary[0];
        var max = boundary[0];
        for (var i = 1; i < boundary.Length; i++)
        {
            min = Vector2.Min(min, boundary[i]);
            max = Vector2.Max(max, boundary[i]);
        }

        return (min + max) * 0.5f;
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

        if (guidanceCoordinator == null)
        {
            guidanceCoordinator = FindObjectOfType<GuidanceCoordinator>();
        }

        if (physicalSpaceCalibration == null)
        {
            physicalSpaceCalibration = FindObjectOfType<PhysicalSpaceCalibration>();
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

    private void HandleRecoverySearchCompleted(RecoveryHeadingSearchResult result)
    {
        latestSearchResult = result;
        RefreshPoseAndDirections();
    }

    private void HandleCalibrationStateChanged(PhysicalCalibrationState _)
    {
        RefreshPoseAndDirections();
    }

}
