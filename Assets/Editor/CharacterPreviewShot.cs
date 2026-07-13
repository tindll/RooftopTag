#nullable enable

using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace Game.EditorTools;

/// <summary>
/// Edit-mode preview of the two character models side by side (the runtime models only attach in
/// play mode, so the normal ScreenshotTool can't see them). Instantiates raccoon + pest_control on
/// the Rooftop arena, renders a close-up, and logs each model's world-space height and base Y so we
/// can check it's ~1.8 m tall with feet at the origin.
/// Run: Unity.exe -batchmode -quit -projectPath . -executeMethod Game.EditorTools.CharacterPreviewShot.Capture -logFile Tools/preview.log
/// </summary>
public static class CharacterPreviewShot
{
    public static void Capture()
    {
        Directory.CreateDirectory("Tools/screenshots");
        EditorSceneManager.OpenScene("Assets/Scenes/RooftopArena.unity", OpenSceneMode.Single);

        var controller = Resources.Load<RuntimeAnimatorController>("CharacterAnimator");
        Debug.Log($"PREVIEW controller_loaded={controller != null}");

        GameObject raccoon = Spawn("raccoon", new Vector3(-0.9f, 12f, 12f));
        GameObject pest = Spawn("pest_control", new Vector3(0.9f, 12f, 12f));

        float feetY = raccoon.transform.position.y;
        var camGo = new GameObject("PreviewCam");
        Camera cam = camGo.AddComponent<Camera>();
        cam.transform.position = new Vector3(0f, feetY + 1.3f, 7.5f);
        cam.transform.LookAt(new Vector3(0f, feetY + 0.9f, 12f));
        cam.fieldOfView = 55f;
        cam.GetUniversalAdditionalCameraData().renderPostProcessing = true;

        var rt = new RenderTexture(1200, 1500, 24, RenderTextureFormat.ARGB32);
        cam.targetTexture = rt;
        cam.Render();
        RenderTexture.active = rt;
        var tex = new Texture2D(1200, 1500, TextureFormat.RGB24, false);
        tex.ReadPixels(new Rect(0, 0, 1200, 1500), 0, 0);
        tex.Apply();
        RenderTexture.active = null;
        cam.targetTexture = null;

        string file = "Tools/screenshots/character_preview.png";
        File.WriteAllBytes(file, tex.EncodeToPNG());
        Debug.Log($"PREVIEW_SHOT_OK: {file}");
    }

    static GameObject Spawn(string resourceName, Vector3 pos)
    {
        var prefab = Resources.Load<GameObject>(resourceName);
        if (prefab == null) { Debug.LogError($"PREVIEW_MISSING {resourceName}"); return new GameObject(resourceName); }

        GameObject go = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
        go.transform.position = pos;

        var renderers = go.GetComponentsInChildren<Renderer>();
        Bounds raw = renderers[0].bounds;
        foreach (var r in renderers) raw.Encapsulate(r.bounds);
        // Scale up to a ~1.8 m character (Tripo exports ~1 m).
        go.transform.localScale *= 1.8f / raw.size.y;

        Bounds b = renderers[0].bounds;
        foreach (var r in renderers) b.Encapsulate(r.bounds);

        // Drop feet onto the roof: raycast down, then lift the model so its lowest point sits there.
        float groundY = pos.y - 8f;
        if (Physics.Raycast(pos + Vector3.up * 2f, Vector3.down, out RaycastHit hit, 40f))
            groundY = hit.point.y;
        float feetGap = go.transform.position.y - b.min.y; // root is above feet by this much
        go.transform.position = new Vector3(pos.x, groundY + feetGap, pos.z);

        // Texture binding check (are the embedded Tripo textures actually applied?).
        int textured = 0;
        foreach (var m in renderers[0].sharedMaterials)
            if (m != null && m.mainTexture != null) textured++;
        Debug.Log($"PREVIEW_BOUNDS {resourceName} height={b.size.y:0.00} width={b.size.x:0.00} groundY={groundY:0.00} submeshes={renderers[0].sharedMaterials.Length} textured={textured}");
        return go;
    }
}
