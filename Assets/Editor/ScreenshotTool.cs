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
        // Construction-zone vantages: RooftopArena is now the main game scene, so these
        // (originally captured against TagArena.unity) point there. The third original TagArena
        // vantage (-22,12,-18 -> 8,4,13) was dropped as a near-duplicate of the overview shot
        // immediately above (-20,14,-20 -> 8,4,12).
        ("Assets/Scenes/RooftopArena.unity", new Vector3(-50f, 16f, -40f), new Vector3(-26f, 3f, -15f)),
        ("Assets/Scenes/RooftopArena.unity", new Vector3(-44f, 5.5f, 6f), new Vector3(-37f, 3f, -20f)),
        ("Assets/Scenes/MovementPlayground.unity", new Vector3(-14f, 8f, -8f), new Vector3(0f, 1f, 25f)),
    };

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
            cam.GetUniversalAdditionalCameraData().renderPostProcessing = true;

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
