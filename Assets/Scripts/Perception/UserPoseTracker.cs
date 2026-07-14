using UnityEngine;

/// <summary>
/// 维护用户虚拟位姿和可模拟的 physical 位姿，后续可替换为真实 XR tracking 输入。
/// </summary>
public sealed class UserPoseTracker : MonoBehaviour
{
    // 允许 Inspector 绑定真实 XR Origin；缺省时会在场景中按规范名称兜底查找。
    [Header("虚拟用户引用")]
    [Tooltip("用户在虚拟展厅中的根节点；未配置时会尝试查找 XR_Origin_or_UserStart。")]
    [SerializeField] private Transform virtualUserRoot;

    // 第一阶段使用模拟 physical pose 作为任务运行输入，后续可替换为真实头显 tracking 数据。
    [Header("模拟 Physical 位姿")]
    [Tooltip("用户在 physical tracking space 的二维位置，单位：米；Vector2.y 对应 Unity Z 方向。")]
    [SerializeField] private Vector2 simulatedPhysicalPosition = new Vector2(1f, 4.3f);
    [Tooltip("用户在 physical tracking space 的水平朝向角，单位：度；0 度表示朝向 +Z。")]
    [SerializeField] private float simulatedPhysicalYawDegrees;

    /// <summary>
    /// 获取用户在虚拟展厅中的世界坐标，用于导览目标和日志记录。
    /// </summary>
    public Vector3 UserVirtualPosition => virtualUserRoot != null ? virtualUserRoot.position : Vector3.zero;

    /// <summary>
    /// 获取用户在虚拟展厅中的朝向，缺省返回 +Z 以匹配实验初始方向。
    /// </summary>
    public Vector3 UserVirtualForward => virtualUserRoot != null ? virtualUserRoot.forward : Vector3.forward;

    public Transform VirtualUserRoot => virtualUserRoot;

    /// <summary>
    /// 获取用户在 physical tracking space 的 XZ 平面坐标。
    /// </summary>
    public Vector2 PhysicalPositionXZ => simulatedPhysicalPosition;

    /// <summary>
    /// 获取用户在 physical tracking space 的二维朝向，用于 clearance 与风险计算。
    /// </summary>
    public Vector2 PhysicalForwardXZ => YawToForward(simulatedPhysicalYawDegrees);

    private void Awake()
    {
        if (virtualUserRoot == null)
        {
            var userStart = GameObject.Find("XR_Origin_or_UserStart");
            if (userStart != null)
            {
                virtualUserRoot = userStart.transform;
            }
        }
    }

    /// <summary>
    /// 设置虚拟用户根节点，供场景构建器或 XR 初始化流程显式绑定用户对象。
    /// </summary>
    /// <param name="root">代表用户虚拟位置和朝向的 Transform。</param>
    public void ConfigureVirtualUserRoot(Transform root)
    {
        virtualUserRoot = root;
    }

    /// <summary>
    /// 设置任务运行时使用的模拟 physical 位姿，冲突状态由该位姿与当前任务/空间共同触发。
    /// </summary>
    /// <param name="position">用户在 physical tracking space 中的二维位置，单位：米。</param>
    /// <param name="yawDegrees">以 +Z 为 0 度的水平朝向角，单位：度。</param>
    public void SetSimulatedPhysicalPose(Vector2 position, float yawDegrees)
    {
        SetPhysicalPose(position, yawDegrees);
    }

    public void SetTrackedPhysicalPose(Vector2 position, float yawDegrees)
    {
        SetPhysicalPose(position, yawDegrees);
    }

    private void SetPhysicalPose(Vector2 position, float yawDegrees)
    {
        simulatedPhysicalPosition = position;
        simulatedPhysicalYawDegrees = yawDegrees;
    }

    private static Vector2 YawToForward(float yawDegrees)
    {
        // Unity 展厅以 +Z 作为前方；二维 physical 坐标用 y 分量承载 Z 方向。
        var radians = yawDegrees * Mathf.Deg2Rad;
        var forward = new Vector2(Mathf.Sin(radians), Mathf.Cos(radians));
        return forward.sqrMagnitude > 0.0001f ? forward.normalized : Vector2.up;
    }
}
