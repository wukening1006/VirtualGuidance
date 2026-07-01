using UnityEngine;

/// <summary>
/// 中文说明：Week2 调试入口，用于切换 physical space 并快速复现 A/B/C 三类 RDW 风险冲突。
/// </summary>
public sealed class PhysicalSpaceDebugController : MonoBehaviour
{
    [Header("调试依赖")]
    [Tooltip("负责加载和切换 physical tracking space 的管理器；未配置时自动查找。")]
    [SerializeField] private PhysicalSpaceManager physicalSpaceManager;
    [Tooltip("提供可复现实验用模拟 physical pose 的追踪器；未配置时自动查找。")]
    [SerializeField] private UserPoseTracker userPoseTracker;
    [Tooltip("负责在空间或位姿切换后立即刷新 risk state 的计算器；未配置时自动查找。")]
    [SerializeField] private PhysicalRiskEvaluator riskEvaluator;

    private void Awake()
    {
        ResolveReferences();
    }

    private void Update()
    {
        // 调试热键只改变 physical 实验条件，不改虚拟展厅，确保同场景下比较 baseline。
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
        else if (Input.GetKeyDown(KeyCode.F1))
        {
            ReproduceConflictA();
        }
        else if (Input.GetKeyDown(KeyCode.F2))
        {
            ReproduceConflictB();
        }
        else if (Input.GetKeyDown(KeyCode.F3))
        {
            ReproduceConflictC();
        }
    }

    /// <summary>
    /// 中文说明：切换到宽敞空间，预期允许沿主线自然前进。
    /// </summary>
    public void SetWideRoom()
    {
        SetPhysicalSpace("wide_room");
    }

    /// <summary>
    /// 中文说明：切换到狭窄空间，用于复现前方边界风险。
    /// </summary>
    public void SetNarrowRoom()
    {
        SetPhysicalSpace("narrow_room");
    }

    /// <summary>
    /// 中文说明：切换到障碍物空间，用于验证左侧或左前方阻塞时的重定向。
    /// </summary>
    public void SetObstacleRoom()
    {
        SetPhysicalSpace("obstacle_room");
    }

    /// <summary>
    /// 中文说明：切换到 L 形空间，用于验证非矩形 tracking space 下的方向判断。
    /// </summary>
    public void SetLShapedRoom()
    {
        SetPhysicalSpace("l_shaped_room");
    }

    /// <summary>
    /// 中文说明：复现 Conflict A，语义目标在前方但真实前方接近边界。
    /// </summary>
    public void ReproduceConflictA()
    {
        SetPoseAndSpace("narrow_room", new Vector2(1f, 4.3f), 0f);
    }

    /// <summary>
    /// 中文说明：复现 Conflict B，左侧语义目标相关但 obstacle_room 中左侧路径受阻。
    /// </summary>
    public void ReproduceConflictB()
    {
        SetPoseAndSpace("obstacle_room", new Vector2(2.7f, 2.2f), 0f);
    }

    /// <summary>
    /// 中文说明：复现 Conflict C，让所有自然方向接近危险区域，预期触发 reset_required。
    /// </summary>
    public void ReproduceConflictC()
    {
        SetPoseAndSpace("wide_room", new Vector2(0.3f, 0.3f), 225f);
    }

    /// <summary>
    /// 中文说明：注入调试控制器依赖，支持场景构建器统一绑定而不是运行时查找。
    /// </summary>
    /// <param name="spaceManager">负责加载和切换 physical space 的管理器。</param>
    /// <param name="poseTracker">负责提供模拟 physical pose 的追踪器。</param>
    /// <param name="evaluator">负责重新计算 deterministic risk state 的计算器。</param>
    public void Configure(PhysicalSpaceManager spaceManager, UserPoseTracker poseTracker, PhysicalRiskEvaluator evaluator)
    {
        physicalSpaceManager = spaceManager;
        userPoseTracker = poseTracker;
        riskEvaluator = evaluator;
    }

    private void SetPoseAndSpace(string spaceId, Vector2 physicalPosition, float yawDegrees)
    {
        ResolveReferences();
        if (userPoseTracker != null)
        {
            // 先更新 physical pose 再切换/刷新空间，保证风险计算读取的是目标冲突状态。
            userPoseTracker.SetSimulatedPhysicalPose(physicalPosition, yawDegrees);
        }

        SetPhysicalSpace(spaceId);
    }

    private void SetPhysicalSpace(string spaceId)
    {
        ResolveReferences();
        if (physicalSpaceManager == null)
        {
            Debug.LogWarning("PhysicalSpaceDebugController cannot find PhysicalSpaceManager.", this);
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

        if (userPoseTracker == null)
        {
            userPoseTracker = FindObjectOfType<UserPoseTracker>();
        }

        if (riskEvaluator == null)
        {
            riskEvaluator = FindObjectOfType<PhysicalRiskEvaluator>();
        }
    }
}
