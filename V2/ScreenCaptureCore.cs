using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

/// <summary>Pure capture engine - no MonoBehaviour, no scene footprint. Everything the Screen
/// Capture Tool actually does (render a camera to a texture, encode, save) lives here so both
/// the Editor window and the optional runtime hotkey trigger call through the exact same code
/// path. Works with ANY Camera, including the Scene View's own camera (see
/// ScreenCaptureCore.GetSceneViewCamera) - "capturing the Scene View" really means "rendering
/// through the Scene View's camera", which gives the clean rendered scene without the Scene
/// View's own Editor-only gizmo/handle overlay (that overlay is drawn separately by SceneView's
/// IMGUI on top of the camera's render, and Camera.Render() never includes it).</summary>
public static class ScreenCaptureCore
{
    public struct CaptureResult
    {
        public bool success;
        public string path;
        public int width;
        public int height;
        public long fileSizeBytes;
        public string error;
    }

    public static CaptureResult Capture(Camera camera, ScreenCaptureSettings settings)
    {
        if (camera == null)
        {
            return new CaptureResult { success = false, error = "No camera available to capture from." };
        }
        if (settings == null)
        {
            return new CaptureResult { success = false, error = "No ScreenCaptureSettings assigned." };
        }

        Vector2Int baseRes = settings.GetResolution();
        int supersample = Mathf.Clamp(settings.supersampleMultiplier, 1, 4);
        int renderWidth = baseRes.x * supersample;
        int renderHeight = baseRes.y * supersample;
        bool isExr = settings.format == ScreenCaptureSettings.CaptureFormat.EXR;
        bool wantsAlpha = settings.transparentBackground && settings.format != ScreenCaptureSettings.CaptureFormat.JPG;

        List<Canvas> hiddenCanvases = settings.hideUIBeforeCapture ? HideAllCanvases() : null;

        CameraClearFlags prevClearFlags = camera.clearFlags;
        Color prevBackgroundColor = camera.backgroundColor;
        RenderTexture prevTargetTexture = camera.targetTexture;
        RenderTexture prevActive = RenderTexture.active;

        RenderTexture rt = null;
        Texture2D fullResTex = null;
        Texture2D finalTex = null;
        CaptureResult result = new CaptureResult();

        try
        {
            if (wantsAlpha)
            {
                camera.clearFlags = CameraClearFlags.SolidColor;
                Color bg = camera.backgroundColor;
                bg.a = 0f;
                camera.backgroundColor = bg;
            }

            RenderTextureFormat rtFormat = isExr ? RenderTextureFormat.ARGBHalf : RenderTextureFormat.ARGB32;
            rt = new RenderTexture(renderWidth, renderHeight, 24, rtFormat);
            rt.Create();

            camera.targetTexture = rt;
            RenderTexture.active = rt;

            // Render a few throw-away frames first so temporal effects (TAA, exposure adaptation,
            // SSR/SSGI/volumetric denoisers) converge before the frame we actually read pixels from -
            // otherwise the capture reflects a fresh/unconverged history and can look subtly different
            // from what's on screen after the view has settled.
            int totalRenders = Mathf.Max(1, settings.temporalSettleFrames + 1);
            for (int i = 0; i < totalRenders; i++)
            {
                camera.Render();
            }

            TextureFormat texFormat = isExr
                ? TextureFormat.RGBAHalf
                : (wantsAlpha ? TextureFormat.RGBA32 : TextureFormat.RGB24);

            fullResTex = new Texture2D(renderWidth, renderHeight, texFormat, false);
            fullResTex.ReadPixels(new Rect(0, 0, renderWidth, renderHeight), 0, 0);
            fullResTex.Apply();

            camera.targetTexture = prevTargetTexture;
            RenderTexture.active = prevActive;
            rt.Release();
            UnityEngine.Object.DestroyImmediate(rt);
            rt = null;

            finalTex = fullResTex;
            if (supersample > 1)
            {
                finalTex = Downsample(fullResTex, baseRes.x, baseRes.y, isExr);
                UnityEngine.Object.DestroyImmediate(fullResTex);
                fullResTex = null;
            }

            byte[] bytes;
            string extension;
            switch (settings.format)
            {
                case ScreenCaptureSettings.CaptureFormat.JPG:
                    bytes = finalTex.EncodeToJPG(Mathf.Clamp(settings.jpgQuality, 1, 100));
                    extension = "jpg";
                    break;
                case ScreenCaptureSettings.CaptureFormat.EXR:
                    bytes = finalTex.EncodeToEXR(Texture2D.EXRFlags.CompressZIP);
                    extension = "exr";
                    break;
                default:
                    bytes = finalTex.EncodeToPNG();
                    extension = "png";
                    break;
            }

            string folder = settings.GetSaveFolder();
            if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);

            string fileName = BuildFileName(settings.filenameTemplate, baseRes, extension);
            string fullPath = Path.Combine(folder, fileName);
            File.WriteAllBytes(fullPath, bytes);

            result.success = true;
            result.path = fullPath;
            result.width = baseRes.x;
            result.height = baseRes.y;
            result.fileSizeBytes = bytes.LongLength;
        }
        catch (Exception e)
        {
            result.success = false;
            result.error = e.Message;
        }
        finally
        {
            camera.clearFlags = prevClearFlags;
            camera.backgroundColor = prevBackgroundColor;
            camera.targetTexture = prevTargetTexture;
            RenderTexture.active = prevActive;

            if (rt != null)
            {
                rt.Release();
                UnityEngine.Object.DestroyImmediate(rt);
            }
            if (fullResTex != null) UnityEngine.Object.DestroyImmediate(fullResTex);
            if (finalTex != null) UnityEngine.Object.DestroyImmediate(finalTex);

            if (hiddenCanvases != null) RestoreCanvases(hiddenCanvases);
        }

