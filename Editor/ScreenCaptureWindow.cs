// ScreenCaptureWindow.cs
// Put this file under an "Editor" folder.
// Full replacement for the old HighResScreenshot tool - see Assets/ScreenShot/ScreenCaptureCore.cs
// for the actual capture logic and Assets/ScreenShot/ScreenCaptureSettings.cs for persisted config.

using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

public class ScreenCaptureWindow : EditorWindow
{
    private ScreenCaptureSettings settings;
    private SerializedObject settingsSO;

    private Camera[] availableCameras = new Camera[0];
    private string[] cameraNames = new string[0];
    private int selectedCameraIndex;
    private bool useSceneView;

    private Texture2D lastThumbnail;
    private string lastStatus = "";
    private bool lastWasError;

    private const int HistoryCapacity = 5;
    private readonly List<string> historyPaths = new List<string>();
    private readonly List<Texture2D> historyThumbs = new List<Texture2D>();

    // --- Image Library: browse/select/open/delete existing captures in the save folder ---
    private static readonly string[] LibraryExtensions = { ".png", ".jpg", ".jpeg", ".exr" };
    private readonly List<string> libraryFiles = new List<string>();
    private readonly HashSet<string> librarySelected = new HashSet<string>();
    private readonly Dictionary<string, Texture2D> libraryThumbCache = new Dictionary<string, Texture2D>();
    private Vector2 libraryScroll;
    private const int LibraryThumbSize = 48;

    // --- Extreme-operation safety thresholds (total rendered pixel count) ---
    private const long WarningPixelCount = 50_000_000L;  // e.g. Ultra 4K x3 supersampling
    private const long DangerPixelCount = 100_000_000L;  // e.g. Ultra 4K x4 supersampling

    private Vector2 scroll;

    [MenuItem("Tools/Screen Capture Tool %&p")]
    public static void Open()
    {
        var window = GetWindow<ScreenCaptureWindow>("Screen Capture");
        window.minSize = new Vector2(380, 560);
        window.Show();
    }

    private void OnEnable()
    {
        LoadSettings();
        RefreshCameraList();
        RefreshLibrary();
    }

    private void OnFocus()
    {
        RefreshCameraList();
        RefreshLibrary();
    }

    private void LoadSettings()
    {
        var guids = AssetDatabase.FindAssets("t:ScreenCaptureSettings");
        if (guids.Length > 0)
        {
            settings = AssetDatabase.LoadAssetAtPath<ScreenCaptureSettings>(AssetDatabase.GUIDToAssetPath(guids[0]));
        }

        if (settings == null)
        {
            settings = CreateInstance<ScreenCaptureSettings>();
            if (!AssetDatabase.IsValidFolder("Assets/ScreenShot"))
                AssetDatabase.CreateFolder("Assets", "ScreenShot");
            AssetDatabase.CreateAsset(settings, "Assets/ScreenShot/ScreenCaptureSettings.asset");
            AssetDatabase.SaveAssets();
        }

        settingsSO = new SerializedObject(settings);
        useSceneView = settings.defaultSource == ScreenCaptureSettings.CaptureSource.SceneView;
    }

    private void RefreshCameraList()
    {
        var cameras = FindObjectsOfType<Camera>();
        availableCameras = cameras;
        cameraNames = new string[cameras.Length];
        selectedCameraIndex = 0;

        for (int i = 0; i < cameras.Length; i++)
        {
            cameraNames[i] = cameras[i] != null ? cameras[i].gameObject.name : "(missing)";
            if (cameras[i] == Camera.main) selectedCameraIndex = i;
        }
    }

    private void OnGUI()
    {
        if (settings == null || settingsSO == null || settingsSO.targetObject == null)
        {
            LoadSettings();
        }

        settingsSO.Update();

        scroll = EditorGUILayout.BeginScrollView(scroll);

        DrawHeader();
        EditorGUILayout.Space(6);
        DrawSourceSection();
        EditorGUILayout.Space(8);
        DrawQualitySection();
        EditorGUILayout.Space(8);
        DrawFormatSection();
        EditorGUILayout.Space(8);
        DrawOutputSection();
        EditorGUILayout.Space(10);
        DrawActions();
        EditorGUILayout.Space(10);
        DrawStatus();
        EditorGUILayout.Space(8);
        DrawPreviewAndHistory();
        EditorGUILayout.Space(10);
        DrawLibrarySection();

        EditorGUILayout.EndScrollView();

        settingsSO.ApplyModifiedProperties();
    }

