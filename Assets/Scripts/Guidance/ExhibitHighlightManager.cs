using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 根据 selected_target 对展品做轻量目标确认高亮，不表达路线安全性。
/// </summary>
public sealed class ExhibitHighlightManager : MonoBehaviour
{
    [Header("高亮样式")]
    [Tooltip("高亮颜色，用于覆盖目标展品 renderer 的材质颜色。")]
    [SerializeField] private Color highlightColor = new Color(1f, 0.86f, 0.25f, 1f);
    [Tooltip("是否在启动时自动收集场景中的 ExhibitMetadata。")]
    [SerializeField] private bool collectOnAwake = true;

    private readonly Dictionary<string, ExhibitMetadata> exhibitsById = new Dictionary<string, ExhibitMetadata>();
    private readonly Dictionary<Renderer, Material[]> originalMaterials = new Dictionary<Renderer, Material[]>();
    private readonly List<Renderer> highlightedRenderers = new List<Renderer>();
    private Material highlightMaterial;

    private void Awake()
    {
        if (collectOnAwake)
        {
            RebuildIndex();
        }
    }

    /// <summary>
    /// 重新扫描场景中的展品元数据，场景优化或动态生成展品后可调用。
    /// </summary>
    public void RebuildIndex()
    {
        exhibitsById.Clear();
        var exhibits = FindObjectsOfType<ExhibitMetadata>();
        foreach (var exhibit in exhibits)
        {
            if (exhibit == null || string.IsNullOrWhiteSpace(exhibit.Id))
            {
                continue;
            }

            exhibitsById[exhibit.Id] = exhibit;
        }
    }

    /// <summary>
    /// 高亮 selected_target 对应展品；找不到目标时只输出 warning，不中断导览。
    /// </summary>
    public void Highlight(string selectedTarget)
    {
        Clear();
        if (string.IsNullOrWhiteSpace(selectedTarget))
        {
            return;
        }

        if (exhibitsById.Count == 0)
        {
            RebuildIndex();
        }

        if (!exhibitsById.TryGetValue(selectedTarget, out var exhibit) || exhibit == null)
        {
            Debug.LogWarning($"[ExhibitHighlightManager] 未找到目标展品 | selected_target={selectedTarget}", this);
            return;
        }

        EnsureHighlightMaterial();
        var renderers = exhibit.GetComponentsInChildren<MeshRenderer>(true);
        foreach (var renderer in renderers)
        {
            if (renderer == null)
            {
                continue;
            }

            if (!originalMaterials.ContainsKey(renderer))
            {
                originalMaterials[renderer] = renderer.sharedMaterials;
            }

            var materials = renderer.sharedMaterials;
            for (var i = 0; i < materials.Length; i++)
            {
                materials[i] = highlightMaterial;
            }

            renderer.sharedMaterials = materials;
            highlightedRenderers.Add(renderer);
        }
    }

    /// <summary>
    /// 清除当前目标高亮，rejected guidance 必须调用。
    /// </summary>
    public void Clear()
    {
        foreach (var renderer in highlightedRenderers)
        {
            if (renderer == null)
            {
                continue;
            }

            if (originalMaterials.TryGetValue(renderer, out var materials))
            {
                renderer.sharedMaterials = materials;
            }
        }

        highlightedRenderers.Clear();
    }

    private void EnsureHighlightMaterial()
    {
        if (highlightMaterial != null)
        {
            return;
        }

        var shader = Shader.Find("Standard");
        highlightMaterial = new Material(shader);
        highlightMaterial.color = highlightColor;
        if (highlightMaterial.HasProperty("_EmissionColor"))
        {
            highlightMaterial.EnableKeyword("_EMISSION");
            highlightMaterial.SetColor("_EmissionColor", highlightColor * 0.35f);
        }
    }
}
