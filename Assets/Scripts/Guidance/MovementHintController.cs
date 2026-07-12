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
            movementHintText.text = "Movement: waiting";
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
                return "Movement: move forward slowly";
            case "turn_left_slowly":
                return "Movement: turn left slowly";
            case "turn_right_slowly":
                return "Movement: turn right slowly";
            case "look_left":
                return "Movement: look left";
            case "look_right":
                return "Movement: look right";
            case "look_forward":
                return "Movement: look forward";
            case "pause_and_wait":
                return "Movement: pause and wait";
            case "reset_required":
                return "Movement: reset required";
            default:
                return "Movement: pause and wait";
        }
    }
}
