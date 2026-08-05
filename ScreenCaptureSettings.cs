using UnityEngine;

/// <summary>Persisted defaults for the Screen Capture Tool (Tools > Screen Capture Tool). One
/// shared asset, auto-created/located by ScreenCaptureWindow if missing - matches this project's
/// own established [CreateAssetMenu] settings-asset convention (e.g. CameraShakeSettings,
/// AimFeedbackSettings), so defaults survive Editor restarts and can be shared across the team.</summary>
[CreateAssetMenu(fileName = "ScreenCaptureSettings", menuName = "Tools/Screen Capture Settings")]
public class ScreenCaptureSettings : ScriptableObject
{
    public enum QualityPreset { Low, Medium, High, Ultra4K, Custom }
    public enum CaptureFormat { PNG, JPG, EXR }
    public enum CaptureSource { GameCamera, SceneView }

    public static readonly Vector2Int LowRes = new Vector2Int(960, 540);
    public static readonly Vector2Int MediumRes = new Vector2Int(1280, 720);
    public static readonly Vector2Int HighRes = new Vector2Int(1920, 1080);
    public static readonly Vector2Int UltraRes = new Vector2Int(3840, 2160);

    [Header("Resolution")]
    [Tooltip("Low = 960x540, Medium = 1280x720, High = 1920x1080, Ultra 4K = 3840x2160, Custom = the width/height below.")]
    public QualityPreset qualityPreset = QualityPreset.High;
    [Tooltip("Used only when Quality Preset is Custom.")]
    public int customWidth = 1920;
    [Tooltip("Used only when Quality Preset is Custom.")]
    public int customHeight = 1080;
    [Range(1, 4)]
    [Tooltip("Renders at this multiple of the target resolution, then downsamples - cleaner edges than capturing at native resolution alone. 1 = no supersampling.")]
    public int supersampleMultiplier = 1;

    [Header("Format")]
    public CaptureFormat format = CaptureFormat.PNG;
    [Range(1, 100)]
    [Tooltip("Only used when Format is JPG.")]
    public int jpgQuality = 90;
    [Tooltip("Captures with a transparent background instead of whatever the camera would normally clear to. Not available for JPG (no alpha channel support) - ignored if Format is JPG.")]
    public bool transparentBackground = false;

    [Header("Scene")]
    [Tooltip("Temporarily disables every active Canvas in the scene for the duration of the capture, then restores them - use for clean shots without HUD/UI.")]
    public bool hideUIBeforeCapture = false;

    [Range(0, 16)]
    [Tooltip("Renders this many extra throw-away frames into the capture target immediately before reading pixels, letting temporal effects (TAA, auto-exposure adaptation, SSR/SSGI/volumetric denoisers) converge to match what you see after the Scene/Game view has settled. 0 captures on the very first frame, which can look slightly softer or less exposed than the converged view.")]
    public int temporalSettleFrames = 4;

    [Header("Output")]
    [Tooltip("Folder to save captures to. Leave empty to use <ProjectRoot>/Screenshots.")]
    public string saveFolder = "";
    [Tooltip("Filename (without extension) - supports {scene}, {date}, {time}, {resolution} tokens.")]
    public string filenameTemplate = "{scene}_{date}_{time}_{resolution}";

    [Header("Capture Source")]
    [Tooltip("Which capture source the tool window opens with by default.")]
    public CaptureSource defaultSource = CaptureSource.GameCamera;

    public Vector2Int GetResolution()
    {
        switch (qualityPreset)
        {
            case QualityPreset.Low: return LowRes;
            case QualityPreset.Medium: return MediumRes;
            case QualityPreset.High: return HighRes;
            case QualityPreset.Ultra4K: return UltraRes;
            case QualityPreset.Custom: return new Vector2Int(Mathf.Max(1, customWidth), Mathf.Max(1, customHeight));
            default: return HighRes;
        }
    }

    public string GetSaveFolder()
    {
        if (!string.IsNullOrEmpty(saveFolder)) return saveFolder;
        return System.IO.Path.Combine(Application.dataPath, "..", "Screenshots");
    }
}