    private void DrawHeader()
    {
        EditorGUILayout.LabelField("Screen Capture Tool", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "Capture the Game view (any camera) or the Scene view, at any quality from Low to Ultra 4K, " +
            "with no scene setup required. Settings are saved to ScreenCaptureSettings.asset.",
            MessageType.Info);
    }

    private void DrawSourceSection()
    {
        EditorGUILayout.LabelField("Capture Source", EditorStyles.boldLabel);
        using (new EditorGUILayout.VerticalScope("box"))
        {
            useSceneView = GUILayout.Toolbar(useSceneView ? 1 : 0, new[] { "Game Camera", "Scene View" }) == 1;

            using (new EditorGUI.DisabledScope(useSceneView))
            {
                if (availableCameras.Length == 0)
                {
                    EditorGUILayout.HelpBox("No cameras found in the open scene(s).", MessageType.Warning);
                }
                else
                {
                    selectedCameraIndex = EditorGUILayout.Popup("Camera", Mathf.Clamp(selectedCameraIndex, 0, availableCameras.Length - 1), cameraNames);
                }
                if (GUILayout.Button("Refresh Camera List", GUILayout.Width(150)))
                {
                    RefreshCameraList();
                }
            }

            if (useSceneView && ScreenCaptureCore.GetSceneViewCamera() == null)
            {
                EditorGUILayout.HelpBox("No Scene View is currently open/focused.", MessageType.Warning);
            }
        }
    }

    private void DrawQualitySection()
    {
        EditorGUILayout.LabelField("Quality", EditorStyles.boldLabel);
        using (new EditorGUILayout.VerticalScope("box"))
        {
            EditorGUILayout.PropertyField(settingsSO.FindProperty("qualityPreset"), new GUIContent("Preset"));

            var preset = settings.qualityPreset;
            if (preset == ScreenCaptureSettings.QualityPreset.Custom)
            {
                EditorGUILayout.PropertyField(settingsSO.FindProperty("customWidth"), new GUIContent("Width"));
                EditorGUILayout.PropertyField(settingsSO.FindProperty("customHeight"), new GUIContent("Height"));
            }

            Vector2Int res = settings.GetResolution();
            EditorGUILayout.LabelField("Output Resolution", $"{res.x} x {res.y}");

            EditorGUILayout.PropertyField(settingsSO.FindProperty("supersampleMultiplier"), new GUIContent("Supersampling"));
            if (settings.supersampleMultiplier > 1)
            {
                Vector2Int renderRes = res * settings.supersampleMultiplier;
                EditorGUILayout.HelpBox($"Renders at {renderRes.x} x {renderRes.y} then downsamples to {res.x} x {res.y}.", MessageType.None);
            }

            string extremeWarning = DescribeExtremeWarning(res, settings.supersampleMultiplier,
                settings.format == ScreenCaptureSettings.CaptureFormat.EXR, out MessageType extremeSeverity);
            if (extremeWarning != null)
            {
                EditorGUILayout.HelpBox(extremeWarning, extremeSeverity);
            }

            EditorGUILayout.PropertyField(settingsSO.FindProperty("temporalSettleFrames"), new GUIContent("Temporal Settle Frames"));
        }
    }

    private static long ComputePixelCount(Vector2Int baseRes, int supersample)
    {
        long w = (long)baseRes.x * supersample;
        long h = (long)baseRes.y * supersample;
        return w * h;
    }

