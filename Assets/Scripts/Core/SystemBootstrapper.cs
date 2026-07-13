using UnityEngine;

[DefaultExecutionOrder(-10000)]
public sealed class SystemBootstrapper : MonoBehaviour
{
    [SerializeField] private GuidanceSceneInstaller guidanceSceneInstaller;

    private void Awake()
    {
        if (guidanceSceneInstaller == null)
        {
            guidanceSceneInstaller = FindObjectOfType<GuidanceSceneInstaller>();
        }

        guidanceSceneInstaller?.Install();
    }
}
