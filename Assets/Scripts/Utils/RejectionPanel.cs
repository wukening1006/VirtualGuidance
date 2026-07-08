using TMPro;
using UnityEngine;

/// <summary>
/// 中文说明：显示 verifier 拒绝原因或请求错误，确保 rejected guidance 在场景内可见。
/// </summary>
public sealed class RejectionPanel : MonoBehaviour
{
    [Header("拒绝信息 UI")]
    [Tooltip("显示 rejection reason 的 TextMeshPro 文本控件；未配置时自动查找 RejectionPanel_Text。")]
    [SerializeField] private TMP_Text rejectionText;
    [Tooltip("可选面板根节点，用于统一显示/隐藏拒绝信息。")]
    [SerializeField] private GameObject panelRoot;

    /// <summary>
    /// 中文说明：注入拒绝面板引用，支持 bootstrap 自动接线。
    /// </summary>
    public void Configure(TMP_Text text, GameObject root)
    {
        rejectionText = text;
        panelRoot = root;
        Hide();
    }

    /// <summary>
    /// 中文说明：显示 verifier 或网络请求的失败原因。
    /// </summary>
    public void Show(string reason)
    {
        ResolveReferences();
        if (panelRoot != null)
        {
            panelRoot.SetActive(true);
        }

        if (rejectionText != null)
        {
            rejectionText.text = $"Guidance rejected:\n{(string.IsNullOrWhiteSpace(reason) ? "Unknown reason." : reason)}";
        }
    }

    /// <summary>
    /// 中文说明：隐藏拒绝原因面板。
    /// </summary>
    public void Hide()
    {
        ResolveReferences();
        if (rejectionText != null)
        {
            rejectionText.text = string.Empty;
        }

        if (panelRoot != null)
        {
            panelRoot.SetActive(false);
        }
    }

    private void ResolveReferences()
    {
        if (rejectionText == null)
        {
            var textObject = GameObject.Find("RejectionPanel_Text");
            if (textObject != null)
            {
                rejectionText = textObject.GetComponent<TMP_Text>();
            }
        }

        if (panelRoot == null && rejectionText != null)
        {
            panelRoot = rejectionText.transform.parent != null ? rejectionText.transform.parent.gameObject : rejectionText.gameObject;
        }
    }
}
