using System;
using UnityEngine;

/// <summary>
/// 表示 Tracking Space 到 Physical Room Space 校准的运行状态。
/// </summary>
public enum PhysicalCalibrationState
{
    /// <summary>尚未建立有效校准。</summary>
    NotCalibrated,
    /// <summary>校准有效，Raw Tracking Pose 可转换为 Physical Room Pose。</summary>
    Calibrated,
    /// <summary>短暂丢失 Tracking；保留校准参数但暂停更新 Physical Pose。</summary>
    TemporarilyTrackingLost,
    /// <summary>校准可能失效，需要研究员确认或重新校准。</summary>
    CalibrationSuspect,
    /// <summary>校准已失效，正式实验不得继续。</summary>
    Invalid
}

/// <summary>
/// 建立 XR Tracking Space 到 Physical Room Space 的二维刚体变换，并将校准后的位姿写入 UserPoseTracker。
/// </summary>
public sealed class PhysicalSpaceCalibration : MonoBehaviour
{
    [Header("校准依赖")]
    [Tooltip("提供当前 Physical Space 的管理器；切换空间后现有校准会失效。")]
    [SerializeField] private PhysicalSpaceManager physicalSpaceManager;
    [Tooltip("接收校准后 Physical Room Pose 的用户位姿追踪器。")]
    [SerializeField] private UserPoseTracker userPoseTracker;

    [Header("Physical Room 校准点")]
    [Tooltip("用户执行校准时应位于的 Physical Room XZ 坐标，单位：米；Vector2.y 对应 Unity Z。")]
    [SerializeField] private Vector2 calibrationPointInRoom;
    [Tooltip("用户执行校准时应面向的 Physical Room 水平角，单位：度；0 度表示 +Z。")]
    [SerializeField] private float calibrationForwardYawDegrees;

    private PhysicalCalibrationState state = PhysicalCalibrationState.NotCalibrated;
    private Vector2 latestRawPosition;
    private float latestRawYawDegrees;
    private bool hasRawTrackingPose;
    private Vector2 rawPositionAtCalibration;
    private float trackingToRoomYawDegrees;
    private string calibratedSpaceId = string.Empty;
    private string calibrationId = string.Empty;

    /// <summary>校准状态发生变化时触发，供实验状态和研究员控制台观察。</summary>
    public event Action<PhysicalCalibrationState> StateChanged;

    /// <summary>当前校准状态。</summary>
    public PhysicalCalibrationState State => state;
    /// <summary>当前校准是否可用于正式实验和风险计算。</summary>
    public bool IsCalibrationValid => state == PhysicalCalibrationState.Calibrated;
    /// <summary>最近一次成功校准的唯一 ID；重新校准后会变化。</summary>
    public string CalibrationId => calibrationId;
    /// <summary>当前校准绑定的 Physical Space ID。</summary>
    public string CalibratedSpaceId => calibratedSpaceId;
    /// <summary>Physical Room 中固定校准点的位置，单位：米。</summary>
    public Vector2 CalibrationPointInRoom => calibrationPointInRoom;
    /// <summary>Physical Room 中固定校准朝向，单位：度。</summary>
    public float CalibrationForwardYawDegrees => calibrationForwardYawDegrees;
    /// <summary>是否已经收到至少一份有效 Raw Tracking Pose。</summary>
    public bool HasRawTrackingPose => hasRawTrackingPose;

    private void Awake()
    {
        ResolveReferences();
    }

    private void OnEnable()
    {
        BindSpaceChanged();
    }

    private void OnDisable()
    {
        UnbindSpaceChanged();
    }

    /// <summary>
    /// 注入 Physical Space 与用户位姿依赖，并迁移空间切换事件订阅。
    /// </summary>
    public void Configure(PhysicalSpaceManager spaceManager, UserPoseTracker poseTracker)
    {
        UnbindSpaceChanged();
        if (spaceManager != null) physicalSpaceManager = spaceManager;
        if (poseTracker != null) userPoseTracker = poseTracker;
        BindSpaceChanged();
    }

