#nullable enable

using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Game.EditorTools;

/// <summary>
/// Enters play mode on the Rooftop arena, lets the runtime bootstrap attach the character models and
/// the animations settle, then captures through the live game camera and exits. This is the only way
/// to see the real integration (models attach at runtime, not in edit mode).
/// Run WITH graphics: Unity.exe -batchmode -projectPath . -executeMethod Game.EditorTools.PlayModeShot.Run -logFile Tools/play.log
/// (no -quit; the method exits the editor itself once the shot is written)
/// </summary>
public static class PlayModeShot
{
    static double _enteredAt = -1;
    const double SettleSeconds = 3.0;

    public static void Run()
    {
        // Disable the domain reload on play-enter so this static callback + timer survive into play
        // mode (otherwise the reload wipes them and the capture never fires).
        EditorSettings.enterPlayModeOptionsEnabled = true;
        EditorSettings.enterPlayModeOptions =
            EnterPlayModeOptions.DisableDomainReload | EnterPlayModeOptions.DisableSceneReload;

        EditorSceneManager.OpenScene("Assets/Scenes/RooftopArena.unity", OpenSceneMode.Single);
        EditorApplication.update += Tick;
        EditorApplication.EnterPlaymode();
    }

    static void Tick()
    {
        if (!EditorApplication.isPlaying) return;
        if (_enteredAt < 0) _enteredAt = EditorApplication.timeSinceStartup;
        if (EditorApplication.timeSinceStartup - _enteredAt < SettleSeconds) return;

        EditorApplication.update -= Tick;

        Camera cam = Camera.main;
        string result;
        if (cam == null)
        {
            result = "PLAYSHOT_NO_CAMERA";
        }
        else
        {
            var rt = new RenderTexture(1600, 900, 24, RenderTextureFormat.ARGB32);
            cam.targetTexture = rt;
            cam.Render();
            RenderTexture.active = rt;
            var tex = new Texture2D(1600, 900, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, 1600, 900), 0, 0);
            tex.Apply();
            RenderTexture.active = null;
            cam.targetTexture = null;

            Directory.CreateDirectory("Tools/screenshots");
            File.WriteAllBytes("Tools/screenshots/playmode.png", tex.EncodeToPNG());
            result = "PLAYSHOT_OK Tools/screenshots/playmode.png";
        }

        // Report how many animated character models actually attached.
        int models = Object.FindObjectsByType<Animator>().Length;
        Debug.Log($"{result} animators={models}");
        EditorApplication.Exit(0);
    }
}
