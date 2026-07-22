using UnityEngine;

/// <summary>
/// 分别维护用户虚拟位姿与 Physical Room 位姿；XR Raw Tracking Pose 必须先经过校准组件转换。
/// </summary>
public sealed class UserPoseTracker : MonoBehaviour
{
    [Header("虚拟用户引用")]
    [Tooltip("提供用户虚拟位置和朝向的 Transform；XR 模式下通常为 HMD/Camera。")]
    [SerializeField] private Transform virtualUserRoot;
    [Tooltip("接收 Rotation Gain 的 XR Origin；必须包含虚拟位姿来源，且不得直接绑定 HMD/Camera。")]
    [SerializeField] private Transform virtualRotationRoot;

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

    /// <summary>提供用户当前虚拟位置和朝向的 Transform。</summary>
    public Transform VirtualPoseSource => virtualUserRoot;

    /// <summary>接收额外虚拟 yaw 的根节点；仅返回包含虚拟位姿来源的有效根节点。</summary>
    public Transform VirtualRotationRoot => HasValidVirtualPoseContract ? virtualRotationRoot : null;

    /// <summary>虚拟位姿来源与 Rotation Gain 根节点是否构成同一 XR 层级。</summary>
    public bool HasValidVirtualPoseContract =>
        virtualUserRoot != null &&
        IsValidRotationRoot(virtualRotationRoot) &&
        ContainsVirtualPoseSource(virtualRotationRoot, virtualUserRoot);

    /// <summary>兼容既有调用的虚拟位姿引用；新代码应使用 VirtualPoseSource。</summary>
    public Transform VirtualUserRoot => VirtualPoseSource;

    /// <summary>
    /// 获取用户在 physical tracking space 的 XZ 平面坐标。
    /// </summary>
    public Vector2 PhysicalPositionXZ => simulatedPhysicalPosition;

    /// <summary>
    /// 获取用户在 physical tracking space 的二维朝向，用于 clearance 与风险计算。
    /// </summary>
    public Vector2 PhysicalForwardXZ => YawToForward(simulatedPhysicalYawDegrees);

    /// <summary>
    /// 设置虚拟用户根节点，供场景构建器或 XR 初始化流程显式绑定用户对象。
    /// </summary>
    /// <param name="root">代表用户虚拟位置和朝向的 Transform。</param>
    public void ConfigureVirtualUserRoot(Transform root)
    {
        ConfigureVirtualPoseSource(root);
        if (IsValidRotationRoot(root) && ContainsVirtualPoseSource(root, virtualUserRoot))
        {
            virtualRotationRoot = root;
        }
    }

    /// <summary>
    /// 设置用户虚拟位姿来源；该引用可为 HMD/Camera，但不会自动成为 Rotation Gain 目标。
    /// </summary>
    /// <param name="source">提供用户虚拟位置和朝向的 Transform。</param>
    public void ConfigureVirtualPoseSource(Transform source)
    {
        virtualUserRoot = source;
    }

    /// <summary>
    /// 设置接收 Rotation Gain 的虚拟根节点；Camera/HMD 或不包含位姿来源的引用会被拒绝。
    /// </summary>
    /// <param name="root">XR Origin、Locomotion Root 或 Virtual Root。</param>
    /// <returns>引用满足 Gain 作用对象契约时返回 true。</returns>
    public bool ConfigureVirtualRotationRoot(Transform root)
    {
        if (!IsValidRotationRoot(root) || !ContainsVirtualPoseSource(root, virtualUserRoot))
        {
            return false;
        }

        virtualRotationRoot = root;
        return true;
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

    /// <summary>
    /// 写入已由 PhysicalSpaceCalibration 转换的 Physical Room 位姿。
    /// </summary>
    /// <param name="position">physical XZ 平面位置，x/y 分别对应 Unity X/Z，单位：米。</param>
    /// <param name="yawDegrees">以 +Z 为 0 度的水平朝向角，单位：度。</param>
    public void SetCalibratedPhysicalPose(Vector2 position, float yawDegrees)
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

    private static bool IsValidRotationRoot(Transform root)
    {
        return root != null && root.GetComponent<Camera>() == null;
    }

    private static bool ContainsVirtualPoseSource(Transform rotationRoot, Transform poseSource)
    {
        return rotationRoot != null &&
               poseSource != null &&
               (poseSource == rotationRoot || poseSource.IsChildOf(rotationRoot));
    }
}
