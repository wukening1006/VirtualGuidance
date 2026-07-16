using System;
using System.IO;
using System.Text;
using TMPro;
using UnityEditor;
using UnityEditor.Events;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

public static class PhaseOneSceneConfigurator
{
    private const string MenuPath = "VirtualGuidance/Configure Phase One Scene";
    private const string ValidateMenuPath = "VirtualGuidance/Validate Phase One Scene";
    private const string ScenePath = "Assets/Scenes/MainScene.unity";
    private const string ParticipantLayerName = "ParticipantOnly";
    private const string ResearcherLayerName = "ResearcherOnly";
    private const string FontFolder = "Assets/Fonts/NotoSansCJKSC";
    private const string FontPath = FontFolder + "/NotoSansSC-Regular.otf";
    private const string FontAssetPath = FontFolder + "/NotoSansSC Dynamic SDF.asset";
    private const string LicensePath = FontFolder + "/OFL-1.1.txt";
    private const string InstalledFontPath = @"C:\Windows\Fonts\Noto Sans SC (TrueType).otf";

    [MenuItem(MenuPath)]
    public static void Configure()
    {
        if (SceneManager.GetActiveScene().path != ScenePath)
        {
            throw new InvalidOperationException($"Open {ScenePath} before running the configurator.");
        }

        Undo.IncrementCurrentGroup();
        Undo.SetCurrentGroupName("Configure VirtualGuidance Phase One");

        var participantLayer = EnsureLayer(ParticipantLayerName);
        var researcherLayer = EnsureLayer(ResearcherLayerName);
        var fontAsset = EnsureChineseFontAsset();

        var mainCamera = Camera.main;
        if (mainCamera == null)
        {
            throw new InvalidOperationException("MainScene must contain one camera tagged MainCamera.");
        }

        mainCamera.cullingMask |= 1 << participantLayer;
        mainCamera.cullingMask &= ~(1 << researcherLayer);
        EditorUtility.SetDirty(mainCamera);

        var diagnostics = FindOrCreateRoot("Diagnostics");
        var researcherCamera = ConfigureResearcherCamera(diagnostics.transform, researcherLayer);
        SetLayerRecursively(diagnostics, researcherLayer);

        var uiRoot = FindOrCreateRoot("UI");
        var debugCanvas = ConfigureDebugCanvas(uiRoot.transform, researcherCamera, researcherLayer);
        var participant = ConfigureParticipantCanvas(mainCamera.transform, participantLayer, fontAsset);
        var dashboard = FindRequired<ExperimentDashboard>();
        var statusText = ConfigureResearcherCanvas(uiRoot.transform, researcherCamera, researcherLayer, fontAsset, dashboard);

        ConfigureRuntimeReferences(participant, statusText, dashboard);
        ConfigureInstaller(participant);

        SetLayerRecursively(debugCanvas, researcherLayer);
        SetLayerRecursively(participant.canvasObject, participantLayer);
        EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
        AssetDatabase.SaveAssets();
        EditorSceneManager.SaveScene(SceneManager.GetActiveScene());
        Debug.Log("VirtualGuidance Phase One scene configuration completed.");
    }