        return result;
    }

#if UNITY_EDITOR
    /// <summary>The camera actually driving the currently-focused Scene View, or null if no
    /// Scene View is open. Pass this into Capture() to "capture the Scene View".</summary>
    public static Camera GetSceneViewCamera()
    {
        var sceneView = SceneView.lastActiveSceneView;
        return sceneView != null ? sceneView.camera : null;
    }
#endif

    /// <summary>Blits source down to the target resolution through an intermediate RenderTexture
    /// matching source's precision (half-float for EXR captures, standard ARGB32 otherwise) so
    /// supersampling doesn't quietly truncate HDR precision back to 8-bit along the way.</summary>
    private static Texture2D Downsample(Texture2D source, int targetWidth, int targetHeight, bool isExr)
    {
        RenderTextureFormat rtFormat = isExr ? RenderTextureFormat.ARGBHalf : RenderTextureFormat.ARGB32;
        RenderTexture rt = RenderTexture.GetTemporary(targetWidth, targetHeight, 0, rtFormat);
        rt.filterMode = FilterMode.Bilinear;
        RenderTexture prevActive = RenderTexture.active;

        RenderTexture.active = rt;
        Graphics.Blit(source, rt);

        Texture2D result = new Texture2D(targetWidth, targetHeight, source.format, false);
        result.ReadPixels(new Rect(0, 0, targetWidth, targetHeight), 0, 0);
        result.Apply();

        RenderTexture.active = prevActive;
        RenderTexture.ReleaseTemporary(rt);
        return result;
    }

    private static string BuildFileName(string template, Vector2Int resolution, string extension)
    {
        string sceneName = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
        if (string.IsNullOrEmpty(sceneName)) sceneName = "Untitled";
        DateTime now = DateTime.Now;

        string name = string.IsNullOrEmpty(template) ? "Screenshot" : template;
        name = name.Replace("{scene}", SanitizeFileName(sceneName))
                    .Replace("{date}", now.ToString("yyyy-MM-dd"))
                    .Replace("{time}", now.ToString("HH-mm-ss"))
                    .Replace("{resolution}", $"{resolution.x}x{resolution.y}");

        if (string.IsNullOrEmpty(name)) name = "Screenshot";
        return name + "." + extension;
    }

    private static string SanitizeFileName(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name;
    }

    private static List<Canvas> HideAllCanvases()
    {
        var canvases = UnityEngine.Object.FindObjectsOfType<Canvas>();
        var hidden = new List<Canvas>();
        foreach (var c in canvases)
        {
            if (c != null && c.enabled)
            {
                c.enabled = false;
                hidden.Add(c);
            }
        }
        return hidden;
    }

    private static void RestoreCanvases(List<Canvas> canvases)
    {
        foreach (var c in canvases)
        {
            if (c != null) c.enabled = true;
        }
    }
}
