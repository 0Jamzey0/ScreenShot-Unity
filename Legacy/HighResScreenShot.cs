using UnityEngine;
using System.IO;

public class HighResScreenshot : MonoBehaviour
{
    public enum Resolution
    {
        HD,FHD,QHD,UHD4K
    }

    public Camera targetCamera; // Assign your camera in the inspector
    private int resolutionWidth = 3840; // 4K width
    private int resolutionHeight = 2160; // 4K height
    public Resolution resolution = Resolution.FHD;
    public string outputFileName = "Screenshot.png";
    public bool CaptureAtStart = false;
    void GetResolution(Resolution res)
    {
        if (res == Resolution.HD)
        {
            resolutionWidth = 1280;
            resolutionHeight = 720;
        }
        else if (res == Resolution.FHD)
        {
            resolutionWidth = 1920;
            resolutionHeight = 1080;
        }
        else if (res == Resolution.QHD)
        {
            resolutionWidth = 2048;
            resolutionHeight = 1152;
        }
        else
        {
            resolutionWidth =  3840;
            resolutionHeight = 2160;
        }
    }

    public void CaptureScreenshot()
    {
        GetResolution(resolution);
        RenderTexture rt = new RenderTexture(resolutionWidth, resolutionHeight, 24);
        targetCamera.targetTexture = rt;
        
        Texture2D screenshot = new Texture2D(resolutionWidth, resolutionHeight, TextureFormat.RGBA32, false);
        targetCamera.Render();
        RenderTexture.active = rt;
        screenshot.ReadPixels(new Rect(0, 0, resolutionWidth, resolutionHeight), 0, 0);
        screenshot.Apply();
        
        targetCamera.targetTexture = null;
        RenderTexture.active = null;
        Destroy(rt);
        byte[] bytes = screenshot.EncodeToPNG();
        string fullPath = Path.Combine(Application.dataPath, outputFileName);
        File.WriteAllBytes(fullPath, bytes);

        Debug.Log("Saved screenshot to: " + fullPath);
    }

    void Start()
    {
        if(CaptureAtStart)
        CaptureScreenshot();
    }
    
    void Update()
    {
        if (Input.GetKeyDown(KeyCode.P))
        {
            CaptureScreenshot();
        }
    }
}