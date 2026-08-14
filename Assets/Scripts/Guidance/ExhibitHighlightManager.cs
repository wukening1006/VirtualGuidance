using System.Collections.Generic;
using UnityEngine;
using UnityFx.Outline;

/// <summary>
/// 根据 selected_target 对展品做轻量目标确认高亮，不表达路线安全性。
/// 使用 UnityFx.Outline 生成黄色外轮廓，保留展品原材质。
/// </summary>
public sealed class ExhibitHighlightManager : MonoBehaviour
{
    [Header("Outline 配置")]
    [Tooltip("Main Camera 上的 OutlineEffect 组件引用。")]
    [SerializeField] private OutlineEffect outlineEffect;

    [Header("高亮样式")]
    [Tooltip("高亮颜色（推荐黄色：1.0, 0.86, 0.25）。")]
    [SerializeField] private Color highlightColor = new Color(1f, 0.86f, 0.25f, 1f);

    [Tooltip("Outline 宽度（像素）。")]
    [SerializeField] private int outlineWidth = 4;

    [Tooltip("Outline 强度。")]
    [SerializeField] private float outlineIntensity = 1.5f;

    [Tooltip("是否在启动时自动收集场景中的 ExhibitMetadata。")]
    [SerializeField] private bool collectOnAwake = true;

    private readonly Dictionary<string, ExhibitMetadata> exhibitsById = new Dictionary<string, ExhibitMetadata>();
    private OutlineLayer guidanceTargetLayer;
    private OutlineLayerCollection runtimeOutlineLayers;
    private GameObject activeTarget;
    private string activeTargetId;

    private void Awake()
    {
        if (collectOnAwake)
        {
            RebuildIndex();
        }

        InitializeOutlineLayer();
    }

    private void InitializeOutlineLayer()
    {
        if (outlineEffect == null)
        {
            Debug.LogError("[ExhibitHighlightManager] OutlineEffect 未配置，请在 Inspector 中分配 Main Camera 上的 OutlineEffect 组件。", this);
            return;
        }

        var outlineLayers = outlineEffect.OutlineLayers;
        if (outlineLayers == null)
        {
            // Inspector 未配置集合时创建运行时实例，保证高亮功能仍可使用。
            runtimeOutlineLayers = ScriptableObject.CreateInstance<OutlineLayerCollection>();
            runtimeOutlineLayers.name = "GuidanceRuntimeOutlineLayers";
            runtimeOutlineLayers.hideFlags = HideFlags.DontSave;
            outlineEffect.OutlineLayers = runtimeOutlineLayers;
            outlineLayers = runtimeOutlineLayers;

            Debug.LogWarning(
                "[ExhibitHighlightManager] OutlineLayerCollection 未配置，已创建运行时集合。建议在 Main Camera 的 OutlineEffect 中分配持久化集合资源。",
                outlineEffect);
        }

        // 创建或获取 Guidance Target Layer
        guidanceTargetLayer = new OutlineLayer("GuidanceTarget")
        {
            OutlineColor = highlightColor,
            OutlineWidth = outlineWidth,
            OutlineIntensity = outlineIntensity,
            OutlineRenderMode = OutlineRenderFlags.Blurred | OutlineRenderFlags.EnableDepthTesting
        };

        outlineLayers.Add(guidanceTargetLayer);
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
        if (string.IsNullOrWhiteSpace(selectedTarget))
        {
            Clear();
            return;
        }

        // 避免重复高亮同一目标
        if (activeTargetId == selectedTarget && activeTarget != null)
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
            Clear();
            return;
        }

        if (guidanceTargetLayer == null || outlineEffect == null)
        {
            Debug.LogError("[ExhibitHighlightManager] OutlineLayer 未初始化，无法高亮。", this);
            return;
        }

        // 清除旧目标
        Clear();

        // 添加新目标到 Outline Layer
        guidanceTargetLayer.Add(exhibit.gameObject);
        activeTarget = exhibit.gameObject;
        activeTargetId = selectedTarget;
    }

    /// <summary>
    /// 清除当前目标高亮，rejected guidance 必须调用。
    /// </summary>
    public void Clear()
    {
        if (activeTarget != null && guidanceTargetLayer != null)
        {
            guidanceTargetLayer.Remove(activeTarget);
            activeTarget = null;
            activeTargetId = null;
        }
    }

    private void OnDestroy()
    {
        // 清理 Outline Layer
        if (outlineEffect != null && outlineEffect.OutlineLayers != null && guidanceTargetLayer != null)
        {
            outlineEffect.OutlineLayers.Remove(guidanceTargetLayer);
        }

        if (runtimeOutlineLayers == null)
        {
            return;
        }

        if (outlineEffect != null && outlineEffect.OutlineLayers == runtimeOutlineLayers)
        {
            outlineEffect.OutlineLayers = null;
        }

        Destroy(runtimeOutlineLayers);
        runtimeOutlineLayers = null;
    }
}
