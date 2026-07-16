using TMPro;
using UnityEngine;

/// <summary>
/// 弱化的视线提示控制器，主要用于短暂方向提示或 debug 展示，避免和展品高亮重复。
/// </summary>
public sealed class GazeCueController : MonoBehaviour
{
    [Header("弱提示 UI")]
    [Tooltip("可选文本控件，用于显示 gaze cue；为空时只记录当前 cue。")]
    [SerializeField] private TMP_Text gazeCueText;
    [Tooltip("gaze cue 在 UI 中保留的时间，单位：秒；0 表示持续到下一次 Clear。")]
    [SerializeField] private float cueVisibleSeconds = 1.5f;

    private float hideTime;
    private string currentCue;

    private void Update()
    {
        if (gazeCueText == null || cueVisibleSeconds <= 0f || string.IsNullOrEmpty(currentCue))
        {
            return;
        }

        if (Time.unscaledTime >= hideTime)
        {
            Clear();
        }
    }

    /// <summary>
    /// 注入可选的 gaze cue 文本控件。
    /// </summary>
    public void Configure(TMP_Text text)
    {
        gazeCueText = text;
    }

    /// <summary>
    /// 显示弱视线提示；MVP 不生成强箭头，避免和 avatar 指向、高亮冲突。
    /// </summary>
    public void Show(string gazeCue, string gazeTargetId)
    {
        currentCue = gazeCue;
        hideTime = Time.unscaledTime + Mathf.Max(0f, cueVisibleSeconds);
        if (gazeCueText != null)
        {
            gazeCueText.text = ToDisplayText(gazeCue);
        }
    }

    /// <summary>
    /// 隐藏当前视线提示。
    /// </summary>
    public void Clear()
    {
        currentCue = null;
        if (gazeCueText != null)
        {
            gazeCueText.text = string.Empty;
        }
    }

    private static string ToDisplayText(string gazeCue)
    {
        switch (gazeCue)
        {
            case "look_left":
                return "请看向左侧";
            case "look_right":
                return "请看向右侧";
            case "look_forward":
                return "请看向前方";
            default:
                return string.Empty;
        }
    }
}