    private static string DescribeExtremeWarning(Vector2Int baseRes, int supersample, bool isExr, out MessageType severity)
    {
        long renderW = (long)baseRes.x * supersample;
        long renderH = (long)baseRes.y * supersample;
        long pixels = renderW * renderH;
        int bytesPerPixel = isExr ? 8 : 4;
        double estimatedMB = pixels * bytesPerPixel * 2.0 / (1024 * 1024); // capture RT + CPU readback, rough estimate

        if (pixels >= DangerPixelCount)
        {
            severity = MessageType.Error;
            return $"EXTREME: this will render at {renderW}x{renderH} (~{pixels / 1_000_000f:0.#} MP, ~{estimatedMB:0} MB estimated). " +
                   "This can hang or crash the Editor or GPU driver depending on available VRAM. Consider lowering Supersampling or the resolution.";
        }
        if (pixels >= WarningPixelCount)
        {
            severity = MessageType.Warning;
            return $"Heavy capture: rendering at {renderW}x{renderH} (~{pixels / 1_000_000f:0.#} MP, ~{estimatedMB:0} MB estimated). " +
                   "This may be slow or use significant GPU memory.";
        }
        severity = MessageType.None;
        return null;
    }

    private bool ConfirmIfExtreme(Vector2Int baseRes, int supersample)
    {
        long pixels = ComputePixelCount(baseRes, supersample);
        if (pixels < DangerPixelCount) return true;

        long renderW = (long)baseRes.x * supersample;
        long renderH = (long)baseRes.y * supersample;
        return EditorUtility.DisplayDialog(
            "Extreme Capture Resolution",
            $"This will render at {renderW}x{renderH} (~{pixels / 1_000_000f:0.#} megapixels). " +
            "This can hang or crash the Editor or GPU driver depending on available VRAM.\n\nContinue anyway?",
            "Capture Anyway", "Cancel");
    }

    private void DrawFormatSection()
    {
        EditorGUILayout.LabelField("Format", EditorStyles.boldLabel);
        using (new EditorGUILayout.VerticalScope("box"))
        {
            EditorGUILayout.PropertyField(settingsSO.FindProperty("format"), new GUIContent("Format"));

            if (settings.format == ScreenCaptureSettings.CaptureFormat.JPG)
            {
                EditorGUILayout.PropertyField(settingsSO.FindProperty("jpgQuality"), new GUIContent("JPG Quality"));
            }

            using (new EditorGUI.DisabledScope(settings.format == ScreenCaptureSettings.CaptureFormat.JPG))
            {
                EditorGUILayout.PropertyField(settingsSO.FindProperty("transparentBackground"), new GUIContent("Transparent Background"));
            }
            if (settings.format == ScreenCaptureSettings.CaptureFormat.JPG && settings.transparentBackground)
            {
                EditorGUILayout.HelpBox("JPG has no alpha channel - Transparent Background is ignored for this format.", MessageType.None);
            }

            EditorGUILayout.PropertyField(settingsSO.FindProperty("hideUIBeforeCapture"), new GUIContent("Hide UI (Canvases) Before Capture"));
        }
    }

    private void DrawOutputSection()
    {
        EditorGUILayout.LabelField("Output", EditorStyles.boldLabel);
        using (new EditorGUILayout.VerticalScope("box"))
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.PropertyField(settingsSO.FindProperty("saveFolder"), new GUIContent("Save Folder"));
                if (GUILayout.Button("Browse", GUILayout.Width(60)))
                {
                    string chosen = EditorUtility.OpenFolderPanel("Choose Screenshot Folder", settings.GetSaveFolder(), "");
                    if (!string.IsNullOrEmpty(chosen))
                    {
                        settingsSO.FindProperty("saveFolder").stringValue = chosen;
                    }
                }
            }
            EditorGUILayout.LabelField("Resolved Path", settings.GetSaveFolder(), EditorStyles.miniLabel);

            EditorGUILayout.PropertyField(settingsSO.FindProperty("filenameTemplate"), new GUIContent("Filename Template"));
            EditorGUILayout.HelpBox("Tokens: {scene} {date} {time} {resolution}", MessageType.None);

