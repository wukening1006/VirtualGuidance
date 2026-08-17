using UnityEngine;

/// <summary>
/// MainScene 的展品场景绑定锚点。
/// 不承载 name、description、tags、related_exhibits 或任何静态语义。
/// </summary>
public sealed class ExhibitMetadata : MonoBehaviour
{
    [Header("展品绑定")]
    [Tooltip("展品 ID，例如 E1、E2；必须与 scene_semantics.json 一一对应。")]
    [SerializeField] private string id;
    [Tooltip("参观者观看该展品时使用的虚拟 ViewPoint。")]
    [SerializeField] private Transform viewPoint;
    [Tooltip("指向该展品的 GazeTarget。")]
    [SerializeField] private Transform gazeTarget;

    /// <summary>展品 ID。</summary>
    public string Id => id;
    /// <summary>实际场景中的观看位置。</summary>
    public Transform ViewPoint => viewPoint;
    /// <summary>实际场景中的注视目标。</summary>
    public Transform GazeTarget => gazeTarget;

    /// <summary>返回该 Anchor 的当前世界层级路径。</summary>
    public string HierarchyPath
    {
        get
        {
            var parts = new System.Collections.Generic.List<string>();
            var current = transform;
            while (current != null)
            {
                parts.Add(current.name);
                current = current.parent;
            }

            parts.Reverse();
            return string.Join("/", parts.ToArray());
        }
    }
}