    [MenuItem(ValidateMenuPath)]
    public static void Validate()
    {
        Require(SceneManager.GetActiveScene().path == ScenePath, "MainScene is not active.");
        var participantLayer = LayerMask.NameToLayer(ParticipantLayerName);
        var researcherLayer = LayerMask.NameToLayer(ResearcherLayerName);
        Require(participantLayer >= 8, "ParticipantOnly layer is missing.");
        Require(researcherLayer >= 8, "ResearcherOnly layer is missing.");

        var mainCameras = Array.FindAll(UnityEngine.Object.FindObjectsOfType<Camera>(true), camera => camera.CompareTag("MainCamera"));
        Require(mainCameras.Length == 1, "Exactly one MainCamera is required.");
        var main = mainCameras[0];
        Require((main.cullingMask & (1 << researcherLayer)) == 0, "MainCamera still renders ResearcherOnly.");
        Require((main.cullingMask & (1 << participantLayer)) != 0, "MainCamera does not render ParticipantOnly.");

        var researcher = GameObject.Find("ResearcherCamera")?.GetComponent<Camera>();
        Require(researcher != null, "ResearcherCamera is missing.");
        Require(researcher.stereoTargetEye == StereoTargetEyeMask.None, "ResearcherCamera must be non-stereo.");
        Require(researcher.orthographic && Mathf.Approximately(researcher.orthographicSize, 3.5f), "ResearcherCamera orthographic settings are invalid.");
        Require(researcher.cullingMask == (1 << researcherLayer), "ResearcherCamera must render only ResearcherOnly.");
        Require(Vector3.Distance(researcher.transform.position, new Vector3(7f, 8f, 0f)) < 0.01f, "ResearcherCamera position is invalid.");

        var participantObject = GameObject.Find("ParticipantCanvas");
        Require(participantObject != null && participantObject.transform.parent == main.transform, "ParticipantCanvas is not parented to MainCamera.");
        var participantCanvas = participantObject.GetComponent<Canvas>();
        var participantRect = participantObject.GetComponent<RectTransform>();
        Require(participantCanvas != null && participantCanvas.renderMode == RenderMode.WorldSpace, "ParticipantCanvas must be World Space.");
        Require(participantRect.sizeDelta == new Vector2(800f, 300f), "ParticipantCanvas size must be 800x300.");
        Require(Vector3.Distance(participantRect.localPosition, new Vector3(0f, -0.22f, 1.5f)) < 0.001f, "ParticipantCanvas local position is invalid.");
        Require(Vector3.Distance(participantRect.localScale, Vector3.one * 0.001f) < 0.0001f, "ParticipantCanvas scale is invalid.");
        Require(participantObject.GetComponent<GraphicRaycaster>() == null, "ParticipantCanvas must not have GraphicRaycaster.");
        foreach (var component in participantObject.GetComponents<Component>())
            Require(component == null || component.GetType().Name != "TrackedDeviceGraphicRaycaster", "ParticipantCanvas must not have TrackedDeviceGraphicRaycaster.");

        var guidanceGroup = participantObject.transform.Find("GuidancePanel")?.GetComponent<CanvasGroup>();
        var safetyGroup = participantObject.transform.Find("SafetyPanel")?.GetComponent<CanvasGroup>();
        Require(guidanceGroup != null && safetyGroup != null, "Participant panel CanvasGroups are missing.");
        Require(guidanceGroup.alpha < 0.5f && safetyGroup.alpha > 0.5f, "Safety must exclusively own the initial Participant display.");
        var narration = participantObject.transform.Find("GuidancePanel/GuidanceTextPanel_Text")?.GetComponent<TMP_Text>();
        var action = participantObject.transform.Find("GuidancePanel/MovementHintPanel_Text")?.GetComponent<TMP_Text>();
        var gaze = participantObject.transform.Find("GuidancePanel/GazeCuePanel_Text")?.GetComponent<TMP_Text>();
        var safety = participantObject.transform.Find("SafetyPanel/SafetyText")?.GetComponent<TMP_Text>();
        Require(narration != null && Mathf.Approximately(narration.fontSize, 42f), "Narration font size must be 42.");
        Require(action != null && Mathf.Approximately(action.fontSize, 34f), "Action font size must be 34.");
        Require(gaze != null && Mathf.Approximately(gaze.fontSize, 28f), "Gaze font size must be 28.");
        Require(safety != null && Mathf.Approximately(safety.fontSize, 40f), "Safety font size must be 40.");
        Require(narration.font != null && narration.font.atlasPopulationMode == AtlasPopulationMode.Dynamic && narration.font.isMultiAtlasTexturesEnabled,
            "Noto TMP Font Asset must be dynamic with Multi Atlas enabled.");
        Require(File.Exists(FontPath) && File.Exists(LicensePath), "Noto source font or OFL license is missing.");

        var researcherCanvas = GameObject.Find("ResearcherCanvas")?.GetComponent<Canvas>();
        Require(researcherCanvas != null && researcherCanvas.renderMode == RenderMode.ScreenSpaceCamera && researcherCanvas.worldCamera == researcher,
            "ResearcherCanvas camera binding is invalid.");
        var researcherScaler = researcherCanvas.GetComponent<CanvasScaler>();
        Require(researcherScaler != null && researcherScaler.referenceResolution == new Vector2(1920f, 1080f), "ResearcherCanvas reference resolution is invalid.");
        Require(researcherCanvas.GetComponent<GraphicRaycaster>() != null, "ResearcherCanvas needs a desktop GraphicRaycaster.");
        Require(GameObject.Find("ParticipantIdInput")?.GetComponent<TMP_InputField>() != null, "Participant ID input is missing.");
        Require(GameObject.Find("ConditionDropdown")?.GetComponent<TMP_Dropdown>()?.captionText != null, "Condition dropdown caption is missing.");
        Require(GameObject.Find("TaskDropdown")?.GetComponent<TMP_Dropdown>()?.captionText != null, "Task dropdown caption is missing.");
        Require(GameObject.Find("PhysicalSpaceDropdown")?.GetComponent<TMP_Dropdown>()?.captionText != null, "Physical-space dropdown caption is missing.");
        Require(GameObject.Find("StartButton")?.GetComponentInChildren<TMP_Text>(true) != null, "Start button label is missing.");
        Require(GameObject.Find("PauseResumeButton")?.GetComponentInChildren<TMP_Text>(true) != null, "Pause button label is missing.");
        Require(GameObject.Find("EndButton")?.GetComponentInChildren<TMP_Text>(true) != null, "End button label is missing.");

        var debugCanvas = GameObject.Find("DebugCanvas")?.GetComponent<Canvas>();
        Require(debugCanvas != null && debugCanvas.worldCamera == researcher, "DebugCanvas camera binding is invalid.");
        Require(AllOnLayer(researcherCanvas.gameObject, researcherLayer) && AllOnLayer(debugCanvas.gameObject, researcherLayer),
            "Researcher or Debug UI contains an incorrect layer.");
        Require(AllOnLayer(participantObject, participantLayer), "Participant UI contains an incorrect layer.");
        var eventSystems = UnityEngine.Object.FindObjectsOfType<UnityEngine.EventSystems.EventSystem>(true);
        Require(eventSystems.Length == 1, "Exactly one EventSystem is required.");
        var xrUiInputModules = Array.FindAll(
            eventSystems[0].GetComponents<Component>(),
            component => component != null && component.GetType().Name == "XRUIInputModule");
        Require(xrUiInputModules.Length == 1, "The scene EventSystem must use exactly one XRUIInputModule.");

        var installer = FindRequired<GuidanceSceneInstaller>();
        Require(!GetSerializedBool(installer, "createDebugUiIfMissing") && !GetSerializedBool(installer, "createRequestButton"),
            "Installer automatic UI switches must be disabled.");
        Require(GetSerializedReference(installer, "participantUiController") != null, "Installer Participant UI reference is missing.");
        var dashboard = FindRequired<ExperimentDashboard>();
        Require(GetSerializedReference(dashboard, "statusText") != null && GetSerializedReference(dashboard, "participantUiController") != null,
            "Dashboard UI references are missing.");
        Require(!SceneManager.GetActiveScene().isDirty, "MainScene still has unsaved changes.");
        Debug.Log("VirtualGuidance Phase One validation passed.");
    }

