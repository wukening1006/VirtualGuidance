using UnityEngine;

/// <summary>
/// 运行时调试入口，仅用于切换 physical space；冲突状态应由任务流程、用户位姿和风险计算自然触发。
/// </summary>
public sealed class PhysicalSpaceDebugController : MonoBehaviour
{
    [Header("调试依赖")]
    [Tooltip("负责加载和切换 physical tracking space 的管理器；未配置时自动查找。")]
    [SerializeField] private PhysicalSpaceManager physicalSpaceManager;
    [Tooltip("负责在空间或位姿切换后立即刷新 risk state 的计算器；未配置时自动查找。")]
    [SerializeField] private PhysicalRiskEvaluator riskEvaluator;

    private void Awake()
    {
        ResolveReferences();
    }

    private void Update()
    {
        // 这里只允许切换空间配置；具体风险冲突必须在 T1-T4 任务运行中由用户位姿和 verifier 流程计算出来。
        if (Input.GetKeyDown(KeyCode.Alpha1))
        {
            SetWideRoom();
        }
        else if (Input.GetKeyDown(KeyCode.Alpha2))
        {
            SetNarrowRoom();
        }
        else if (Input.GetKeyDown(KeyCode.Alpha3))
        {
            SetObstacleRoom();
        }
        else if (Input.GetKeyDown(KeyCode.Alpha4))
        {
            SetLShapedRoom();
        }
    }

    /// <summary>
    /// 切换到宽敞空间，预期允许沿主线自然前进。
    /// </summary>
    public void SetWideRoom()
    {
        SetPhysicalSpace("wide_room");
    }

    /// <summary>
    /// 切换到狭窄空间；是否出现前方风险由用户运行任务时的 physical pose 决定。
    /// </summary>
    public void SetNarrowRoom()
    {
        SetPhysicalSpace("narrow_room");
    }

    /// <summary>
    /// 切换到障碍物空间；是否阻塞候选方向由任务运行时的位姿和几何计算决定。
    /// </summary>
    public void SetObstacleRoom()
    {
        SetPhysicalSpace("obstacle_room");
    }

    /// <summary>
    /// 切换到 L 形空间，用于验证非矩形 tracking space 下的方向判断。
    /// </summary>
    public void SetLShapedRoom()
    {
        SetPhysicalSpace("l_shaped_room");
    }

    /// <summary>
    /// 注入调试控制器依赖，支持场景构建器统一绑定而不是运行时查找。
    /// </summary>
    /// <param name="spaceManager">负责加载和切换 physical space 的管理器。</param>
    /// <param name="evaluator">负责重新计算 deterministic risk state 的计算器。</param>
    public void Configure(PhysicalSpaceManager spaceManager, PhysicalRiskEvaluator evaluator)
    {
        physicalSpaceManager = spaceManager;
        riskEvaluator = evaluator;
    }

    private void SetPhysicalSpace(string spaceId)
    {
        ResolveReferences();
        if (physicalSpaceManager == null)
        {
            Debug.LogWarning("[PhysicalSpaceDebugController] 未找到 PhysicalSpaceManager。", this);
            return;
        }

        if (physicalSpaceManager.SetPhysicalSpace(spaceId) && riskEvaluator != null)
        {
            riskEvaluator.EvaluateNow();
        }
    }

    private void ResolveReferences()
    {
        if (physicalSpaceManager == null)
        {
            physicalSpaceManager = FindObjectOfType<PhysicalSpaceManager>();
        }

        if (riskEvaluator == null)
        {
            riskEvaluator = FindObjectOfType<PhysicalRiskEvaluator>();
        }
    }
}
