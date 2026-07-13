using System;
using UnityEngine;

public sealed class ArrivalDetector : MonoBehaviour
{
    [SerializeField] private UserPoseTracker userPoseTracker;
    [SerializeField] private NavigationTargetManager navigationTargets;
    [SerializeField] private float arrivalDistanceMeters = 0.75f;

    private string watchedTargetId;

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
        var distance = Vector3.Distance(userPoseTracker.UserVirtualPosition, anchor.position);
        if (distance <= Mathf.Max(0.05f, arrivalDistanceMeters))
        {
            var arrived = watchedTargetId;
            watchedTargetId = string.Empty;
            TargetArrived?.Invoke(arrived);
        }
    }

    public void Configure(UserPoseTracker poseTracker, NavigationTargetManager targetManager)
    {
        userPoseTracker = poseTracker;
        navigationTargets = targetManager;
    }

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
