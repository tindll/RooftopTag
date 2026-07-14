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

    // Slide-frame diagnostic (Bug A). Poses the raccoon at each frame of the (import-trimmed 18-40)
    // "X Bot@Running Slide" clip via humanoid retarget and logs the Hips world-Y — a precise
    // crouch-depth curve to pick the sustained-low-glide window — plus a side-on PNG per frame.
    // Run: Unity.exe -batchmode -quit -projectPath . -executeMethod Game.EditorTools.CharacterPreviewShot.SlideFrames -logFile Tools/slideframes.log
    public static void SlideFrames()
    {
        Directory.CreateDirectory("Tools/screenshots/slide");
        EditorSceneManager.OpenScene("Assets/Scenes/RooftopArena.unity", OpenSceneMode.Single);

        const string clipPath = "Assets/Art/Characters/Animations/X Bot@Running Slide.fbx";
        // Force a reimport so the clip's trim reflects the CURRENT postprocessor consts.
        AssetDatabase.ImportAsset(clipPath, ImportAssetOptions.ForceUpdate);
        AnimationClip? clip = null;
        foreach (var o in AssetDatabase.LoadAllAssetsAtPath(clipPath))
            if (o is AnimationClip c && !c.name.StartsWith("__preview")) { clip = c; break; }
        if (clip == null) { Debug.LogError("SLIDE_NO_CLIP"); return; }

        // The on-disk clip is import-trimmed to original frames 18..40 (SlideFirst/LastFrame), so its
        // local frame f maps to original frame 18+f. frameRate is the source's (usually 30).
        var prefab = Resources.Load<GameObject>("raccoon");
        GameObject model = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
        model.transform.position = Vector3.zero;
        model.transform.rotation = Quaternion.identity;
        var anim = model.GetComponent<Animator>();
        if (anim == null) anim = model.AddComponent<Animator>();

        // Read the trim's first-frame from the importer so the original-frame labels are correct
        // regardless of what SlideFirst/LastFrame are currently set to.
        var slideImporter = (ModelImporter)AssetImporter.GetAtPath(clipPath);
        var slideClips = slideImporter.clipAnimations.Length > 0 ? slideImporter.clipAnimations : slideImporter.defaultClipAnimations;
        int baseFrame = slideClips.Length > 0 ? Mathf.RoundToInt(slideClips[0].firstFrame) : 0;

        int trimmedFrames = Mathf.RoundToInt(clip.length * clip.frameRate);
        Debug.Log($"SLIDE_CLIP length={clip.length:0.000}s frameRate={clip.frameRate} trimmedFrames={trimmedFrames} (local 0..{trimmedFrames} = original {baseFrame}..{baseFrame + trimmedFrames})");

        var camGo = new GameObject("SlideCam");
        Camera cam = camGo.AddComponent<Camera>();
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0.2f, 0.22f, 0.28f);
        cam.fieldOfView = 35f;
        var rt = new RenderTexture(700, 700, 24, RenderTextureFormat.ARGB32);
        cam.targetTexture = rt;

        AnimationMode.StartAnimationMode();
        for (int f = 0; f <= trimmedFrames; f++)
        {
            float t = trimmedFrames == 0 ? 0f : (float)f / trimmedFrames * clip.length;
            AnimationMode.BeginSampling();
            AnimationMode.SampleAnimationClip(model, clip, t);
            AnimationMode.EndSampling();

            Transform? hips = anim.GetBoneTransform(HumanBodyBones.Hips);
            Transform? head = anim.GetBoneTransform(HumanBodyBones.Head);
            var renderers = model.GetComponentsInChildren<Renderer>();
            Bounds b = renderers[0].bounds;
            foreach (var r in renderers) b.Encapsulate(r.bounds);
            float hipsY = hips != null ? hips.position.y : -1f;
            float headY = head != null ? head.position.y : -1f;
            int originalFrame = baseFrame + f;
            Debug.Log($"SLIDE_FRAME orig={originalFrame} local={f} hipsY={hipsY:0.000} headY={headY:0.000} boundsMinY={b.min.y:0.000} boundsHeight={b.size.y:0.000}");

            // Side-on shot, re-centred on the model each frame so root drift doesn't push it out of view.
            cam.transform.position = b.center + new Vector3(3.2f, 0.15f, 0f);
            cam.transform.LookAt(b.center);
            cam.Render();
            RenderTexture.active = rt;
            var tex = new Texture2D(700, 700, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, 700, 700), 0, 0);
            tex.Apply();
            RenderTexture.active = null;
            File.WriteAllBytes($"Tools/screenshots/slide/frame_{originalFrame:00}.png", tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
        }
        AnimationMode.StopAnimationMode();
        cam.targetTexture = null;
        Debug.Log("SLIDE_FRAMES_OK");
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
