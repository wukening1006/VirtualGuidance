using System;
using UnityEngine;
using UnityEngine.XR;

/// <summary>
/// 指定 Raw Tracking Pose 的唯一输入来源。
/// </summary>
public enum TrackingInputMode
{
    /// <summary>读取真实 HMD 的位置与旋转，保持正式实验的 physical walking 行为。</summary>
    PhysicalWalking,
    /// <summary>使用左手柄摇杆累计逻辑 Physical XZ，旋转与有效性仍读取真实 HMD。</summary>
    ControllerSimulation,
    /// <summary>使用显式配置的编辑器模拟位姿，不会由设备丢失自动切换。</summary>
    EditorSimulation
}

/// <summary>
/// 读取显式 Tracking 输入，并将 Raw Tracking Pose 交给 PhysicalSpaceCalibration。
/// 虚拟位姿仍由头显 Transform 提供，Physical Room Pose 必须经过独立校准后写入追踪器。
/// </summary>
public sealed class XRTrackingAdapter : MonoBehaviour
{
    [Header("Tracking 依赖")]
    [Tooltip("接收虚拟与校准后物理位姿的追踪器。")]
    [SerializeField] private UserPoseTracker userPoseTracker;
    [Tooltip("将 Raw Tracking Pose 转换为 Physical Room Pose 的校准组件。")]
    [SerializeField] private PhysicalSpaceCalibration physicalSpaceCalibration;
    [Tooltip("PhysicalWalking 使用真实 HMD 平移；ControllerSimulation 使用左摇杆模拟逻辑 Physical XZ。")]
    [SerializeField] private TrackingInputMode inputMode = TrackingInputMode.PhysicalWalking;
    [Tooltip("Tracking 从丢失恢复为有效前所需的连续有效样本数。")]
    [SerializeField, Min(2)] private int stableSamplesRequired = 2;
    [Tooltip("虚拟头部 Transform；为空时使用 Main Camera。")]
    [SerializeField] private Transform headsetTransform;

    [Header("Controller Simulation")]
    [Tooltip("左摇杆模拟 Physical XZ 平移的速度，单位：米/秒。")]
    [SerializeField, Min(0f)] private float simulatedMoveSpeedMetersPerSecond = 1.5f;

    [Header("Editor Simulation")]
    [Tooltip("编辑器模拟的 Raw Tracking 局部位置，单位：米。")]
    [SerializeField] private Vector3 editorSimulationLocalPosition = new Vector3(1f, 1.65f, 2.5f);
    [Tooltip("编辑器模拟的 Raw Tracking 局部欧拉角，单位：度。")]
    [SerializeField] private Vector3 editorSimulationLocalEulerAngles;

    private int consecutiveValidSamples;
    private bool hasControllerSimulationOrigin;
    private Vector2 controllerSimulationRawPosition;
    private Vector2 controllerSimulationVirtualTranslationDeltaXZ;

    /// <summary>Tracking 稳定有效状态发生变化时触发。</summary>
    public event Action<bool> TrackingStateChanged;
    /// <summary>一份 tracking sample 被接受后触发，参数为该样本持续时间，单位：秒。</summary>
    public event Action<float> TrackingSampleApplied;

    /// <summary>当前是否已取得足够连续样本，可用于校准与正式实验。</summary>
    public bool IsTrackingActive { get; private set; }
    /// <summary>当前显式 Tracking 输入模式。</summary>
    public TrackingInputMode InputMode => inputMode;
    /// <summary>最近一次有效头显局部位置，单位：米。</summary>
    public Vector3 LastHeadLocalPosition { get; private set; }
    /// <summary>最近一次有效头显局部旋转。</summary>
    public Quaternion LastHeadLocalRotation { get; private set; } = Quaternion.identity;
    /// <summary>当前 ControllerSimulation 样本请求的虚拟世界 XZ 平移增量，单位：米。</summary>
    public Vector2 ControllerSimulationVirtualTranslationDeltaXZ =>
        inputMode == TrackingInputMode.ControllerSimulation
            ? controllerSimulationVirtualTranslationDeltaXZ
            : Vector2.zero;

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

    /// <summary>
    /// 显式切换 Tracking 输入模式；切换时立即使当前 Tracking 状态失效。
    /// </summary>
    public void SetInputMode(TrackingInputMode mode)
    {
        if (inputMode == mode)
        {
            return;
        }

        inputMode = mode;
        ResetControllerSimulation();
        HandleTrackingUnavailable();
    }

