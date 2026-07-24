using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

public static class ScenePreviewCapture
{
    public static void CaptureShowroom()
    {
        Capture(
            "Assets/Showroom Environment - gallery/scenes/showroom.unity",
            "C:/Users/sataxisama/Documents/GitHub/VirtualGuidance/Preview_Showroom.png",
            new Vector3(0f, 2.2f, -8f),
            Quaternion.Euler(12f, 0f, 0f),
            58f);
    }

    public static void CaptureFactory()
    {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        var floor = GameObject.CreatePrimitive(PrimitiveType.Plane);
        floor.name = "Preview Floor";
        floor.transform.localScale = new Vector3(0.08f, 1f, 0.08f);
        var floorRenderer = floor.GetComponent<Renderer>();
        if (floorRenderer != null)
        {
            floorRenderer.sharedMaterial = new Material(Shader.Find("Standard"))
            {
                color = new Color(0.45f, 0.45f, 0.45f)
            };
        }

        string[] prefabPaths =
        {
            "Assets/Low Poly Factory/Prefabs/Props/SM_Forklift_01.prefab",
            "Assets/Low Poly Factory/Prefabs/Props/SM_Machine_01.prefab",
            "Assets/Low Poly Factory/Prefabs/Props/SM_Container_01.prefab",
            "Assets/Low Poly Factory/Prefabs/Props/SM_Rack_01.prefab",
            "Assets/Low Poly Factory/Prefabs/Props/SM_Barrel_01.prefab",
            "Assets/Low Poly Factory/Prefabs/Props/SM_Crane_01.prefab"
        };

        Vector3[] positions =
        {
            new Vector3(-3f, 0f, 0f),
            new Vector3(1f, 0f, 0.5f),
            new Vector3(3f, 0f, -1.2f),
            new Vector3(-1f, 0f, 2.5f),
            new Vector3(0f, 0f, -2.5f),
            new Vector3(3.5f, 0f, 2.2f)
        };

        for (var index = 0; index < prefabPaths.Length; index++)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPaths[index]);
            if (prefab == null)
            {
                Debug.LogWarning($"Missing factory preview prefab: {prefabPaths[index]}");
                continue;
            }

            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            instance.transform.position = positions[index];
            instance.transform.rotation = Quaternion.Euler(0f, index * 35f, 0f);
        }

        CaptureFramedScene(
            "C:/Users/sataxisama/Documents/GitHub/VirtualGuidance/Preview_Factory.png",
            48f);
    }

    private static void Capture(string scenePath, string outputPath, Vector3 cameraPosition, Quaternion cameraRotation, float fieldOfView)
    {
        EditorSceneManager.OpenScene(scenePath);
        CaptureCurrentScene(outputPath, cameraPosition, cameraRotation, fieldOfView);
    }

    private static void CaptureCurrentScene(string outputPath, Vector3 cameraPosition, Quaternion cameraRotation, float fieldOfView)
    {
        var camera = Camera.main;
        if (camera == null)
        {
            var cameraObject = new GameObject("Preview Camera");
            camera = cameraObject.AddComponent<Camera>();
        }

        camera.transform.position = cameraPosition;
        camera.transform.rotation = cameraRotation;
        camera.fieldOfView = fieldOfView;
        camera.nearClipPlane = 0.03f;
        camera.farClipPlane = 200f;

        var light = Object.FindObjectOfType<Light>();
        if (light == null)
        {
            var lightObject = new GameObject("Preview Directional Light");
            light = lightObject.AddComponent<Light>();
            light.type = LightType.Directional;
        }

        light.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
        light.intensity = 1.2f;

        var renderTexture = new RenderTexture(1600, 900, 24);
        camera.targetTexture = renderTexture;
        camera.Render();

        RenderTexture.active = renderTexture;
        var texture = new Texture2D(renderTexture.width, renderTexture.height, TextureFormat.RGB24, false);
        texture.ReadPixels(new Rect(0, 0, renderTexture.width, renderTexture.height), 0, 0);
        texture.Apply();

        File.WriteAllBytes(outputPath, texture.EncodeToPNG());

        camera.targetTexture = null;
        RenderTexture.active = null;
        Object.DestroyImmediate(texture);
        Object.DestroyImmediate(renderTexture);

        Debug.Log($"Preview saved to {outputPath}");
    }

    private static void CaptureFramedScene(string outputPath, float fieldOfView)
    {
        var renderers = Object.FindObjectsOfType<Renderer>();
        var hasBounds = false;
        var bounds = new Bounds(Vector3.zero, Vector3.zero);
        foreach (var renderer in renderers)
        {
            if (renderer.name == "Preview Floor")
            {
                continue;
            }

            if (!hasBounds)
            {
                bounds = renderer.bounds;
                hasBounds = true;
            }
            else
            {
                bounds.Encapsulate(renderer.bounds);
            }
        }

        var center = hasBounds ? bounds.center : Vector3.zero;
        var size = hasBounds ? Mathf.Max(bounds.size.x, bounds.size.y, bounds.size.z) : 5f;
        Debug.Log($"Factory preview bounds hasBounds={hasBounds} center={center} size={bounds.size} maxSize={size}");
        var distance = Mathf.Max(3f, size * 1.8f);
        var position = center + new Vector3(0f, size * 0.55f, -distance);
        var rotation = Quaternion.LookRotation(center - position, Vector3.up);

        CaptureCurrentScene(outputPath, position, rotation, fieldOfView);
    }
}