    private static Camera ConfigureResearcherCamera(Transform parent, int layer)
    {
        var cameraObject = FindOrCreateChild(parent, "ResearcherCamera");
        cameraObject.tag = "Untagged";
        cameraObject.layer = layer;
        cameraObject.transform.SetPositionAndRotation(new Vector3(7f, 8f, 0f), Quaternion.Euler(90f, 0f, 0f));
        var camera = GetOrAdd<Camera>(cameraObject);
        camera.enabled = true;
        camera.orthographic = true;
        camera.orthographicSize = 3.5f;
        camera.nearClipPlane = 0.01f;
        camera.farClipPlane = 30f;
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = new Color(0.035f, 0.045f, 0.055f, 1f);
        camera.cullingMask = 1 << layer;
        camera.depth = 10f;
        camera.stereoTargetEye = StereoTargetEyeMask.None;
        camera.targetDisplay = 0;
        EditorUtility.SetDirty(camera);
        return camera;
    }

    private static GameObject ConfigureDebugCanvas(Transform uiRoot, Camera camera, int layer)
    {
        var debug = GameObject.Find("DebugCanvas");
        if (debug == null)
        {
            debug = GameObject.Find("ResearcherCanvas");
            if (debug != null)
            {
                Undo.RecordObject(debug, "Rename debug canvas");
                debug.name = "DebugCanvas";
            }
        }

        if (debug == null)
        {
            debug = FindOrCreateChild(uiRoot, "DebugCanvas");
        }
        else
        {
            debug.transform.SetParent(uiRoot, false);
        }

        var canvas = GetOrAdd<Canvas>(debug);
        canvas.renderMode = RenderMode.ScreenSpaceCamera;
        canvas.worldCamera = camera;
        canvas.planeDistance = 1f;
        canvas.sortingOrder = 20;
        var scaler = GetOrAdd<CanvasScaler>(debug);
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight = 0.5f;
        SetLayerRecursively(debug, layer);
        return debug;
    }

    private static ParticipantBindings ConfigureParticipantCanvas(Transform mainCamera, int layer, TMP_FontAsset font)
    {
        var canvasObject = GameObject.Find("ParticipantCanvas");
        if (canvasObject == null)
        {
            canvasObject = new GameObject("ParticipantCanvas", typeof(RectTransform));
            Undo.RegisterCreatedObjectUndo(canvasObject, "Create ParticipantCanvas");
        }
        canvasObject.transform.SetParent(mainCamera, false);
        var rect = (RectTransform)canvasObject.transform;
        rect.localPosition = new Vector3(0f, -0.22f, 1.5f);
        rect.localRotation = Quaternion.identity;
        rect.localScale = Vector3.one * 0.001f;
        rect.sizeDelta = new Vector2(800f, 300f);

        var canvas = GetOrAdd<Canvas>(canvasObject);
        canvas.renderMode = RenderMode.WorldSpace;
        canvas.worldCamera = mainCamera.GetComponent<Camera>();
        canvas.sortingOrder = 10;
        var scaler = GetOrAdd<CanvasScaler>(canvasObject);
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
        scaler.dynamicPixelsPerUnit = 10f;
        RemoveComponent<GraphicRaycaster>(canvasObject);
        RemoveComponentByName(canvasObject, "TrackedDeviceGraphicRaycaster");

        var guidancePanel = FindOrCreateChild(canvasObject.transform, "GuidancePanel");
        ConfigureFullPanel(guidancePanel, new Color(0.025f, 0.035f, 0.045f, 0.82f));
        var guidanceGroup = GetOrAdd<CanvasGroup>(guidancePanel);
        SetCanvasGroup(guidanceGroup, false);

        var safetyPanel = FindOrCreateChild(canvasObject.transform, "SafetyPanel");
        ConfigureFullPanel(safetyPanel, new Color(0.30f, 0.035f, 0.025f, 0.94f));
        var safetyGroup = GetOrAdd<CanvasGroup>(safetyPanel);
        SetCanvasGroup(safetyGroup, true);

        var narration = FindOrCreateText("GuidanceTextPanel_Text", guidancePanel.transform, font, 42f);
        SetRect(narration.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f),
            Vector2.zero, new Vector2(0f, 116f), new Vector2(32f, -22f), new Vector2(-32f, -138f));
        narration.alignment = TextAlignmentOptions.TopLeft;

