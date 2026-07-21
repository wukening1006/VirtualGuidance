using System;
using UnityEngine;

/// <summary>
/// 从连续 physical XZ 朝向采样估算水平角速度和转向状态，供旋转增益控制器使用。
/// 使用不同的开始与停止阈值形成迟滞，避免阈值附近逐帧抖动。
/// </summary>
public sealed class MotionDetector : MonoBehaviour
{
    [SerializeField] private UserPoseTracker userPoseTracker;
    [SerializeField] private float turnStartDegreesPerSecond = 12f;
    [SerializeField] private float turnStopDegreesPerSecond = 4f;

    private bool hasPreviousForward;
    private Vector2 previousForward;

    /// <summary>每次 Physical Forward 采样完成后触发，参数依次为转向状态、有符号 yaw delta 和采样时长。</summary>
    public event Action<bool, float, float> TurnSampled;

    /// <summary>当前角速度是否处于转向状态，采用开始/停止双阈值判断。</summary>
    public bool IsTurning { get; private set; }
    /// <summary>最近两次采样间的有符号水平转角，正值为左转，负值为右转，单位：度。</summary>
    public float LastSignedYawDeltaDegrees { get; private set; }
    /// <summary>最近一次采样计算的水平角速度绝对值，单位：度/秒。</summary>
    public float AngularSpeedDegreesPerSecond { get; private set; }

    private void Awake()
    {
        ResolveReferences();
    }

    private void Update()
    {
        if (userPoseTracker == null)
        {
            return;
        }

        Sample(userPoseTracker.PhysicalForwardXZ, Time.deltaTime);
    }

    /// <summary>
    /// 输入一次 physical XZ 朝向采样并更新转角、角速度和转向状态。
    /// </summary>
    /// <param name="forward">二维 physical 朝向，其中 x 对应 Unity X、y 对应 Unity Z。</param>
    /// <param name="deltaTimeSeconds">距上一采样的时间，单位：秒。</param>
    public void Sample(Vector2 forward, float deltaTimeSeconds)
    {
        forward = forward.sqrMagnitude > 0.0001f ? forward.normalized : Vector2.up;
        if (!hasPreviousForward)
        {
            previousForward = forward;
            hasPreviousForward = true;
            TurnSampled?.Invoke(false, 0f, Mathf.Max(0f, deltaTimeSeconds));
            return;
        }

        LastSignedYawDeltaDegrees = Vector2.SignedAngle(previousForward, forward);
        AngularSpeedDegreesPerSecond = Mathf.Abs(LastSignedYawDeltaDegrees) / Mathf.Max(deltaTimeSeconds, 0.0001f);
        previousForward = forward;

        // 双阈值迟滞避免角速度接近边界时频繁切换 IsTurning。
        if (!IsTurning && AngularSpeedDegreesPerSecond >= turnStartDegreesPerSecond)
        {
            IsTurning = true;
        }
        else if (IsTurning && AngularSpeedDegreesPerSecond <= turnStopDegreesPerSecond)
        {
            IsTurning = false;
        }

        TurnSampled?.Invoke(IsTurning, LastSignedYawDeltaDegrees, Mathf.Max(0f, deltaTimeSeconds));
    }

    /// <summary>
    /// 替换 physical 位姿来源，并清除上一来源留下的采样与转向状态。
    /// </summary>
    public void Configure(UserPoseTracker poseTracker)
    {
        if (poseTracker != null)
        {
            userPoseTracker = poseTracker;
        }
        hasPreviousForward = false;
        IsTurning = false;
        LastSignedYawDeltaDegrees = 0f;
        AngularSpeedDegreesPerSecond = 0f;
    }

    private void ResolveReferences()
    {
        if (userPoseTracker == null)
        {
            userPoseTracker = FindObjectOfType<UserPoseTracker>();
        }
    }
}
