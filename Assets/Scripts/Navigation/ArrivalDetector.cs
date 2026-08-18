using System;
using UnityEngine;

/// <summary>
/// 监测用户虚拟位置与当前展品实体边界的水平距离，并将单次到达结果提供给任务控制器。
/// 用户位姿来自 UserPoseTracker，目标实体来自 NavigationTargetManager。
/// </summary>
public sealed class ArrivalDetector : MonoBehaviour
{
    [SerializeField] private UserPoseTracker userPoseTracker;
    [SerializeField] private NavigationTargetManager navigationTargets;
    [SerializeField] private float arrivalDistanceMeters = 0.75f;

    private TargetReservation watchedReservation;
    private ExhibitMetadata watchedTarget;
    private Collider[] watchedTargetColliders = Array.Empty<Collider>();

    /// <summary>
    /// 用户首次进入当前目标实体边界外扩的到达半径时触发一次。
    /// WatchReservation 再次设置目标后，后续到达可再次触发。
    /// </summary>
    public event Action<TargetReservation> TargetArrived;

    private void Awake()
    {
        ResolveReferences();
    }

    private void Update()
    {
        if (watchedReservation == null ||
            watchedReservation.TargetRole == NavigationTargetRole.TurnCueAnchor ||
            userPoseTracker == null ||
            navigationTargets == null)
        {
            return;
        }

        if (!navigationTargets.TryGetTarget(watchedReservation.TargetId, out var target) || target == null)
        {
            return;
        }

        if (watchedTarget != target)
        {
            CacheTargetColliders(target);
        }

        // Room-scale 到达只比较水平面；HMD/Camera 高度不应阻塞展品到达。
        var userPosition = userPoseTracker.UserVirtualPosition;
        var distanceSquared = CalculateHorizontalDistanceSquared(target, userPosition);
        var arrivalRadius = Mathf.Max(0.05f, arrivalDistanceMeters);
        if (distanceSquared <= arrivalRadius * arrivalRadius)
        {
            var arrived = watchedReservation;
            // 触发前清空监测目标，避免用户停留在半径内时每帧重复推进任务。
            watchedReservation = null;
            TargetArrived?.Invoke(arrived);
        }
    }

    /// <summary>注入虚拟用户位姿与展品目标索引。</summary>
    public void Configure(UserPoseTracker poseTracker, NavigationTargetManager targetManager)
    {
        if (poseTracker != null) userPoseTracker = poseTracker;
        if (targetManager != null) navigationTargets = targetManager;
    }

    /// <summary>
    /// 开始监测指定目标预留；null 会取消当前监测，新的 token 会覆盖旧预留。
    /// </summary>
    public void WatchReservation(TargetReservation reservation)
    {
        watchedReservation = reservation;
        if (reservation == null)
        {
            ClearTargetCache();
        }
    }

    private void CacheTargetColliders(ExhibitMetadata target)
    {
        watchedTarget = target;
        watchedTargetColliders = target != null
            ? target.GetComponentsInChildren<Collider>(true)
            : Array.Empty<Collider>();
    }

    private void ClearTargetCache()
    {
        watchedTarget = null;
        watchedTargetColliders = Array.Empty<Collider>();
    }

    private float CalculateHorizontalDistanceSquared(ExhibitMetadata target, Vector3 userPosition)
    {
        var hasValidCollider = false;
        var closestDistanceSquared = float.PositiveInfinity;
        var viewPoint = target.ViewPoint;

        foreach (var collider in watchedTargetColliders)
        {
            if (collider == null ||
                !collider.enabled ||
                !collider.gameObject.activeInHierarchy ||
                (viewPoint != null &&
                 (collider.transform == viewPoint || collider.transform.IsChildOf(viewPoint))))
            {
                continue;
            }

            hasValidCollider = true;
            var bounds = collider.bounds;
            var deltaX = Mathf.Max(Mathf.Max(bounds.min.x - userPosition.x, 0f), userPosition.x - bounds.max.x);
            var deltaZ = Mathf.Max(Mathf.Max(bounds.min.z - userPosition.z, 0f), userPosition.z - bounds.max.z);
            var distanceSquared = deltaX * deltaX + deltaZ * deltaZ;
            if (distanceSquared < closestDistanceSquared)
            {
                closestDistanceSquared = distanceSquared;
            }
        }

        if (hasValidCollider)
        {
            return closestDistanceSquared;
        }

        var targetPosition = target.transform.position;
        var fallbackDeltaX = userPosition.x - targetPosition.x;
        var fallbackDeltaZ = userPosition.z - targetPosition.z;
        return fallbackDeltaX * fallbackDeltaX + fallbackDeltaZ * fallbackDeltaZ;
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
