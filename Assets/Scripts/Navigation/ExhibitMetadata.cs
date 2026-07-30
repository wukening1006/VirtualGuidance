using UnityEngine;

/// <summary>
/// 展品语义元数据组件，输入来自场景/配置，输出供导览、gaze cue、avatar 指向与日志使用。
/// </summary>
public sealed class ExhibitMetadata : MonoBehaviour
{
    [Header("展品语义")]
    [Tooltip("展品 ID，例如 E1、E2；需要与 JSON schema 和日志字段保持一致。")]
    [SerializeField] private string id;
    [Tooltip("展品展示名称，用于 UI、讲解文本和语义导出。")]
    [SerializeField] private string exhibitName;
    [Tooltip("展品语义标签，用于后续路线选择和 related exhibit 匹配。")]
    [SerializeField] private string[] tags;
    [Tooltip("展品简短描述，用于 mock guidance 或后续 MLLM prompt。")]
    [SerializeField, TextArea(2, 4)] private string description;
    [Tooltip("展品在实验路线中的语义角色，例如 introduction、main_target。")]
    [SerializeField] private string semanticRole;
    [Tooltip("与当前展品语义相关的其他展品 ID 列表。")]
    [SerializeField] private string[] relatedExhibits;

    [Header("展品 Anchor")]
    [Tooltip("建议用户观看该展品时站立或移动到的虚拟位置。")]
    [SerializeField] private Transform viewPoint;
    [Tooltip("gaze cue 和 avatar 指向应关注的目标点。")]
    [SerializeField] private Transform gazeTarget;

    /// <summary>
    /// 展品 ID，例如 E1、E2，用于 JSON schema、日志和跨模块引用。
    /// </summary>
    public string Id => id;

    /// <summary>
    /// 展品展示名称，用于 UI、讲解文本和场景语义导出。
    /// </summary>
    public string ExhibitName => exhibitName;

    /// <summary>
    /// 展品语义标签，用于后续路线选择和 related exhibit 匹配。
    /// </summary>
    public string[] Tags => tags;

    /// <summary>
    /// 展品简短描述，用于 mock guidance 或后续 MLLM prompt。
    /// </summary>
    public string Description => description;

    /// <summary>
    /// 展品在实验路线中的语义角色，例如 introduction、main_target。
    /// </summary>
    public string SemanticRole => semanticRole;

    /// <summary>
    /// 将场景语义角色映射为安全候选职责；只有显式 turn_cue_anchor 才不会要求到达。
    /// </summary>
    public NavigationTargetRole SafetyTargetRole
    {
        get
        {
            if (string.Equals(semanticRole, "turn_cue_anchor", System.StringComparison.OrdinalIgnoreCase) ||
                ContainsTag("turn_cue"))
            {
                return NavigationTargetRole.TurnCueAnchor;
            }

            return NavigationTargetRole.NavigableDetour;
        }
    }

    /// <summary>
    /// 当前展品是否由场景元数据声明为安全规划候选。
    /// </summary>
    public bool IsSafetyPlanningTarget =>
        SafetyTargetRole == NavigationTargetRole.TurnCueAnchor ||
        ContainsTag("navigable_detour") ||
        ContainsTag("left_redirect") ||
        ContainsTag("right_redirect") ||
        string.Equals(semanticRole, "navigable_detour", System.StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 与当前展品语义相关的其他展品 ID 列表。
    /// </summary>
    public string[] RelatedExhibits => relatedExhibits;

    /// <summary>
    /// 建议用户观看该展品时站立或移动到的虚拟位置。
    /// </summary>
    public Transform ViewPoint => viewPoint;

    /// <summary>
    /// gaze cue 和 avatar 指向应关注的目标点。
    /// </summary>
    public Transform GazeTarget => gazeTarget;

    /// <summary>
    /// 批量配置展品语义元数据，供场景构建脚本生成 E1-E8 时写入规范字段。
    /// </summary>
    /// <param name="exhibitId">展品 ID。</param>
    /// <param name="displayName">展品显示名称。</param>
    /// <param name="exhibitTags">展品语义标签。</param>
    /// <param name="exhibitDescription">展品描述文本。</param>
    /// <param name="role">展品在导览实验中的语义角色。</param>
    /// <param name="related">相关展品 ID 列表。</param>
    /// <param name="viewingPoint">推荐观看位置。</param>
    /// <param name="lookingTarget">推荐注视目标。</param>
    public void Configure(
        string exhibitId,
        string displayName,
        string[] exhibitTags,
        string exhibitDescription,
        string role,
        string[] related,
        Transform viewingPoint,
        Transform lookingTarget)
    {
        id = exhibitId;
        exhibitName = displayName;
        tags = exhibitTags;
        description = exhibitDescription;
        semanticRole = role;
        relatedExhibits = related;
        viewPoint = viewingPoint;
        gazeTarget = lookingTarget;
    }

    private bool ContainsTag(string expected)
    {
        if (tags == null)
        {
            return false;
        }

        foreach (var tag in tags)
        {
            if (string.Equals(tag, expected, System.StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