        var action = FindOrCreateText("MovementHintPanel_Text", guidancePanel.transform, font, 34f);
        SetRect(action.rectTransform, new Vector2(0f, 0.5f), new Vector2(1f, 0.5f), new Vector2(0.5f, 0.5f),
            new Vector2(0f, -8f), new Vector2(0f, 64f), Vector2.zero, Vector2.zero);
        action.alignment = TextAlignmentOptions.Center;

        var gaze = FindOrCreateText("GazeCuePanel_Text", guidancePanel.transform, font, 28f);
        SetRect(gaze.rectTransform, new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0.5f, 0f),
            new Vector2(0f, 22f), new Vector2(0f, 50f), Vector2.zero, Vector2.zero);
        gaze.alignment = TextAlignmentOptions.Center;

        var safety = FindOrCreateText("SafetyText", safetyPanel.transform, font, 40f);
        Stretch(safety.rectTransform, 40f, 28f, 40f, 28f);
        safety.alignment = TextAlignmentOptions.Center;
        safety.text = "设备已连接，请等待实验开始";

        var controller = GetOrAdd<ParticipantUiController>(canvasObject);
        SetObjectReference(controller, "guidancePanel", guidanceGroup);
        SetObjectReference(controller, "safetyPanel", safetyGroup);
        SetObjectReference(controller, "narrationText", narration);
        SetObjectReference(controller, "actionText", action);
        SetObjectReference(controller, "gazeText", gaze);
        SetObjectReference(controller, "safetyText", safety);
        SetLayerRecursively(canvasObject, layer);

        return new ParticipantBindings(canvasObject, controller, narration, action, gaze);
    }

    private static TMP_Text ConfigureResearcherCanvas(
        Transform uiRoot,
        Camera camera,
        int layer,
        TMP_FontAsset font,
        ExperimentDashboard dashboard)
    {
        var canvasObject = GameObject.Find("ResearcherCanvas");
        if (canvasObject == null)
        {
            canvasObject = new GameObject("ResearcherCanvas", typeof(RectTransform));
            Undo.RegisterCreatedObjectUndo(canvasObject, "Create ResearcherCanvas");
        }
        canvasObject.transform.SetParent(uiRoot, false);
        ClearChildren(canvasObject.transform);

        var canvas = GetOrAdd<Canvas>(canvasObject);
        canvas.renderMode = RenderMode.ScreenSpaceCamera;
        canvas.worldCamera = camera;
        canvas.planeDistance = 1f;
        canvas.sortingOrder = 30;
        var scaler = GetOrAdd<CanvasScaler>(canvasObject);
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight = 0.5f;
        GetOrAdd<GraphicRaycaster>(canvasObject);

        var controls = FindOrCreateChild(canvasObject.transform, "ControlsPanel");
        var controlsRect = ConfigurePanelRect(controls, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f),
            new Vector2(24f, -24f), new Vector2(520f, 1032f), new Color(0.035f, 0.05f, 0.065f, 0.94f));

        CreateLabel(controlsRect, "ParticipantLabel", "Participant ID", font, 22f, 24f, -28f, 200f, 32f);
        var input = CreateInputField(controlsRect, "ParticipantIdInput", "P001", font, 24f, 24f, -68f, 472f, 48f);
        ClearPersistent(input.onEndEdit);
        UnityEventTools.AddPersistentListener(input.onEndEdit, new UnityAction<string>(dashboard.SetParticipantId));

        CreateLabel(controlsRect, "ConditionLabel", "Condition", font, 22f, 24f, -136f, 200f, 32f);
        var condition = CreateDropdown(controlsRect, "ConditionDropdown", ExperimentConditionMatrix.Conditions, 5, font, 22f, 24f, -176f, 472f, 48f);
        ClearPersistent(condition.onValueChanged);
        UnityEventTools.AddPersistentListener(condition.onValueChanged, new UnityAction<int>(dashboard.SetConditionIndex));

        CreateLabel(controlsRect, "TaskLabel", "Task", font, 22f, 24f, -244f, 200f, 32f);
        var task = CreateDropdown(controlsRect, "TaskDropdown", ExperimentConditionMatrix.TaskIds, 1, font, 22f, 24f, -284f, 472f, 48f);
        ClearPersistent(task.onValueChanged);
        UnityEventTools.AddPersistentListener(task.onValueChanged, new UnityAction<int>(dashboard.SetTaskIndex));

        CreateLabel(controlsRect, "SpaceLabel", "Physical Space", font, 22f, 24f, -352f, 220f, 32f);
        var space = CreateDropdown(controlsRect, "PhysicalSpaceDropdown", ExperimentConditionMatrix.PhysicalSpaces, 1, font, 22f, 24f, -392f, 472f, 48f);
        ClearPersistent(space.onValueChanged);
        UnityEventTools.AddPersistentListener(space.onValueChanged, new UnityAction<int>(dashboard.SetPhysicalSpaceIndex));

        var start = CreateButton(controlsRect, "StartButton", "开始", font, 24f, 24f, -472f, 148f, 48f);
        var pause = CreateButton(controlsRect, "PauseResumeButton", "暂停/继续", font, 22f, 186f, -472f, 148f, 48f);
        var end = CreateButton(controlsRect, "EndButton", "结束实验", font, 22f, 348f, -472f, 148f, 48f);
        ClearPersistent(start.onClick);
        ClearPersistent(pause.onClick);
        ClearPersistent(end.onClick);
        UnityEventTools.AddPersistentListener(start.onClick, new UnityAction(dashboard.StartSession));
        UnityEventTools.AddPersistentListener(pause.onClick, new UnityAction(dashboard.TogglePauseSession));
        UnityEventTools.AddPersistentListener(end.onClick, new UnityAction(dashboard.EndSession));

        var status = FindOrCreateText("ResearcherStatusText", canvasObject.transform, font, 28f);
        SetRect(status.rectTransform, new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(1f, 1f),
            new Vector2(-24f, -24f), new Vector2(610f, 500f), Vector2.zero, Vector2.zero);
        status.alignment = TextAlignmentOptions.TopLeft;
        status.color = Color.white;
        status.text = "Initializing";

        SetObjectReference(dashboard, "statusText", status);
        SetLayerRecursively(canvasObject, layer);
        return status;
    }

    private static void ConfigureRuntimeReferences(ParticipantBindings participant, TMP_Text status, ExperimentDashboard dashboard)
    {
        SetObjectReference(dashboard, "physicalSpaceManager", FindRequired<PhysicalSpaceManager>());
        SetObjectReference(dashboard, "userPoseTracker", FindRequired<UserPoseTracker>());
        SetObjectReference(dashboard, "riskEvaluator", FindRequired<PhysicalRiskEvaluator>());
        SetObjectReference(dashboard, "xrTrackingAdapter", FindRequired<XRTrackingAdapter>());
        SetObjectReference(dashboard, "taskController", FindRequired<GuidanceTaskController>());
        SetObjectReference(dashboard, "guidanceCoordinator", FindRequired<GuidanceCoordinator>());
        SetObjectReference(dashboard, "agentClient", FindRequired<AgentClient>());
        SetObjectReference(dashboard, "experimentLogger", FindRequired<ExperimentLogger>());
        SetObjectReference(dashboard, "arrivalDetector", FindRequired<ArrivalDetector>());
        SetObjectReference(dashboard, "rotationGainController", FindRequired<RotationGainController>());
        SetObjectReference(dashboard, "guidanceExecutor", FindRequired<GuidanceExecutor>());
        SetObjectReference(dashboard, "pauseResetController", FindRequired<PauseResetController>());
        SetObjectReference(dashboard, "participantUiController", participant.controller);
        SetObjectReference(dashboard, "statusText", status);
    }

    private static void ConfigureInstaller(ParticipantBindings participant)
    {
        var installer = FindRequired<GuidanceSceneInstaller>();
        SetBool(installer, "createDebugUiIfMissing", false);
        SetBool(installer, "createRequestButton", false);
        SetObjectReference(installer, "participantUiController", participant.controller);
        SetObjectReference(installer, "narrationText", participant.narration);
        SetObjectReference(installer, "movementHintText", participant.action);
        SetObjectReference(installer, "gazeCueText", participant.gaze);
    }

    private static TMP_FontAsset EnsureChineseFontAsset()
    {
        if (!AssetDatabase.IsValidFolder(FontFolder))
        {
            AssetDatabase.CreateFolder("Assets/Fonts", "NotoSansCJKSC");
        }

        if (!File.Exists(FontPath))
        {
            if (!File.Exists(InstalledFontPath))
                throw new FileNotFoundException("Installed Noto Sans SC font was not found.", InstalledFontPath);
            File.Copy(InstalledFontPath, FontPath, false);
            AssetDatabase.ImportAsset(FontPath, ImportAssetOptions.ForceSynchronousImport);
        }

        EnsureFontLicense();
        var sourceFont = AssetDatabase.LoadAssetAtPath<Font>(FontPath);
        if (sourceFont == null)
            throw new InvalidOperationException("Unity could not import the Noto Sans SC source font.");

        var fontAsset = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(FontAssetPath);
        if (fontAsset == null)
        {
            fontAsset = TMP_FontAsset.CreateFontAsset(sourceFont);
            fontAsset.name = "NotoSansSC Dynamic SDF";
            AssetDatabase.CreateAsset(fontAsset, FontAssetPath);
            if (fontAsset.material != null && !AssetDatabase.Contains(fontAsset.material))
                AssetDatabase.AddObjectToAsset(fontAsset.material, fontAsset);
            if (fontAsset.atlasTextures != null)
            {
                foreach (var atlas in fontAsset.atlasTextures)
                    if (atlas != null && !AssetDatabase.Contains(atlas)) AssetDatabase.AddObjectToAsset(atlas, fontAsset);
            }
        }

        fontAsset.atlasPopulationMode = AtlasPopulationMode.Dynamic;
        fontAsset.isMultiAtlasTexturesEnabled = true;
        EditorUtility.SetDirty(fontAsset);
        return fontAsset;
    }

    private static void EnsureFontLicense()
    {
        if (File.Exists(LicensePath)) return;
        const string source = "Assets/Fonts/TextMesh Pro/Fonts/LiberationSans - OFL.txt";
        var body = File.ReadAllText(source);
        var marker = body.IndexOf("SIL OPEN FONT LICENSE", StringComparison.Ordinal);
        if (marker >= 0) body = body.Substring(marker);
        var header = "Copyright (c) 2014-2020 Adobe (http://www.adobe.com/).\n\n" +
                     "Noto Sans SC is licensed under the SIL Open Font License, Version 1.1.\n\n";
        File.WriteAllText(LicensePath, header + body, new UTF8Encoding(false));
        AssetDatabase.ImportAsset(LicensePath, ImportAssetOptions.ForceSynchronousImport);
    }

    private static TMP_InputField CreateInputField(RectTransform parent, string name, string value, TMP_FontAsset font, float fontSize, float x, float y, float width, float height)
    {
        var root = FindOrCreateChild(parent, name);
        ConfigureTopLeftRect(root.GetComponent<RectTransform>(), x, y, width, height);
        GetOrAdd<Image>(root).color = new Color(0.10f, 0.13f, 0.16f, 1f);
        var field = GetOrAdd<TMP_InputField>(root);
        var viewport = FindOrCreateChild(root.transform, "Text Area");
        Stretch(viewport.GetComponent<RectTransform>(), 12f, 6f, 12f, 6f);
        GetOrAdd<RectMask2D>(viewport);
        var placeholder = FindOrCreateText("Placeholder", viewport.transform, font, fontSize);
        Stretch(placeholder.rectTransform, 0f, 0f, 0f, 0f);
        placeholder.text = "Participant ID";
        placeholder.color = new Color(1f, 1f, 1f, 0.4f);
        var text = FindOrCreateText("Text", viewport.transform, font, fontSize);
        Stretch(text.rectTransform, 0f, 0f, 0f, 0f);
        text.text = value;
        field.textViewport = viewport.GetComponent<RectTransform>();
        field.textComponent = text;
        field.placeholder = placeholder;
        field.text = value;
        return field;
    }

    private static TMP_Dropdown CreateDropdown(RectTransform parent, string name, string[] options, int value, TMP_FontAsset font, float fontSize, float x, float y, float width, float height)
    {
        var root = FindOrCreateChild(parent, name);
        ConfigureTopLeftRect(root.GetComponent<RectTransform>(), x, y, width, height);
        GetOrAdd<Image>(root).color = new Color(0.10f, 0.13f, 0.16f, 1f);
        var dropdown = GetOrAdd<TMP_Dropdown>(root);
        var caption = FindOrCreateText("Label", root.transform, font, fontSize);
        Stretch(caption.rectTransform, 12f, 4f, 42f, 4f);
        caption.alignment = TextAlignmentOptions.MidlineLeft;
        var arrow = FindOrCreateText("Arrow", root.transform, font, 20f);
        SetRect(arrow.rectTransform, new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), new Vector2(-12f, 0f), new Vector2(28f, height), Vector2.zero, Vector2.zero);
        arrow.text = "▼";
        arrow.alignment = TextAlignmentOptions.Center;

        var template = FindOrCreateChild(root.transform, "Template");
        var templateRect = template.GetComponent<RectTransform>();
        SetRect(templateRect, new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0.5f, 1f), Vector2.zero, new Vector2(0f, 190f), Vector2.zero, Vector2.zero);
        GetOrAdd<Image>(template).color = new Color(0.06f, 0.08f, 0.10f, 1f);
        var scroll = GetOrAdd<ScrollRect>(template);
        var viewport = FindOrCreateChild(template.transform, "Viewport");
        Stretch(viewport.GetComponent<RectTransform>(), 4f, 4f, 4f, 4f);
        GetOrAdd<RectMask2D>(viewport);
        var content = FindOrCreateChild(viewport.transform, "Content");
        var contentRect = content.GetComponent<RectTransform>();
        SetRect(contentRect, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f), Vector2.zero, Vector2.zero, Vector2.zero, Vector2.zero);
        var layout = GetOrAdd<VerticalLayoutGroup>(content);
        layout.childControlHeight = true;
        layout.childControlWidth = true;
        layout.childForceExpandHeight = false;
        var fitter = GetOrAdd<ContentSizeFitter>(content);
        fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        var item = FindOrCreateChild(content.transform, "Item");
        GetOrAdd<LayoutElement>(item).preferredHeight = 42f;
        var itemImage = GetOrAdd<Image>(item);
        itemImage.color = new Color(0.10f, 0.13f, 0.16f, 1f);
        var toggle = GetOrAdd<Toggle>(item);
        toggle.targetGraphic = itemImage;
        var itemLabel = FindOrCreateText("Item Label", item.transform, font, fontSize);
        Stretch(itemLabel.rectTransform, 12f, 2f, 12f, 2f);
        itemLabel.alignment = TextAlignmentOptions.MidlineLeft;

        scroll.viewport = viewport.GetComponent<RectTransform>();
        scroll.content = contentRect;
        scroll.horizontal = false;
        scroll.vertical = true;
        dropdown.template = templateRect;
        dropdown.captionText = caption;
        dropdown.itemText = itemLabel;
        dropdown.options.Clear();
        foreach (var option in options) dropdown.options.Add(new TMP_Dropdown.OptionData(option));
        dropdown.value = Mathf.Clamp(value, 0, Mathf.Max(0, options.Length - 1));
        dropdown.RefreshShownValue();
        template.SetActive(false);
        return dropdown;
    }

    private static Button CreateButton(RectTransform parent, string name, string label, TMP_FontAsset font, float fontSize, float x, float y, float width, float height)
    {
        var root = FindOrCreateChild(parent, name);
        ConfigureTopLeftRect(root.GetComponent<RectTransform>(), x, y, width, height);
        var image = GetOrAdd<Image>(root);
        image.color = name == "EndButton" ? new Color(0.52f, 0.12f, 0.10f, 1f) : new Color(0.08f, 0.34f, 0.48f, 1f);
        var button = GetOrAdd<Button>(root);
        button.targetGraphic = image;
        var text = FindOrCreateText("Text", root.transform, font, fontSize);
        Stretch(text.rectTransform, 6f, 2f, 6f, 2f);
        text.text = label;
        text.alignment = TextAlignmentOptions.Center;
        return button;
    }

    private static void CreateLabel(RectTransform parent, string name, string value, TMP_FontAsset font, float fontSize, float x, float y, float width, float height)
    {
        var label = FindOrCreateText(name, parent, font, fontSize);
        ConfigureTopLeftRect(label.rectTransform, x, y, width, height);
        label.text = value;
        label.alignment = TextAlignmentOptions.MidlineLeft;
    }

    private static TMP_Text FindOrCreateText(string name, Transform parent, TMP_FontAsset font, float fontSize)
    {
        var direct = parent.Find(name);
        GameObject go;
        if (direct != null)
        {
            go = direct.gameObject;
        }
        else if (IsParticipantPresentationText(name))
        {
            go = GameObject.Find(name) ?? FindOrCreateChild(parent, name);
        }
        else
        {
            go = FindOrCreateChild(parent, name);
        }
        go.transform.SetParent(parent, false);
        var text = GetOrAdd<TextMeshProUGUI>(go);
        text.font = font;
        text.fontSize = fontSize;
        text.color = Color.white;
        text.enableWordWrapping = true;
        text.overflowMode = TextOverflowModes.Ellipsis;
        EditorUtility.SetDirty(text);
        return text;
    }

    private static bool IsParticipantPresentationText(string name)
    {
        return name == "GuidanceTextPanel_Text" || name == "MovementHintPanel_Text" || name == "GazeCuePanel_Text";
    }

    private static RectTransform ConfigurePanelRect(GameObject go, Vector2 anchorMin, Vector2 anchorMax, Vector2 pivot, Vector2 position, Vector2 size, Color color)
    {
        var rect = go.GetComponent<RectTransform>();
        SetRect(rect, anchorMin, anchorMax, pivot, position, size, Vector2.zero, Vector2.zero);
        GetOrAdd<Image>(go).color = color;
        return rect;
    }

    private static void ConfigureFullPanel(GameObject panel, Color color)
    {
        Stretch(panel.GetComponent<RectTransform>(), 0f, 0f, 0f, 0f);
        GetOrAdd<Image>(panel).color = color;
    }

    private static void ConfigureTopLeftRect(RectTransform rect, float x, float y, float width, float height)
    {
        SetRect(rect, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(x, y), new Vector2(width, height), Vector2.zero, Vector2.zero);
    }

    private static void Stretch(RectTransform rect, float left, float top, float right, float bottom)
    {
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.offsetMin = new Vector2(left, bottom);
        rect.offsetMax = new Vector2(-right, -top);
    }

    private static void SetRect(RectTransform rect, Vector2 anchorMin, Vector2 anchorMax, Vector2 pivot, Vector2 position, Vector2 size, Vector2 offsetMin, Vector2 offsetMax)
    {
        rect.anchorMin = anchorMin;
        rect.anchorMax = anchorMax;
        rect.pivot = pivot;
        rect.anchoredPosition = position;
        rect.sizeDelta = size;
        if (anchorMin != anchorMax)
        {
            rect.offsetMin = offsetMin;
            rect.offsetMax = offsetMax;
        }
        rect.localScale = Vector3.one;
    }

    private static GameObject FindOrCreateRoot(string name)
    {
        return GameObject.Find(name) ?? CreateObject(name, null);
    }

    private static GameObject FindOrCreateChild(Transform parent, string name)
    {
        var direct = parent.Find(name);
        return direct != null ? direct.gameObject : CreateObject(name, parent);
    }

    private static GameObject CreateObject(string name, Transform parent)
    {
        var go = new GameObject(name, typeof(RectTransform));
        Undo.RegisterCreatedObjectUndo(go, "Create " + name);
        if (parent != null) go.transform.SetParent(parent, false);
        return go;
    }

    private static T GetOrAdd<T>(GameObject go) where T : Component
    {
        var component = go.GetComponent<T>();
        return component != null ? component : Undo.AddComponent<T>(go);
    }

    private static T FindRequired<T>() where T : UnityEngine.Object
    {
        var found = UnityEngine.Object.FindObjectOfType<T>(true);
        if (found == null) throw new InvalidOperationException($"MainScene is missing required {typeof(T).Name}.");
        return found;
    }

    private static void RemoveComponent<T>(GameObject go) where T : Component
    {
        var component = go.GetComponent<T>();
        if (component != null) Undo.DestroyObjectImmediate(component);
    }

    private static void RemoveComponentByName(GameObject go, string typeName)
    {
        foreach (var component in go.GetComponents<Component>())
            if (component != null && component.GetType().Name == typeName) Undo.DestroyObjectImmediate(component);
    }

    private static void ClearChildren(Transform parent)
    {
        for (var i = parent.childCount - 1; i >= 0; i--) Undo.DestroyObjectImmediate(parent.GetChild(i).gameObject);
    }

    private static void SetLayerRecursively(GameObject root, int layer)
    {
        root.layer = layer;
        foreach (Transform child in root.transform) SetLayerRecursively(child.gameObject, layer);
    }

    private static int EnsureLayer(string layerName)
    {
        var existing = LayerMask.NameToLayer(layerName);
        if (existing >= 0) return existing;
        var tagManager = new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset")[0]);
        var layers = tagManager.FindProperty("layers");
        for (var i = 8; i < 32; i++)
        {
            var slot = layers.GetArrayElementAtIndex(i);
            if (!string.IsNullOrEmpty(slot.stringValue)) continue;
            slot.stringValue = layerName;
            tagManager.ApplyModifiedProperties();
            AssetDatabase.SaveAssets();
            return i;
        }
        throw new InvalidOperationException("No empty user layer slot is available.");
    }

    private static void SetObjectReference(UnityEngine.Object target, string propertyName, UnityEngine.Object value)
    {
        var serialized = new SerializedObject(target);
        var property = serialized.FindProperty(propertyName);
        if (property == null) throw new MissingFieldException(target.GetType().Name, propertyName);
        property.objectReferenceValue = value;
        serialized.ApplyModifiedProperties();
        EditorUtility.SetDirty(target);
    }

    private static void SetBool(UnityEngine.Object target, string propertyName, bool value)
    {
        var serialized = new SerializedObject(target);
        var property = serialized.FindProperty(propertyName);
        if (property == null) throw new MissingFieldException(target.GetType().Name, propertyName);
        property.boolValue = value;
        serialized.ApplyModifiedProperties();
        EditorUtility.SetDirty(target);
    }

    private static void SetCanvasGroup(CanvasGroup group, bool visible)
    {
        group.alpha = visible ? 1f : 0f;
        group.interactable = false;
        group.blocksRaycasts = false;
    }

    private static bool AllOnLayer(GameObject root, int layer)
    {
        if (root.layer != layer) return false;
        foreach (Transform child in root.transform)
            if (!AllOnLayer(child.gameObject, layer)) return false;
        return true;
    }

    private static bool GetSerializedBool(UnityEngine.Object target, string propertyName)
    {
        return new SerializedObject(target).FindProperty(propertyName).boolValue;
    }

    private static UnityEngine.Object GetSerializedReference(UnityEngine.Object target, string propertyName)
    {
        return new SerializedObject(target).FindProperty(propertyName).objectReferenceValue;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void ClearPersistent(UnityEventBase unityEvent)
    {
        while (unityEvent.GetPersistentEventCount() > 0) UnityEventTools.RemovePersistentListener(unityEvent, 0);
    }

    private readonly struct ParticipantBindings
    {
        public readonly GameObject canvasObject;
        public readonly ParticipantUiController controller;
        public readonly TMP_Text narration;
        public readonly TMP_Text action;
        public readonly TMP_Text gaze;

        public ParticipantBindings(GameObject canvasObject, ParticipantUiController controller, TMP_Text narration, TMP_Text action, TMP_Text gaze)
        {
            this.canvasObject = canvasObject;
            this.controller = controller;
            this.narration = narration;
            this.action = action;
            this.gaze = gaze;
        }
    }
}
