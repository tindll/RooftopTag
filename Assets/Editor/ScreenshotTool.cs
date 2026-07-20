#nullable enable

using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace Game.EditorTools;

/// <summary>
/// Headless screenshot capture for the visual-review loop: opens each scene in edit mode,
/// renders fixed vantage shots to Tools/screenshots/. Run with -batchmode but WITHOUT
/// -nographics (rendering needs a GPU context):
/// Unity.exe -batchmode -quit -projectPath . -executeMethod Game.EditorTools.ScreenshotTool.CaptureAll -logFile Tools/shots.log
/// </summary>
public static class ScreenshotTool
{
    private static readonly (string scenePath, Vector3 pos, Vector3 lookAt)[] Shots =
    {
        ("Assets/Scenes/RooftopArena.unity", new Vector3(-20f, 14f, -20f), new Vector3(8f, 4f, 12f)),
        ("Assets/Scenes/RooftopArena.unity", new Vector3(0f, 5.5f, -4f), new Vector3(0f, 4.5f, 13f)),
        ("Assets/Scenes/RooftopArena.unity", new Vector3(30f, 20f, 45f), new Vector3(0f, 4f, 8f)),
        // Construction-zone vantages for RooftopArena; a near-duplicate of the overview shot
        // immediately above is intentionally omitted.
        ("Assets/Scenes/RooftopArena.unity", new Vector3(-50f, 16f, -40f), new Vector3(-26f, 3f, -15f)),
        ("Assets/Scenes/RooftopArena.unity", new Vector3(-44f, 5.5f, 6f), new Vector3(-37f, 3f, -20f)),
    };

    /// <summary>Rooftop-vantage shots of the CURRENTLY OPEN scene (no reopen), framing the backdrop
    /// city's traffic avenues and skyline from where the player actually stands. Works identically in
    /// edit mode and in PLAY mode — call it a second or two into play (via manage_editor play, then
    /// execute this) to catch cars mid-move and traffic lights mid-cycle. Writes
    /// Tools/screenshots/shot_city_{i}.png. Used by the backdrop-city visual-review loop.</summary>
    private static readonly (string tag, Vector3 pos, Vector3 lookAt)[] CityVantages =
    {
        // 0: high corner overview — whole street grid, perimeter avenues, skyline behind.
        ("overview", new Vector3(52f, 30f, 52f), new Vector3(-7f, -14f, -2f)),
        // 1: look down the NORTH avenue (z=45, 8m wide — the most visible open street) from a roof.
        ("north_ave", new Vector3(-7f, 17f, 16f), new Vector3(-7f, -20f, 45f)),
        // 2: look down the EAST side toward the E avenue (x=45) and east skyline.
        ("east_ave", new Vector3(26f, 15f, -4f), new Vector3(46f, -21f, -6f)),
    };

    public static void PlayModeShot()
    {
        Directory.CreateDirectory("Tools/screenshots");
        for (int i = 0; i < CityVantages.Length; i++)
        {
            (string tag, Vector3 pos, Vector3 lookAt) = CityVantages[i];
            var camGo = new GameObject("CityShotCam");
            Camera cam = camGo.AddComponent<Camera>();
            cam.transform.position = pos;
            cam.transform.LookAt(lookAt);
            cam.fieldOfView = 68f;
            cam.farClipPlane = 1200f;
            var camData = cam.GetUniversalAdditionalCameraData();
            camData.renderPostProcessing = true;
            camData.antialiasing = AntialiasingMode.SubpixelMorphologicalAntiAliasing;
            camData.antialiasingQuality = AntialiasingQuality.High;

            var rt = new RenderTexture(1600, 900, 24, RenderTextureFormat.ARGB32);
            cam.targetTexture = rt;
            cam.Render();

            RenderTexture.active = rt;
            var tex = new Texture2D(1600, 900, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, 1600, 900), 0, 0);
            tex.Apply();
            RenderTexture.active = null;
            cam.targetTexture = null;

            string file = $"Tools/screenshots/shot_city_{i}_{tag}.png";
            File.WriteAllBytes(file, tex.EncodeToPNG());
            Object.DestroyImmediate(camGo);
            Object.DestroyImmediate(rt);
            Debug.Log($"SCREENSHOT_OK: {file}");
        }
    }

    public static void CaptureAll()
    {
        Directory.CreateDirectory("Tools/screenshots");
        for (int i = 0; i < Shots.Length; i++)
        {
            (string scenePath, Vector3 pos, Vector3 lookAt) = Shots[i];
            EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);

            var camGo = new GameObject("ShotCam");
            Camera cam = camGo.AddComponent<Camera>();
            cam.transform.position = pos;
            cam.transform.LookAt(lookAt);
            cam.fieldOfView = 70f;
            var camData = cam.GetUniversalAdditionalCameraData();
            camData.renderPostProcessing = true;
            // Match the player camera's AA so review shots reflect what ships (see BuildCamera).
            camData.antialiasing = AntialiasingMode.SubpixelMorphologicalAntiAliasing;
            camData.antialiasingQuality = AntialiasingQuality.High;

            var rt = new RenderTexture(1600, 900, 24, RenderTextureFormat.ARGB32);
            cam.targetTexture = rt;
            cam.Render();

            RenderTexture.active = rt;
            var tex = new Texture2D(1600, 900, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, 1600, 900), 0, 0);
            tex.Apply();
            RenderTexture.active = null;
            cam.targetTexture = null;

            string file = $"Tools/screenshots/shot_{i}_{Path.GetFileNameWithoutExtension(scenePath)}.png";
            File.WriteAllBytes(file, tex.EncodeToPNG());
            Object.DestroyImmediate(camGo);
            Object.DestroyImmediate(rt);
            Debug.Log($"SCREENSHOT_OK: {file}");
        }
    }
}
