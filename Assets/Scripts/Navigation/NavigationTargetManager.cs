using System;
using System.Collections.Generic;
using UnityEngine;

public sealed class NavigationTargetManager : MonoBehaviour
{
    [SerializeField] private bool rebuildOnAwake = true;

    private readonly Dictionary<string, ExhibitMetadata> targetsById = new Dictionary<string, ExhibitMetadata>();

    private void Awake()
    {
        if (rebuildOnAwake)
        {
            RebuildIndex();
        }
    }

    public void RebuildIndex()
    {
        targetsById.Clear();
        foreach (var exhibit in FindObjectsOfType<ExhibitMetadata>())
        {
            if (exhibit != null && !string.IsNullOrWhiteSpace(exhibit.Id))
            {
                targetsById[exhibit.Id] = exhibit;
            }
        }
    }

    public bool HasTarget(string targetId)
    {
        EnsureIndex();
        return !string.IsNullOrWhiteSpace(targetId) && targetsById.ContainsKey(targetId);
    }

    public bool TryGetTarget(string targetId, out ExhibitMetadata target)
    {
        EnsureIndex();
        return targetsById.TryGetValue(targetId, out target);
    }

    public string[] GetRelatedTargetIds(string targetId)
    {
        return TryGetTarget(targetId, out var target) && target.RelatedExhibits != null
            ? target.RelatedExhibits
            : Array.Empty<string>();
    }

    public string GetTargetDirection(string targetId)
    {
        if (!TryGetTarget(targetId, out var target) || target == null)
        {
            return "forward";
        }

        var anchor = target.ViewPoint != null ? target.ViewPoint : target.transform;
        if (anchor.position.x < -0.5f)
        {
            return "left";
        }

        if (anchor.position.x > 0.5f)
        {
            return "right";
        }

        return "forward";
    }

    private void EnsureIndex()
    {
        if (targetsById.Count == 0)
        {
            RebuildIndex();
        }
    }
}
