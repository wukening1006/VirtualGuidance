using UnityEngine;

/// <summary>
/// 中文说明：把后端 avatar_action 映射为可见的导览员动作，MVP 使用指向调试物体表达方向。
/// </summary>
public sealed class GuideAvatarController : MonoBehaviour
{
    [Header("Avatar 引用")]
    [Tooltip("导览员根节点；未配置时自动查找 GuideAvatar。")]
    [SerializeField] private Transform avatarRoot;
    [Tooltip("用于表达指向动作的调试手臂或箭头；未配置时自动查找 PointingArm_Debug。")]
    [SerializeField] private Transform pointingArm;

    [Header("动作参数")]
    [Tooltip("指向动作时手臂或箭头的抬起角度，单位：度。")]
    [SerializeField] private float pointingPitchDegrees = -25f;

    /// <summary>
    /// 中文说明：注入 avatar 和指向物体引用。
    /// </summary>
    public void Configure(Transform root, Transform pointer)
    {
        avatarRoot = root;
        pointingArm = pointer;
        Wait();
    }

    /// <summary>
    /// 中文说明：执行后端返回的 avatar_action；未知动作回到 wait。
    /// </summary>
    public void Apply(string avatarAction, string selectedTarget, string gazeTargetId)
    {
        ResolveReferences();
        switch (avatarAction)
        {
            case "point_left":
                Point(-55f);
                break;
            case "point_right":
                Point(55f);
                break;
            case "point_forward":
                Point(0f);
                break;
            case "look_at_target":
                LookAtTarget(selectedTarget, gazeTargetId);
                break;
            case "wait":
                Wait();
                break;
            default:
                Wait();
                break;
        }
    }

    /// <summary>
    /// 中文说明：恢复等待状态，rejected guidance 必须调用该状态。
    /// </summary>
    public void Wait()
    {
        ResolveReferences();
        if (pointingArm != null)
        {
            pointingArm.localRotation = Quaternion.identity;
        }
    }

    private void Point(float yawDegrees)
    {
        if (pointingArm != null)
        {
            pointingArm.localRotation = Quaternion.Euler(pointingPitchDegrees, yawDegrees, 0f);
        }
    }

    private void LookAtTarget(string selectedTarget, string gazeTargetId)
    {
        ResolveReferences();
        var target = FindTargetTransform(selectedTarget, gazeTargetId);
        if (target == null || avatarRoot == null)
        {
            Wait();
            return;
        }

        var direction = target.position - avatarRoot.position;
        direction.y = 0f;
        if (direction.sqrMagnitude <= 0.001f)
        {
            Wait();
            return;
        }

        avatarRoot.rotation = Quaternion.LookRotation(direction.normalized, Vector3.up);
        Point(0f);
    }

    private Transform FindTargetTransform(string selectedTarget, string gazeTargetId)
    {
        if (!string.IsNullOrWhiteSpace(gazeTargetId))
        {
            var gazeObject = GameObject.Find(gazeTargetId);
            if (gazeObject != null)
            {
                return gazeObject.transform;
            }
        }

        var exhibits = FindObjectsOfType<ExhibitMetadata>();
        foreach (var exhibit in exhibits)
        {
            if (exhibit != null && exhibit.Id == selectedTarget)
            {
                return exhibit.GazeTarget != null ? exhibit.GazeTarget : exhibit.transform;
            }
        }

        return null;
    }

    private void ResolveReferences()
    {
        if (avatarRoot == null)
        {
            var avatarObject = GameObject.Find("GuideAvatar");
            if (avatarObject != null)
            {
                avatarRoot = avatarObject.transform;
            }
        }

        if (pointingArm == null)
        {
            var pointerObject = GameObject.Find("PointingArm_Debug");
            if (pointerObject != null)
            {
                pointingArm = pointerObject.transform;
            }
        }
    }
}