    /// <summary>
    /// 配置 Physical Room 中固定的校准位置和朝向。
    /// </summary>
    /// <param name="roomPosition">Physical Room XZ 位置，单位：米。</param>
    /// <param name="roomYawDegrees">以 +Z 为 0 度的水平朝向，单位：度。</param>
    public void SetCalibrationPoint(Vector2 roomPosition, float roomYawDegrees)
    {
        calibrationPointInRoom = roomPosition;
        calibrationForwardYawDegrees = NormalizeYaw(roomYawDegrees);
        if (!string.IsNullOrWhiteSpace(calibrationId))
        {
            InvalidateCalibration();
        }
    }

    /// <summary>
    /// 接收 XR Tracking Space 的原始位姿；校准有效时转换并更新 Physical Room Pose。
    /// </summary>
    /// <param name="rawPosition">Raw Tracking XZ 位置，单位：米。</param>
    /// <param name="rawYawDegrees">Raw Tracking 水平朝向，单位：度。</param>
    public void UpdateRawTrackingPose(Vector2 rawPosition, float rawYawDegrees)
    {
        latestRawPosition = rawPosition;
        latestRawYawDegrees = NormalizeYaw(rawYawDegrees);
        hasRawTrackingPose = true;

        ApplyLatestCalibratedPose();
    }

    /// <summary>
    /// 使用最近一份 Raw Tracking Pose 对齐当前 Physical Room 校准点。
    /// </summary>
    /// <returns>依赖、空间和 Raw Tracking Pose 均有效时返回 true。</returns>
    public bool CalibrateCurrentPose()
    {
        ResolveReferences();
        if (!hasRawTrackingPose || physicalSpaceManager == null || physicalSpaceManager.CurrentSpace == null || userPoseTracker == null ||
            !PhysicalSpaceGeometry.ContainsPoint(physicalSpaceManager.CurrentSpace.boundary, calibrationPointInRoom))
        {
            return false;
        }

        rawPositionAtCalibration = latestRawPosition;
        trackingToRoomYawDegrees = NormalizeYaw(calibrationForwardYawDegrees - latestRawYawDegrees);
        calibratedSpaceId = physicalSpaceManager.CurrentSpaceId;
        calibrationId = Guid.NewGuid().ToString("N");
        SetState(PhysicalCalibrationState.Calibrated);
        ApplyLatestCalibratedPose();
        return true;
    }

    /// <summary>
    /// 标记短暂 Tracking Loss；保留校准 ID 与变换，恢复采样后可继续使用。
    /// </summary>
    public void ReportTrackingLost()
    {
        if (state == PhysicalCalibrationState.Calibrated)
        {
            SetState(PhysicalCalibrationState.TemporarilyTrackingLost);
        }
    }

    /// <summary>
    /// 在 Tracking 输入已连续稳定后恢复既有校准，并应用恢复后的最新 Raw Pose。
    /// </summary>
    /// <returns>既有校准与当前 Physical Space 匹配且成功恢复时返回 true。</returns>
    public bool ReportTrackingRecovered()
    {
        if (state != PhysicalCalibrationState.TemporarilyTrackingLost ||
            !hasRawTrackingPose ||
            !HasMatchingPhysicalSpace())
        {
            return false;
        }

        SetState(PhysicalCalibrationState.Calibrated);
        ApplyLatestCalibratedPose();
        return true;
    }

    /// <summary>
    /// 标记校准可疑；状态恢复必须通过重新校准完成。
    /// </summary>
    public void MarkCalibrationSuspect()
    {
        if (!string.IsNullOrWhiteSpace(calibrationId))
        {
            SetState(PhysicalCalibrationState.CalibrationSuspect);
        }
    }