            if (GUILayout.Button("Open Save Folder", GUILayout.Width(150)))
            {
                string folder = settings.GetSaveFolder();
                if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);
                EditorUtility.RevealInFinder(folder);
            }
        }
    }

    private void DrawActions()
    {
        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Capture", GUILayout.Height(32)))
            {
                DoCapture();
            }
            if (GUILayout.Button("Batch Capture (All Presets)", GUILayout.Height(32)))
            {
                DoBatchCapture();
            }
        }
    }

    private void DrawStatus()
    {
        if (string.IsNullOrEmpty(lastStatus)) return;
        EditorGUILayout.HelpBox(lastStatus, lastWasError ? MessageType.Error : MessageType.Info);
    }

    private void DrawPreviewAndHistory()
    {
        if (lastThumbnail != null)
        {
            EditorGUILayout.LabelField("Last Capture", EditorStyles.boldLabel);
            float w = Mathf.Min(position.width - 20f, 320f);
            float h = w * lastThumbnail.height / Mathf.Max(1, lastThumbnail.width);
            Rect r = GUILayoutUtility.GetRect(w, h, GUILayout.ExpandWidth(false));
            GUI.DrawTexture(r, lastThumbnail, ScaleMode.ScaleToFit);
        }

        if (historyPaths.Count > 0)
        {
            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("Recent Captures", EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                for (int i = 0; i < historyThumbs.Count; i++)
                {
                    if (historyThumbs[i] == null) continue;
                    if (GUILayout.Button(historyThumbs[i], GUILayout.Width(56), GUILayout.Height(56)))
                    {
                        EditorUtility.RevealInFinder(historyPaths[i]);
                    }
                }
            }
        }
    }

    private Camera ResolveCaptureCamera()
    {
        if (useSceneView)
        {
            return ScreenCaptureCore.GetSceneViewCamera();
        }
        if (availableCameras != null && availableCameras.Length > 0)
        {
            int index = Mathf.Clamp(selectedCameraIndex, 0, availableCameras.Length - 1);
            return availableCameras[index];
        }
        return Camera.main;
    }

    private void DoCapture()
    {
        if (!ConfirmIfExtreme(settings.GetResolution(), settings.supersampleMultiplier)) return;

        Camera cam = ResolveCaptureCamera();
        var result = ScreenCaptureCore.Capture(cam, settings);
        HandleResult(result);
    }

    private void DoBatchCapture()
    {
        if (!ConfirmIfExtreme(ScreenCaptureSettings.UltraRes, settings.supersampleMultiplier)) return;

        Camera cam = ResolveCaptureCamera();
        var presets = new[]
        {
            ScreenCaptureSettings.QualityPreset.Low,
            ScreenCaptureSettings.QualityPreset.Medium,
            ScreenCaptureSettings.QualityPreset.High,
            ScreenCaptureSettings.QualityPreset.Ultra4K,
        };

        var originalPreset = settings.qualityPreset;
        int successCount = 0;
        foreach (var preset in presets)
        {
            settings.qualityPreset = preset;
            var result = ScreenCaptureCore.Capture(cam, settings);
            if (result.success) successCount++;
            HandleResult(result);
        }
        settings.qualityPreset = originalPreset;
        EditorUtility.SetDirty(settings);

        lastStatus = $"Batch capture finished: {successCount}/{presets.Length} succeeded.";
        lastWasError = successCount < presets.Length;
        Repaint();
    }

    private void HandleResult(ScreenCaptureCore.CaptureResult result)
    {
        if (result.success)
        {
            lastWasError = false;
            lastStatus = $"Saved {result.width}x{result.height} ({FormatBytes(result.fileSizeBytes)}) to:\n{result.path}";
            LoadThumbnail(result.path);
            AddToHistory(result.path);
            RefreshLibrary();
        }
        else
        {
            lastWasError = true;
            lastStatus = "Capture failed: " + result.error;
            Debug.LogError("[ScreenCaptureTool] " + result.error);
        }
        Repaint();
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB" };
        double size = bytes;
        int unitIndex = 0;
        while (size >= 1024.0 && unitIndex < units.Length - 1)
        {
            size /= 1024.0;
            unitIndex++;
        }
        return $"{size:0.##} {units[unitIndex]}";
    }

    private void LoadThumbnail(string path)
    {
        Texture2D tex = TryLoadPreviewTexture(path);
        if (tex != null)
        {
            if (lastThumbnail != null) Object.DestroyImmediate(lastThumbnail);
            lastThumbnail = tex;
        }
    }

    private Texture2D TryLoadPreviewTexture(string path)
    {
        // LoadImage only understands PNG/JPG - EXR has no in-Editor preview here, which is fine,
        // the status line above still confirms the save.
        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext != ".png" && ext != ".jpg" && ext != ".jpeg") return null;

        try
        {
            byte[] bytes = File.ReadAllBytes(path);
            Texture2D tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (tex.LoadImage(bytes))
            {
                return tex;
            }
            Object.DestroyImmediate(tex);
        }
        catch (System.Exception e)
        {
            Debug.LogWarning("[ScreenCaptureTool] Could not load preview thumbnail: " + e.Message);
        }
        return null;
    }

    private void AddToHistory(string path)
    {
        historyPaths.Insert(0, path);
        Texture2D thumb = TryLoadPreviewTexture(path);
        historyThumbs.Insert(0, thumb);

        while (historyPaths.Count > HistoryCapacity)
        {
            int last = historyPaths.Count - 1;
            if (historyThumbs[last] != null) Object.DestroyImmediate(historyThumbs[last]);
            historyPaths.RemoveAt(last);
            historyThumbs.RemoveAt(last);
        }
    }

    // --- Image Library ---

    private void RefreshLibrary()
    {
        libraryFiles.Clear();

        string folder = settings != null ? settings.GetSaveFolder() : null;
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
        {
            librarySelected.Clear();
            return;
        }

        var found = new List<string>();
        foreach (var file in Directory.GetFiles(folder, "*", SearchOption.TopDirectoryOnly))
        {
            string ext = Path.GetExtension(file).ToLowerInvariant();
            if (System.Array.IndexOf(LibraryExtensions, ext) >= 0) found.Add(file);
        }
        found.Sort((a, b) => File.GetLastWriteTime(b).CompareTo(File.GetLastWriteTime(a)));
        libraryFiles.AddRange(found);

        librarySelected.IntersectWith(libraryFiles);
    }

    private void DrawLibrarySection()
    {
        EditorGUILayout.LabelField("Image Library", EditorStyles.boldLabel);
        using (new EditorGUILayout.VerticalScope("box"))
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField($"{libraryFiles.Count} image(s) in save folder", EditorStyles.miniLabel);
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Refresh", GUILayout.Width(70))) RefreshLibrary();
            }

            if (libraryFiles.Count == 0)
            {
                EditorGUILayout.HelpBox("No captures found in the save folder yet.", MessageType.None);
                return;
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Select All", GUILayout.Width(80)))
                {
                    librarySelected.Clear();
                    librarySelected.UnionWith(libraryFiles);
                }
                if (GUILayout.Button("Deselect All", GUILayout.Width(90)))
                {
                    librarySelected.Clear();
                }
                GUILayout.FlexibleSpace();
                EditorGUILayout.LabelField($"{librarySelected.Count} selected", EditorStyles.miniLabel, GUILayout.Width(80));
            }

            using (new EditorGUI.DisabledScope(librarySelected.Count == 0))
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Open Selected", GUILayout.Height(24)))
                {
                    OpenSelectedLibraryFiles();
                }
                Color prevColor = GUI.backgroundColor;
                GUI.backgroundColor = new Color(1f, 0.55f, 0.55f);
                if (GUILayout.Button("Delete Selected", GUILayout.Height(24)))
                {
                    DeleteSelectedLibraryFiles();
                }
                GUI.backgroundColor = prevColor;
            }

            EditorGUILayout.Space(4);
            libraryScroll = EditorGUILayout.BeginScrollView(libraryScroll, GUILayout.Height(Mathf.Min(280, libraryFiles.Count * 30 + 10)));
            for (int i = 0; i < libraryFiles.Count; i++)
            {
                string path = libraryFiles[i];
                using (new EditorGUILayout.HorizontalScope())
                {
                    bool isSelected = librarySelected.Contains(path);
                    bool newSelected = EditorGUILayout.ToggleLeft(GUIContent.none, isSelected, GUILayout.Width(20));
                    if (newSelected != isSelected)
                    {
                        if (newSelected) librarySelected.Add(path);
                        else librarySelected.Remove(path);
                    }

                    Texture2D thumb = LoadLibraryThumbnail(path);
                    Rect thumbRect = GUILayoutUtility.GetRect(LibraryThumbSize, LibraryThumbSize, GUILayout.Width(LibraryThumbSize), GUILayout.Height(LibraryThumbSize));
                    if (thumb != null) GUI.DrawTexture(thumbRect, thumb, ScaleMode.ScaleToFit);
                    else EditorGUI.DrawRect(thumbRect, new Color(0f, 0f, 0f, 0.15f));

                    using (new EditorGUILayout.VerticalScope())
                    {
                        EditorGUILayout.LabelField(Path.GetFileName(path), EditorStyles.miniBoldLabel);
                        EditorGUILayout.LabelField($"{FormatBytes(SafeFileLength(path))}  ·  {File.GetLastWriteTime(path):yyyy-MM-dd HH:mm}", EditorStyles.miniLabel);
                    }
                }
            }
            EditorGUILayout.EndScrollView();
        }
    }

    private static long SafeFileLength(string path)
    {
        try { return new FileInfo(path).Length; }
        catch { return 0; }
    }

    private Texture2D LoadLibraryThumbnail(string path)
    {
        if (libraryThumbCache.TryGetValue(path, out var cached)) return cached;

        Texture2D thumb = null;
        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext == ".png" || ext == ".jpg" || ext == ".jpeg")
        {
            try
            {
                byte[] bytes = File.ReadAllBytes(path);
                Texture2D full = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (full.LoadImage(bytes))
                {
                    thumb = DownsizeThumbnail(full, LibraryThumbSize);
                }
                Object.DestroyImmediate(full);
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("[ScreenCaptureTool] Could not load library thumbnail for " + path + ": " + e.Message);
            }
        }

        libraryThumbCache[path] = thumb;
        return thumb;
    }

    private static Texture2D DownsizeThumbnail(Texture2D source, int maxSize)
    {
        int w = source.width, h = source.height;
        float scale = Mathf.Min(1f, (float)maxSize / Mathf.Max(w, h));
        int targetW = Mathf.Max(1, Mathf.RoundToInt(w * scale));
        int targetH = Mathf.Max(1, Mathf.RoundToInt(h * scale));

        RenderTexture rt = RenderTexture.GetTemporary(targetW, targetH, 0, RenderTextureFormat.ARGB32);
        RenderTexture prevActive = RenderTexture.active;
        RenderTexture.active = rt;
        Graphics.Blit(source, rt);

        Texture2D result = new Texture2D(targetW, targetH, TextureFormat.RGBA32, false);
        result.ReadPixels(new Rect(0, 0, targetW, targetH), 0, 0);
        result.Apply();

        RenderTexture.active = prevActive;
        RenderTexture.ReleaseTemporary(rt);
        return result;
    }

    private void OpenSelectedLibraryFiles()
    {
        foreach (var path in librarySelected)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("[ScreenCaptureTool] Could not open " + path + ": " + e.Message);
            }
        }
    }

    private void DeleteSelectedLibraryFiles()
    {
        int count = librarySelected.Count;
        if (count == 0) return;

        bool confirmed = EditorUtility.DisplayDialog(
            "Delete Captures",
            $"Permanently delete {count} selected image(s)? This cannot be undone.",
            "Delete", "Cancel");
        if (!confirmed) return;

        int deleted = 0;
        bool touchedAssets = false;
        foreach (var path in new List<string>(librarySelected))
        {
            try
            {
                File.Delete(path);
                deleted++;
                if (path.Replace('\\', '/').Contains("/Assets/")) touchedAssets = true;

                string metaPath = path + ".meta";
                if (File.Exists(metaPath)) File.Delete(metaPath);
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("[ScreenCaptureTool] Could not delete " + path + ": " + e.Message);
            }

            if (libraryThumbCache.TryGetValue(path, out var tex) && tex != null)
            {
                Object.DestroyImmediate(tex);
            }
            libraryThumbCache.Remove(path);
        }

        librarySelected.Clear();
        RefreshLibrary();
        if (touchedAssets) AssetDatabase.Refresh();

        lastStatus = $"Deleted {deleted}/{count} image(s).";
        lastWasError = deleted < count;
        Repaint();
    }

    private void OnDestroy()
    {
        if (lastThumbnail != null) Object.DestroyImmediate(lastThumbnail);
        foreach (var t in historyThumbs)
        {
            if (t != null) Object.DestroyImmediate(t);
        }
        foreach (var kv in libraryThumbCache)
        {
            if (kv.Value != null) Object.DestroyImmediate(kv.Value);
        }
    }
}
