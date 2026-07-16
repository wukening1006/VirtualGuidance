using UnityEngine;
using UnityEngine.XR;

/// <summary>
/// 读取 Unity XR 头显局部位姿，并将 Raw Tracking Pose 交给 PhysicalSpaceCalibration。
/// 虚拟位姿仍由头显 Transform 提供，Physical Room Pose 必须经过独立校准后写入追踪器。
/// </summary>
public sealed class XRTrackingAdapter : MonoBehaviour
{
    [SerializeField] private UserPoseTracker userPoseTracker;
    [Tooltip("将 Raw Tracking Pose 转换为 Physical Room Pose 的校准组件。")]
    [SerializeField] private PhysicalSpaceCalibration physicalSpaceCalibration;
    [SerializeField] private bool readHeadsetTrackingWhenAvailable = true;
    [SerializeField] private Transform headsetTransform;

    /// <summary>上一帧是否成功取得并应用 XR 头显位姿。</summary>
    public bool IsHeadsetTrackingActive { get; private set; }
    /// <summary>是否允许 Update 主动读取 XRNode.Head。</summary>
    public bool IsHeadsetTrackingEnabled => readHeadsetTrackingWhenAvailable;
    /// <summary>最近一次有效头显局部位置，单位：米。</summary>
    public Vector3 LastHeadLocalPosition { get; private set; }
    /// <summary>最近一次有效头显局部旋转。</summary>
    public Quaternion LastHeadLocalRotation { get; private set; } = Quaternion.identity;

    private void Awake()
    {
        ResolveReferences();
    }

    /// <summary>注入接收物理位姿的追踪器，并尝试绑定主相机作为虚拟头部。</summary>
    public void Configure(UserPoseTracker poseTracker, PhysicalSpaceCalibration calibration = null)
    {
        if (poseTracker != null)
        {
            userPoseTracker = poseTracker;
        }
        if (calibration != null)
        {
            physicalSpaceCalibration = calibration;
        }
        ResolveHeadsetTransform();
    }

    /// <summary>启用或禁用 XR 设备读取；禁用时立即将追踪状态标记为非活动。</summary>
    public void SetHeadsetTrackingEnabled(bool enabled)
    {
        readHeadsetTrackingWhenAvailable = enabled;
        if (!enabled)
        {
            IsHeadsetTrackingActive = false;
            physicalSpaceCalibration?.ReportTrackingLost();
        }
    }

    private void Update()
    {
        if (!readHeadsetTrackingWhenAvailable || userPoseTracker == null)
        {
            IsHeadsetTrackingActive = false;
            physicalSpaceCalibration?.ReportTrackingLost();
            return;
        }

        var headDevice = InputDevices.GetDeviceAtXRNode(XRNode.Head);
        // 设备无效或任一位姿分量缺失时保留上一份数据，但明确标记本帧追踪未生效。
        if (!headDevice.isValid ||
            !headDevice.TryGetFeatureValue(CommonUsages.devicePosition, out var localPosition) ||
            !headDevice.TryGetFeatureValue(CommonUsages.deviceRotation, out var localRotation))
        {
            IsHeadsetTrackingActive = false;
            physicalSpaceCalibration?.ReportTrackingLost();
            return;
        }

        ApplyTrackedPose(localPosition, localRotation, headsetTransform);
    }

    /// <summary>
    /// 应用一份头显局部位姿，将 XZ 位置和水平 yaw 写入物理位姿追踪器。
    /// </summary>
    /// <param name="localPosition">相对 XR Origin 的局部位置，单位：米。</param>
    /// <param name="localRotation">相对 XR Origin 的局部旋转。</param>
    /// <param name="virtualHead">可选虚拟头部；提供时同步作为虚拟用户根节点。</param>
    public void ApplyTrackedPose(Vector3 localPosition, Quaternion localRotation, Transform virtualHead = null)
    {
        if (userPoseTracker == null)
        {
            IsHeadsetTrackingActive = false;
            return;
        }

        LastHeadLocalPosition = localPosition;
        LastHeadLocalRotation = localRotation;
        // Raw Tracking 二维坐标使用 x 承载 X、y 承载 Unity Z；校准组件负责转换到 Room Space。
        physicalSpaceCalibration?.UpdateRawTrackingPose(
            new Vector2(localPosition.x, localPosition.z),
            HorizontalYawDegrees(localRotation));

        if (virtualHead != null)
        {
            headsetTransform = virtualHead;
            userPoseTracker.ConfigureVirtualUserRoot(virtualHead);
        }

        IsHeadsetTrackingActive = true;
    }

    /// <summary>捕获当前虚拟与物理用户位姿；追踪器缺失时返回默认值快照。</summary>
    public UserStateSnapshot CaptureSnapshot()
    {
        return UserStateSnapshot.Capture(userPoseTracker);
    }

    /// <summary>当前 physical XZ 朝向相对 +Z 的水平角，单位：度。</summary>
    public float PhysicalYawDegrees
    {
        get
        {
            var forward = userPoseTracker != null ? userPoseTracker.PhysicalForwardXZ : Vector2.up;
            return Mathf.Atan2(forward.x, forward.y) * Mathf.Rad2Deg;
        }
    }

    private void ResolveReferences()
    {
        if (userPoseTracker == null)
        {
            userPoseTracker = FindObjectOfType<UserPoseTracker>();
        }

        if (physicalSpaceCalibration == null)
        {
            physicalSpaceCalibration = FindObjectOfType<PhysicalSpaceCalibration>();
        }

        ResolveHeadsetTransform();
    }

    private void ResolveHeadsetTransform()
    {
        if (headsetTransform == null && Camera.main != null)
        {
            headsetTransform = Camera.main.transform;
        }
    }

    private static float HorizontalYawDegrees(Quaternion rotation)
    {
        var forward = rotation * Vector3.forward;
        var horizontal = new Vector2(forward.x, forward.z);
        return horizontal.sqrMagnitude > 0.0001f
            ? Mathf.Atan2(horizontal.x, horizontal.y) * Mathf.Rad2Deg
            : 0f;
    }
}