    /// <summary>
    /// 使现有校准失效，但保留最近 Raw Tracking Pose 以便立即重新校准。
    /// </summary>
    public void InvalidateCalibration()
    {
        if (state == PhysicalCalibrationState.NotCalibrated && string.IsNullOrWhiteSpace(calibrationId))
        {
            return;
        }

        SetState(PhysicalCalibrationState.Invalid);
    }

    /// <summary>
    /// 将 Raw Tracking Space 位置转换到 Physical Room Space。
    /// </summary>
    /// <param name="rawPosition">待转换 Raw Tracking XZ 位置，单位：米。</param>
    /// <returns>校准后的 Physical Room XZ 位置，单位：米。</returns>
    public Vector2 TransformPositionToRoom(Vector2 rawPosition)
    {
        return calibrationPointInRoom + Rotate(rawPosition - rawPositionAtCalibration, trackingToRoomYawDegrees);
    }

    /// <summary>
    /// 将 Raw Tracking yaw 转换到 Physical Room yaw。
    /// </summary>
    /// <param name="rawYawDegrees">Raw Tracking 水平角，单位：度。</param>
    /// <returns>Physical Room 水平角，单位：度。</returns>
    public float TransformYawToRoom(float rawYawDegrees)
    {
        return NormalizeYaw(rawYawDegrees + trackingToRoomYawDegrees);
    }

    private void ApplyLatestCalibratedPose()
    {
        if (!IsCalibrationValid || userPoseTracker == null || !HasMatchingPhysicalSpace())
        {
            return;
        }

        // Physical Risk 只读取此处写入的 Room Pose，不直接使用 XR Raw Tracking Pose。
        userPoseTracker.SetCalibratedPhysicalPose(
            TransformPositionToRoom(latestRawPosition),
            TransformYawToRoom(latestRawYawDegrees));
    }

    private bool HasMatchingPhysicalSpace()
    {
        return physicalSpaceManager != null &&
               physicalSpaceManager.CurrentSpace != null &&
               string.Equals(calibratedSpaceId, physicalSpaceManager.CurrentSpaceId, StringComparison.Ordinal);
    }

    private void HandleSpaceChanged(PhysicalSpaceConfig space)
    {
        if (space == null || string.IsNullOrWhiteSpace(calibrationId))
        {
            return;
        }

        if (!string.Equals(calibratedSpaceId, space.id, StringComparison.Ordinal))
        {
            InvalidateCalibration();
        }
    }

    private void ResolveReferences()
    {
        if (physicalSpaceManager == null) physicalSpaceManager = FindObjectOfType<PhysicalSpaceManager>();
        if (userPoseTracker == null) userPoseTracker = FindObjectOfType<UserPoseTracker>();
    }

    private void BindSpaceChanged()
    {
        if (!isActiveAndEnabled)
        {
            return;
        }

        ResolveReferences();
        if (physicalSpaceManager != null)
        {
            physicalSpaceManager.CurrentSpaceChanged -= HandleSpaceChanged;
            physicalSpaceManager.CurrentSpaceChanged += HandleSpaceChanged;
        }
    }

    private void UnbindSpaceChanged()
    {
        if (physicalSpaceManager != null)
        {
            physicalSpaceManager.CurrentSpaceChanged -= HandleSpaceChanged;
        }
    }

    private void SetState(PhysicalCalibrationState nextState)
    {
        if (state == nextState)
        {
            return;
        }

        state = nextState;
        StateChanged?.Invoke(state);
    }

    private static Vector2 Rotate(Vector2 value, float yawDegrees)
    {
        var radians = yawDegrees * Mathf.Deg2Rad;
        var sin = Mathf.Sin(radians);
        var cos = Mathf.Cos(radians);
        return new Vector2(cos * value.x + sin * value.y, -sin * value.x + cos * value.y);
    }

    private static float NormalizeYaw(float yawDegrees)
    {
        return Mathf.DeltaAngle(0f, yawDegrees);
    }
}
