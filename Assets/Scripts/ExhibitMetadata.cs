using UnityEngine;

// 展品语义元数据。输入来自场景/配置，输出供导览、gaze cue、avatar 指向与日志使用。
public sealed class ExhibitMetadata : MonoBehaviour
{
    [SerializeField] private string id;
    [SerializeField] private string exhibitName;
    [SerializeField] private string[] tags;
    [SerializeField, TextArea(2, 4)] private string description;
    [SerializeField] private string semanticRole;
    [SerializeField] private string[] relatedExhibits;
    [SerializeField] private Transform viewPoint;
    [SerializeField] private Transform gazeTarget;

    public string Id => id;
    public string ExhibitName => exhibitName;
    public string[] Tags => tags;
    public string Description => description;
    public string SemanticRole => semanticRole;
    public string[] RelatedExhibits => relatedExhibits;
    public Transform ViewPoint => viewPoint;
    public Transform GazeTarget => gazeTarget;

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
}
