using System;
using UnityEngine;

/// <summary>
/// 监测用户虚拟位置与当前展品观看点的距离，并将单次到达结果提供给任务控制器。
/// 用户位姿来自 UserPoseTracker，目标位置来自 NavigationTargetManager。
/// </summary>
public sealed class ArrivalDetector : MonoBehaviour
{
    [SerializeField] private UserPoseTracker userPoseTracker;
    [SerializeField] private NavigationTargetManager navigationTargets;
    [SerializeField] private float arrivalDistanceMeters = 0.75f;

    private string watchedTargetId;

    /// <summary>
    /// 用户首次进入当前目标到达半径时触发一次；参数为到达的展品 ID。
    /// WatchTarget 再次设置目标后，后续到达可再次触发。
    /// </summary>
    public event Action<string> TargetArrived;

    private void Awake()
    {
        ResolveReferences();
    }

    private void Update()
    {
        if (string.IsNullOrWhiteSpace(watchedTargetId) || userPoseTracker == null || navigationTargets == null)
        {
            return;
        }

        if (!navigationTargets.TryGetTarget(watchedTargetId, out var target) || target == null)
        {
            return;
        }

        var anchor = target.ViewPoint != null ? target.ViewPoint : target.transform;
        // 到达判定使用虚拟世界坐标，阈值单位为米，并保留 0.05 米的最小有效半径。
        var distance = Vector3.Distance(userPoseTracker.UserVirtualPosition, anchor.position);
        if (distance <= Mathf.Max(0.05f, arrivalDistanceMeters))
        {
            var arrived = watchedTargetId;
            // 触发前清空监测目标，避免用户停留在半径内时每帧重复推进任务。
            watchedTargetId = string.Empty;
            TargetArrived?.Invoke(arrived);
        }
    }

    /// <summary>注入虚拟用户位姿与展品目标索引。</summary>
    public void Configure(UserPoseTracker poseTracker, NavigationTargetManager targetManager)
    {
        userPoseTracker = poseTracker;
        navigationTargets = targetManager;
    }

    /// <summary>
    /// 开始监测指定展品；空 ID 会取消当前监测，新的 ID 会覆盖旧目标。
    /// </summary>
    public void WatchTarget(string targetId)
    {
        watchedTargetId = targetId;
    }

    private void ResolveReferences()
    {
        if (userPoseTracker == null)
        {
            userPoseTracker = FindObjectOfType<UserPoseTracker>();
        }

        if (navigationTargets == null)
        {
            navigationTargets = FindObjectOfType<NavigationTargetManager>();
        }
    }
}