    private void Update()
    {
        if (userPoseTracker == null)
        {
            HandleTrackingUnavailable();
            return;
        }

        if (inputMode == TrackingInputMode.EditorSimulation)
        {
            ApplyEditorSimulationPose(
                editorSimulationLocalPosition,
                Quaternion.Euler(editorSimulationLocalEulerAngles),
                headsetTransform);
            return;
        }

        var headDevice = InputDevices.GetDeviceAtXRNode(XRNode.Head);
        // 设备无效或任一位姿分量缺失时保留上一份数据，但立即使其失去安全有效性。
        if (!headDevice.isValid ||
            !headDevice.TryGetFeatureValue(CommonUsages.devicePosition, out var localPosition) ||
            !headDevice.TryGetFeatureValue(CommonUsages.deviceRotation, out var localRotation))
        {
            HandleTrackingUnavailable();
            return;
        }

        if (inputMode == TrackingInputMode.ControllerSimulation)
        {
            var leftController = InputDevices.GetDeviceAtXRNode(XRNode.LeftHand);
            var stick = leftController.isValid &&
                        leftController.TryGetFeatureValue(CommonUsages.primary2DAxis, out var axis)
                ? axis
                : Vector2.zero;
            ApplyControllerSimulationPose(
                localPosition,
                localRotation,
                stick,
                Time.unscaledDeltaTime,
                headsetTransform);
            return;
        }

        ApplyTrackedPose(localPosition, localRotation, headsetTransform);
    }

    /// <summary>
    /// 应用一份 XR 设备局部位姿；只有 PhysicalWalking 模式会接受该样本。
    /// </summary>
    /// <param name="localPosition">相对 XR Origin 的局部位置，单位：米。</param>
    /// <param name="localRotation">相对 XR Origin 的局部旋转。</param>
    /// <param name="virtualHead">可选虚拟头部；只作为 Virtual Pose Source，不会成为 Rotation Gain 目标。</param>
    /// <returns>当前样本被 PhysicalWalking 模式接受时返回 true。</returns>
    public bool ApplyTrackedPose(Vector3 localPosition, Quaternion localRotation, Transform virtualHead = null)
    {
        if (inputMode != TrackingInputMode.PhysicalWalking)
        {
            return false;
        }

        controllerSimulationVirtualTranslationDeltaXZ = Vector2.zero;

        return ApplyTrackingSample(
            localPosition,
            localRotation,
            new Vector2(localPosition.x, localPosition.z),
            virtualHead);
    }

    /// <summary>
    /// 应用真实 HMD 样本并累计左摇杆模拟的逻辑 Physical XZ；不写入任何 Camera 或 XR Root。
    /// </summary>
    /// <param name="localPosition">真实 HMD 相对 XR Origin 的局部位置，单位：米。</param>
    /// <param name="localRotation">真实 HMD 相对 XR Origin 的局部旋转。</param>
    /// <param name="moveInput">左摇杆二维输入，x 为横移、y 为前后移动。</param>
    /// <param name="deltaTimeSeconds">本次模拟累计的时长，单位：秒。</param>
    /// <param name="virtualHead">可选虚拟头部；仅作为 Virtual Pose Source。</param>
    /// <returns>当前样本被 ControllerSimulation 模式接受时返回 true。</returns>
    public bool ApplyControllerSimulationPose(
        Vector3 localPosition,
        Quaternion localRotation,
        Vector2 moveInput,
        float deltaTimeSeconds,
        Transform virtualHead = null)
    {
        if (inputMode != TrackingInputMode.ControllerSimulation)
        {
            return false;
        }

        if (!hasControllerSimulationOrigin)
        {
            controllerSimulationRawPosition = new Vector2(localPosition.x, localPosition.z);
            hasControllerSimulationOrigin = true;
        }

        controllerSimulationVirtualTranslationDeltaXZ = Vector2.zero;
        // Tracking 尚未稳定或刚从丢失恢复时不累计摇杆位移，避免无效样本推进模拟 Physical Pose。
        if (IsTrackingActive)
        {
            var rawTranslation = CalculateControllerTranslation(
                moveInput,
                localRotation,
                simulatedMoveSpeedMetersPerSecond,
                deltaTimeSeconds);
            controllerSimulationRawPosition += rawTranslation;
            controllerSimulationVirtualTranslationDeltaXZ = ToVirtualWorldTranslation(rawTranslation);
        }

        return ApplyTrackingSample(
            localPosition,
            localRotation,
            controllerSimulationRawPosition,
            virtualHead);
    }

    /// <summary>
    /// 应用一份显式编辑器模拟位姿；只有 EditorSimulation 模式会接受该样本。
    /// </summary>
    /// <param name="localPosition">模拟的 Tracking 局部位置，单位：米。</param>
    /// <param name="localRotation">模拟的 Tracking 局部旋转。</param>
    /// <param name="virtualHead">可选虚拟头部 Transform。</param>
    /// <returns>当前样本被 EditorSimulation 模式接受时返回 true。</returns>
    public bool ApplyEditorSimulationPose(Vector3 localPosition, Quaternion localRotation, Transform virtualHead = null)
    {
        if (inputMode != TrackingInputMode.EditorSimulation)
        {
            return false;
        }

        controllerSimulationVirtualTranslationDeltaXZ = Vector2.zero;

        return ApplyTrackingSample(
            localPosition,
            localRotation,
            new Vector2(localPosition.x, localPosition.z),
            virtualHead);
    }

