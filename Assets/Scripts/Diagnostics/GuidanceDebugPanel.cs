using System.Text;
using TMPro;
using UnityEngine;

/// <summary>
/// 显示 guidance response 的关键字段，作为研究者调试视图。
/// </summary>
public sealed class GuidanceDebugPanel : MonoBehaviour
{
    [Header("调试 UI")]
    [Tooltip("显示 guidance 调试信息的 TextMeshPro 文本控件；未配置时自动查找 GuidanceDebugPanel_Text。")]
    [SerializeField] private TMP_Text debugText;

    private readonly StringBuilder builder = new StringBuilder(512);

    /// <summary>
    /// 注入调试文本控件，支持 bootstrap 自动接线。
    /// </summary>
    public void Configure(TMP_Text text)
    {
        debugText = text;
        ShowStatus("Guidance debug: waiting");
    }

    /// <summary>
    /// 显示请求状态或错误信息。
    /// </summary>
    public void ShowStatus(string status)
    {
        ResolveReferences();
        if (debugText != null)
        {
            debugText.text = status;
        }
    }

    /// <summary>
    /// 显示后端 guidance response 的关键执行字段。
    /// </summary>
    public void Show(GuidanceResponse response)
    {
        ResolveReferences();
        if (debugText == null)
        {
            return;
        }

        if (response == null)
        {
            debugText.text = "Guidance response: null";
            return;
        }

        builder.Clear();
        builder.AppendLine("Guidance Debug");
        builder.Append("Approved: ").AppendLine(GuidanceTypeUtility.ApprovedText(response));
        builder.Append("Target: ").AppendLine(Empty(response.selected_target));
        builder.Append("Route: ").AppendLine(Empty(response.route_decision));
        builder.Append("Intent: ").AppendLine(Empty(response.physical_intent));
        builder.Append("Gaze: ").AppendLine(Empty(response.gaze_cue));
        builder.Append("Avatar: ").AppendLine(Empty(response.avatar_action));
        builder.Append("Movement: ").AppendLine(Empty(response.movement_instruction));
        builder.Append("Reason: ").AppendLine(response.verifier != null ? Empty(response.verifier.reason) : "missing verifier");
        debugText.text = builder.ToString();
    }

    private void ResolveReferences()
    {
        if (debugText != null)
        {
            return;
        }

        var textObject = GameObject.Find("GuidanceDebugPanel_Text");
        if (textObject == null)
        {
            textObject = GameObject.Find("ResearcherDebugPanel_Text");
        }

        if (textObject != null)
        {
            debugText = textObject.GetComponent<TMP_Text>();
        }
    }

    private static string Empty(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? "none" : value;
    }
}
