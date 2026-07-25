using TMPro;
using UnityEngine;

/// <summary>
/// 显示身体动作提示，只提示用户如何行动，不直接移动用户或执行 reset。
/// </summary>
public sealed class MovementHintController : MonoBehaviour
{
    [Header("动作提示 UI")]
    [Tooltip("显示 movement_instruction 的文本控件；未配置时自动查找 MovementHintPanel_Text。")]
    [SerializeField] private TMP_Text movementHintText;

    /// <summary>
    /// 注入动作提示文本控件，支持 bootstrap 自动接线。
    /// </summary>
    public void Configure(TMP_Text text)
    {
        movementHintText = text;
        Clear();
    }

    /// <summary>
    /// 显示后端给出的 movement_instruction，未知值按 pause 处理。
    /// </summary>
    public void Show(string movementInstruction)
    {
        ResolveReferences();
        if (movementHintText == null)
        {
            return;
        }

        movementHintText.text = ToDisplayText(movementInstruction);
    }

    /// <summary>
    /// 隐藏当前动作提示。
    /// </summary>
    public void Clear()
    {
        ResolveReferences();
        if (movementHintText != null)
        {
            movementHintText.text = string.Empty;
        }
    }

    private void ResolveReferences()
    {
        if (movementHintText != null)
        {
            return;
        }

        var textObject = GameObject.Find("MovementHintPanel_Text");
        if (textObject != null)
        {
            movementHintText = textObject.GetComponent<TMP_Text>();
        }
    }

    private static string ToDisplayText(string instruction)
    {
        switch (instruction)
        {
            case "move_forward_slowly":
                return "缓慢向前移动";
            case "turn_left_slowly":
                return "缓慢向左转";
            case "turn_right_slowly":
                return "缓慢向右转";
            case "look_left":
                return "请看向左侧";
            case "look_right":
                return "请看向右侧";
            case "look_forward":
                return "请看向前方";
            case "pause_and_wait":
                return "请暂停等待";
            case "reset_required":
                return "请暂停等待";
            default:
                return "请暂停等待";
        }
    }
}