    /// <summary>显式报告当前 Tracking 样本不可用，立即进入失效状态。</summary>
    public void ReportTrackingUnavailable()
    {
        HandleTrackingUnavailable();
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

    private bool ApplyTrackingSample(
        Vector3 localPosition,
        Quaternion localRotation,
        Vector2 rawPhysicalPosition,
        Transform virtualHead)
    {
        if (userPoseTracker == null)
        {
            HandleTrackingUnavailable();
            return false;
        }

        LastHeadLocalPosition = localPosition;
        LastHeadLocalRotation = localRotation;
        // Raw Tracking 二维坐标使用 x 承载 X、y 承载 Unity Z；校准组件负责转换到 Room Space。
        physicalSpaceCalibration?.UpdateRawTrackingPose(
            rawPhysicalPosition,
            HorizontalYawDegrees(localRotation));

        if (virtualHead != null)
        {
            headsetTransform = virtualHead;
            userPoseTracker.ConfigureVirtualPoseSource(virtualHead);
        }

        consecutiveValidSamples = Mathf.Min(consecutiveValidSamples + 1, Mathf.Max(2, stableSamplesRequired));
        if (consecutiveValidSamples >= Mathf.Max(2, stableSamplesRequired))
        {
            SetTrackingActive(true);
            physicalSpaceCalibration?.ReportTrackingRecovered();
        }

        TrackingSampleApplied?.Invoke(Time.unscaledDeltaTime);

        return true;
    }

    private void HandleTrackingUnavailable()
    {
        controllerSimulationVirtualTranslationDeltaXZ = Vector2.zero;
        consecutiveValidSamples = 0;
        SetTrackingActive(false);
        physicalSpaceCalibration?.ReportTrackingLost();
    }

    private void ResetControllerSimulation()
    {
        hasControllerSimulationOrigin = false;
        controllerSimulationRawPosition = Vector2.zero;
        controllerSimulationVirtualTranslationDeltaXZ = Vector2.zero;
    }

    private void SetTrackingActive(bool active)
    {
        if (IsTrackingActive == active)
        {
            return;
        }

        IsTrackingActive = active;
        TrackingStateChanged?.Invoke(active);
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

    private static Vector2 CalculateControllerTranslation(
        Vector2 moveInput,
        Quaternion headRotation,
        float speedMetersPerSecond,
        float deltaTimeSeconds)
    {
        moveInput = Vector2.ClampMagnitude(moveInput, 1f);
        var yawRadians = HorizontalYawDegrees(headRotation) * Mathf.Deg2Rad;
        var forward = new Vector2(Mathf.Sin(yawRadians), Mathf.Cos(yawRadians));
        var right = new Vector2(forward.y, -forward.x);
        return (right * moveInput.x + forward * moveInput.y) *
               Mathf.Max(0f, speedMetersPerSecond) *
               Mathf.Max(0f, deltaTimeSeconds);
    }

    private Vector2 ToVirtualWorldTranslation(Vector2 rawTranslation)
    {
        var rotationRoot = userPoseTracker != null ? userPoseTracker.VirtualRotationRoot : null;
        if (rotationRoot == null)
        {
            return Vector2.zero;
        }

        // 将 tracking-space 位移映射到当前合法 RDW Root 的世界朝向，保留已注入的虚拟 yaw。
        var worldTranslation = rotationRoot.TransformDirection(
            new Vector3(rawTranslation.x, 0f, rawTranslation.y));
        return new Vector2(worldTranslation.x, worldTranslation.z);
    }
}

/// <summary>将 ControllerSimulation 的虚拟平移接入现有 ContinuousRedirector owner。</summary>
public sealed class ControllerSimulationLocomotionRedirector : IRdwRedirector
{
    private readonly XRTrackingAdapter trackingAdapter;

    /// <summary>创建只读取现有 Tracking Adapter 样本的虚拟移动桥接器。</summary>
    public ControllerSimulationLocomotionRedirector(XRTrackingAdapter adapter)
    {
        trackingAdapter = adapter;
    }

    /// <summary>为当前 RDW 帧生成 ControllerSimulation 虚拟平移命令。</summary>
    public RdwGainCommand Evaluate(
        in RdwPoseFrame pose,
        in RdwGeometrySnapshot geometry,
        in RdwNavigationIntent intent)
    {
        var translation = trackingAdapter != null
            ? trackingAdapter.ControllerSimulationVirtualTranslationDeltaXZ
            : Vector2.zero;
        return translation.sqrMagnitude > 0.000001f
            ? new RdwGainCommand(
                0f,
                0f,
                translation,
                RdwGainOwner.ContinuousRedirector,
                "controller_simulation_locomotion",
                pose.Sequence)
            : RdwGainCommand.Zero(pose.Sequence, "controller_simulation_idle");
    }
}
