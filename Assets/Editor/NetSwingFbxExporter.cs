#nullable enable

using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Formats.Fbx.Exporter;
using UnityEngine;

namespace Game.EditorTools;

/// <summary>
/// Bakes the humanoid-muscle NetSwing.anim onto the pest_control rig as plain per-bone transform
/// curves and exports the result (rig + baked animation) to NetSwing.fbx — viewable in any DCC or
/// Unity's FBX inspector preview. Muscle clips can't go to FBX directly (FBX has no concept of
/// Unity muscle space), hence the bake: sample the clip at BakeFps, record every bone's local
/// rotation (+ hips position) per frame.
/// </summary>
public static class NetSwingFbxExporter
{
    private const string ClipPath = "Assets/Art/Characters/Animations/NetSwing.anim";
    private const string OutputPath = "Assets/Art/Characters/Animations/NetSwing.fbx";
    private const float BakeFps = 30f;

    [MenuItem("RooftopTag/Export Net Swing FBX")]
    public static void Export()
    {
        var prefab = Resources.Load<GameObject>("pest_control");
        if (prefab == null) { Debug.LogError("NetSwingFbxExporter: missing pest_control"); return; }

        GameObject rig = Object.Instantiate(prefab);
        rig.name = "NetSwing";
        try
        {
            Animator animator = rig.GetComponentInChildren<Animator>();
            if (animator == null) { Debug.LogError("NetSwingFbxExporter: no Animator"); return; }

            // Collect every transform under the rig with its relative path (curve binding paths)
            // plus the bind pose, restored before posing each frame (poses must not accumulate).
            var bones = new List<(Transform t, string path)>();
            CollectBones(rig.transform, string.Empty, bones);
            var bindRot = new Dictionary<string, Quaternion>();
            var bindPos = new Dictionary<string, Vector3>();
            foreach ((Transform bt, string path) in bones) { bindRot[path] = bt.localRotation; bindPos[path] = bt.localPosition; }

            // Drive NetSwingClipBuilder's pose math directly (SampleAnimation can't apply the
            // generic clip while an Animator sits on the rig, so we never round-trip the asset).
            const float clipLength = 0.9f;
            int frames = Mathf.CeilToInt(clipLength * BakeFps) + 1;
            var rotCurves = new Dictionary<string, AnimationCurve[]>();
            var posCurves = new Dictionary<string, AnimationCurve[]>();
            foreach ((_, string path) in bones)
            {
                rotCurves[path] = new[] { new AnimationCurve(), new AnimationCurve(), new AnimationCurve(), new AnimationCurve() };
                posCurves[path] = new[] { new AnimationCurve(), new AnimationCurve(), new AnimationCurve() };
            }

            for (int f = 0; f < frames; f++)
            {
                float t = Mathf.Min(f / BakeFps, clipLength);
                foreach ((Transform bt, string path) in bones) { bt.localRotation = bindRot[path]; bt.localPosition = bindPos[path]; }
                NetSwingClipBuilder.ApplySwingPose(animator, rig.transform, t);
                foreach ((Transform bone, string path) in bones)
                {
                    Quaternion q = bone.localRotation;
                    Vector3 p = bone.localPosition;
                    AnimationCurve[] rc = rotCurves[path];
                    rc[0].AddKey(t, q.x); rc[1].AddKey(t, q.y); rc[2].AddKey(t, q.z); rc[3].AddKey(t, q.w);
                    AnimationCurve[] pc = posCurves[path];
                    pc[0].AddKey(t, p.x); pc[1].AddKey(t, p.y); pc[2].AddKey(t, p.z);
                }
            }

            // Legacy clip on an Animation component — the FBX exporter picks that up as the take.
            var baked = new AnimationClip { name = "NetSwing", frameRate = BakeFps, legacy = true };
            foreach ((_, string path) in bones)
            {
                AnimationCurve[] rc = rotCurves[path];
                baked.SetCurve(path, typeof(Transform), "m_LocalRotation.x", rc[0]);
                baked.SetCurve(path, typeof(Transform), "m_LocalRotation.y", rc[1]);
                baked.SetCurve(path, typeof(Transform), "m_LocalRotation.z", rc[2]);
                baked.SetCurve(path, typeof(Transform), "m_LocalRotation.w", rc[3]);
                AnimationCurve[] pc = posCurves[path];
                baked.SetCurve(path, typeof(Transform), "m_LocalPosition.x", pc[0]);
                baked.SetCurve(path, typeof(Transform), "m_LocalPosition.y", pc[1]);
                baked.SetCurve(path, typeof(Transform), "m_LocalPosition.z", pc[2]);
            }
            baked.EnsureQuaternionContinuity();

            var animComp = rig.AddComponent<Animation>();
            animComp.AddClip(baked, "NetSwing");
            animComp.clip = baked;

            ModelExporter.ExportObject(OutputPath, rig);
            AssetDatabase.Refresh();
            Debug.Log($"NetSwing FBX exported to {OutputPath}");
        }
        finally
        {
            Object.DestroyImmediate(rig);
        }
    }

    private static void CollectBones(Transform node, string path, List<(Transform, string)> bones)
    {
        foreach (Transform child in node)
        {
            string childPath = string.IsNullOrEmpty(path) ? child.name : path + "/" + child.name;
            bones.Add((child, childPath));
            CollectBones(child, childPath, bones);
        }
    }
}
