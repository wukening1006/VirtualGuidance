using UnityEngine;

[DefaultExecutionOrder(-10000)]
/// <summary>
/// 在普通场景组件初始化前启动 guidance 场景装配，确保依赖和事件连接可供后续模块使用。
/// 装配器来自 Inspector，缺省时从当前场景查找。
/// </summary>
public sealed class SystemBootstrapper : MonoBehaviour
{
    [SerializeField] private GuidanceSceneInstaller guidanceSceneInstaller;

    private void Awake()
    {
        // 提前执行安装可避免其他组件在 Awake/OnEnable 中观察到未连接的 guidance 链。
        if (guidanceSceneInstaller == null)
        {
            guidanceSceneInstaller = FindObjectOfType<GuidanceSceneInstaller>();
        }

        guidanceSceneInstaller?.Install();
    }
}
