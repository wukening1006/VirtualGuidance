using UnityEditor;
using UnityEngine;

public static class RestoreUnityDefaultLayout
{
    private const string AppliedKey = "VirtualGuidance.RestoreDefaultLayout.Applied";

    [InitializeOnLoadMethod]
    private static void Restore()
    {
        if (EditorPrefs.GetBool(AppliedKey, false))
        {
            return;
        }

        EditorApplication.delayCall += () =>
        {
            if (EditorApplication.ExecuteMenuItem("Window/Layouts/Default"))
            {
                EditorPrefs.SetBool(AppliedKey, true);
                Debug.Log("Unity default layout restored.");
            }
        };
    }
}
