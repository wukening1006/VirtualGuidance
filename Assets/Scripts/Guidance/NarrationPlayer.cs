using TMPro;
using UnityEngine;

/// <summary>
/// 显示 avatar/guide 的导览话术；MVP 阶段以字幕面板表达，不直接处理 TTS 或口型。
/// </summary>
public sealed class NarrationPlayer : MonoBehaviour
{
    [Header("话术 UI")]
    [Tooltip("显示后端 narration 的 TextMeshPro 文本控件；未配置时自动查找 GuidanceTextPanel_Text。")]
    [SerializeField] private TMP_Text narrationText;
    [Tooltip("没有话术时显示的占位文本。")]
    [SerializeField] private string idleText = string.Empty;

    private void Awake()
    {
        ResolveReferences();
        if (narrationText != null && string.IsNullOrWhiteSpace(narrationText.text))
        {
            narrationText.text = idleText;
        }
    }

    /// <summary>
    /// 注入字幕文本控件，支持 bootstrap 自动接线。
    /// </summary>
    public void Configure(TMP_Text text)
    {
        narrationText = text;
        Clear();
    }

    /// <summary>
    /// 显示后端生成的自然语言导览话术。
    /// </summary>
    public void Play(string narration)
    {
        ResolveReferences();
        if (narrationText == null)
        {
            return;
        }

        narrationText.text = string.IsNullOrWhiteSpace(narration) ? idleText : narration;
    }

    /// <summary>
    /// 清除当前导览话术，通常在 rejected guidance 时调用。
    /// </summary>
    public void Clear()
    {
        ResolveReferences();
        if (narrationText != null)
        {
            narrationText.text = idleText;
        }
    }

    private void ResolveReferences()
    {
        if (narrationText != null)
        {
            return;
        }

        var textObject = GameObject.Find("GuidanceTextPanel_Text");
        if (textObject != null)
        {
            narrationText = textObject.GetComponent<TMP_Text>();
        }
    }
}
