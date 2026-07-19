#nullable enable

using System.IO;
using Game.Movement;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Game.EditorTools;

/// <summary>
/// Play-mode diagnostic: logs, for every character Animator, which clip is playing, whether its
/// normalized time advances (animation actually running vs frozen), the driving motor speed/state,
/// and whether the humanoid avatar/controller are bound. Captures two frames. Reveals why animations
/// "don't work". Run WITH graphics, no -quit.
/// </summary>
public static class CharacterAnimDiag
{
    static double _t0 = -1;
    static double _lastLog = -1;
    static bool _shotA;

    public static void Run()
    {
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
        if (_t0 < 0) _t0 = EditorApplication.timeSinceStartup;
        double el = EditorApplication.timeSinceStartup - _t0;

        if (el - _lastLog >= 0.6)
        {
            _lastLog = el;
            foreach (Animator a in Object.FindObjectsByType<Animator>())
            {
                var motor = a.GetComponentInParent<CharacterMotor>();
                string clip = "none";
                var ci = a.GetCurrentAnimatorClipInfo(0);
                if (ci.Length > 0 && ci[0].clip != null) clip = ci[0].clip.name;
                var st = a.GetCurrentAnimatorStateInfo(0);
                Debug.Log($"DIAG t={el:0.0} root={a.transform.root.name} clip={clip} nt={st.normalizedTime:0.00} " +
                          $"speed={(motor != null ? motor.CurrentSpeed : -1):0.0} mstate={(motor != null ? motor.CurrentState.ToString() : "?")} " +
                          $"ctrl={(a.runtimeAnimatorController != null)} human={a.isHuman} avatar={(a.avatar != null)} enabled={a.enabled} speedParam={a.speed:0.0}");
            }
        }

        // Fire the dive roll through the real bridge path (as a lunge would), then capture it.
        if (el >= 2.0 && _phase == 0) { _phase = 1; Capture("idle_real"); TriggerDiveRolls(); }
        if (el >= 2.5 && _phase == 1) { _phase = 2; Capture("diveroll"); }
        if (el >= 5.0 && _phase == 2)
        {
            EditorApplication.update -= Tick;
            EditorApplication.Exit(0);
        }
    }

    static int _phase;

    static void TriggerDiveRolls()
    {
        foreach (var b in Object.FindObjectsByType<CharacterAnimatorBridge>())
            b.TriggerDiveRoll();
    }

    static void Capture(string name)
    {
        Camera cam = Camera.main;
        if (cam == null) { Debug.Log($"DIAG_NO_CAM {name}"); return; }
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
        File.WriteAllBytes($"Tools/screenshots/{name}.png", tex.EncodeToPNG());
        Debug.Log($"DIAG_SHOT {name}");
    }
}
