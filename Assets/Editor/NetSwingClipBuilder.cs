#nullable enable

using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Game.EditorTools;

/// <summary>
/// Authors the pest-control net-swing clip by aiming LIMB DIRECTIONS, then baking per-bone
/// transform curves. v1 hand-guessed muscle values ("snapping wood over his knee"); v2 rotated
/// bones about world axes, which twists instead of lifts when the base pose points a limb along
/// the rotation axis (T-pose arms). v3 is direction-driven: per frame, reset to bind pose, then
/// FromToRotation each arm segment onto an authored target direction in agent space — correct
/// regardless of rig or base pose — and key every bone's local rotation.
///
/// Motion (0.9s): 0→0.32 both hands raise the pole up and BEHIND the right shoulder (left hand
/// gripping above the right), back arched, twist right → hold to 0.45 → 0.45-0.55 whip
/// forward-down in a scooping arc sweeping right-to-left, torso pitching forward → recoil to a
/// ready half-pose. The net pole rides the right hand's local +Y (how NetVisual.BuildNet mounts).
///
/// Output NetSwing.anim = generic transform-curve clip for THIS rig (pest_control). Regenerate:
/// menu RooftopTag/Generate Net Swing Clip (GUID preserved). FBX: RooftopTag/Export Net Swing FBX.
/// </summary>
public static class NetSwingClipBuilder
{
    private const string OutputPath = "Assets/Art/Characters/Animations/NetSwing.anim";
    private const float ClipLength = 0.9f;
    private const float BakeFps = 30f;

    // Phase boundaries (seconds).
    private const float RaiseEnd = 0.32f;
    private const float HoldEnd = 0.45f;
    private const float WhipEnd = 0.55f;

    // Ready pose kept at the end of the recoil (fraction of the scoop pose).
    private const float ReadyResidual = 0.2f;

    // Whip sweep, degrees about agent up. Observed on this rig: NEGATIVE = character's right.
    // Kept small — the swing must live in the character's FRONT plane (high → low), the sweep is
    // only a light load-right / release-left accent.
    private const float SweepStartDeg = -8f;
    private const float SweepEndDeg = 6f;

    // Two-handed grip: how far above the right hand the left hand grabs the pole (rig units, on a
    // ~1m-tall unscaled rig), and the palm-facing axis candidates resolved by render iteration.
    private const float GripSeparation = 0.22f;

    // Torso, degrees about agent axes.
    private const float SpineArchDeg = 14f;
    private const float SpinePitchFwdDeg = 24f;
    private const float SpineTwistLoadDeg = 15f;

    [MenuItem("RooftopTag/Generate Net Swing Clip")]
    public static void Generate()
    {
        var prefab = Resources.Load<GameObject>("pest_control");
        if (prefab == null) { Debug.LogError("NetSwingClipBuilder: pest_control not in Resources"); return; }

        GameObject rig = Object.Instantiate(prefab);
        try
        {
            Animator animator = rig.GetComponentInChildren<Animator>();
            if (animator == null || !animator.isHuman) { Debug.LogError("NetSwingClipBuilder: no humanoid Animator"); return; }

            // Bind-pose snapshot (restored before posing each frame so nothing accumulates).
            var bones = new List<(Transform t, string path, Quaternion rot, Vector3 pos)>();
            CollectBones(rig.transform, string.Empty, bones);

            int frames = Mathf.CeilToInt(ClipLength * BakeFps) + 1;
            var rotCurves = new Dictionary<string, AnimationCurve[]>();
            foreach ((_, string path, _, _) in bones)
                rotCurves[path] = new[] { new AnimationCurve(), new AnimationCurve(), new AnimationCurve(), new AnimationCurve() };

            for (int f = 0; f < frames; f++)
            {
                float t = Mathf.Min(f / BakeFps, ClipLength);
                foreach ((Transform bt, _, Quaternion r, Vector3 p) in bones) { bt.localRotation = r; bt.localPosition = p; }

                ApplySwingPose(animator, rig.transform, t);

                foreach ((Transform bt, string path, _, _) in bones)
                {
                    Quaternion q = bt.localRotation;
                    AnimationCurve[] rc = rotCurves[path];
                    rc[0].AddKey(t, q.x); rc[1].AddKey(t, q.y); rc[2].AddKey(t, q.z); rc[3].AddKey(t, q.w);
                }
            }

            var clip = new AnimationClip { name = "NetSwing", frameRate = BakeFps };
            foreach ((_, string path, _, _) in bones)
            {
                AnimationCurve[] rc = rotCurves[path];
                clip.SetCurve(path, typeof(Transform), "m_LocalRotation.x", rc[0]);
                clip.SetCurve(path, typeof(Transform), "m_LocalRotation.y", rc[1]);
                clip.SetCurve(path, typeof(Transform), "m_LocalRotation.z", rc[2]);
                clip.SetCurve(path, typeof(Transform), "m_LocalRotation.w", rc[3]);
            }
            clip.EnsureQuaternionContinuity();

            var existing = AssetDatabase.LoadAssetAtPath<AnimationClip>(OutputPath);
            if (existing != null) { EditorUtility.CopySerialized(clip, existing); Object.DestroyImmediate(clip); }
            else AssetDatabase.CreateAsset(clip, OutputPath);
            AssetDatabase.SaveAssets();
            Debug.Log($"NetSwing clip (direction-posed, {frames} frames) written to {OutputPath}");
        }
        finally
        {
            Object.DestroyImmediate(rig);
        }
    }

    // ---------------------------------------------------------------- pose synthesis

    // The clip is a path through FOUR keyposes — READY → LOAD → SCOOP → READY — at full pose
    // authority throughout (bind pose is a T-pose and must never leak in). READY = arms in FRONT
    // holding the pole upright; LOAD = both hands carry the pole up OVER THE RIGHT SHOULDER
    // (batter-style); SCOOP = the whip lands the net low in front. Returns which segment t is in
    // and the eased progress through it.
    private enum Seg { Windup, Hold, Whip, Recoil }

    private static (Seg seg, float u) PhaseAt(float t)
    {
        if (t < RaiseEnd)
        {
            float u = t / RaiseEnd;
            return (Seg.Windup, 1f - (1f - u) * (1f - u)); // EaseOut load
        }
        if (t < HoldEnd) return (Seg.Hold, 1f);
        if (t < WhipEnd)
        {
            float u = (t - HoldEnd) / (WhipEnd - HoldEnd);
            return (Seg.Whip, u * u);                       // EaseIn whip
        }
        float v = (t - WhipEnd) / (ClipLength - WhipEnd);
        return (Seg.Recoil, 1f - (1f - v) * (1f - v));      // EaseOut settle
    }

    // One keypose: arm segment directions + pole direction + torso angles (pitch +fwd/-back,
    // twist +right), all in skeleton space.
    private readonly struct KeyPose
    {
        public readonly Vector3 UpperR, LowerR, Pole;
        public readonly float SpinePitch, SpineTwist;
        public KeyPose(Vector3 upperR, Vector3 lowerR, Vector3 pole, float spinePitch, float spineTwist)
        { UpperR = upperR; LowerR = lowerR; Pole = pole; SpinePitch = spinePitch; SpineTwist = spineTwist; }

        public static KeyPose Blend(KeyPose a, KeyPose b, float u) => new(
            Vector3.Slerp(a.UpperR, b.UpperR, u), Vector3.Slerp(a.LowerR, b.LowerR, u),
            Vector3.Slerp(a.Pole, b.Pole, u),
            Mathf.Lerp(a.SpinePitch, b.SpinePitch, u), Mathf.Lerp(a.SpineTwist, b.SpineTwist, u));
    }

    // internal: NetSwingFbxExporter drives this directly (single source of truth for the pose math).
    internal static void ApplySwingPose(Animator animator, Transform agent, float t)
    {
        // Axes from the SKELETON, not the root: this FBX's bind pose faces -X while the prefab
        // root faces +Z (measured: shoulder line runs along +Z). Authoring against root axes put
        // the whole swing 90° off. Must be read BEFORE any bones move this frame (callers restore
        // bind pose first, so this sees the bind skeleton).
        Transform lSh = animator.GetBoneTransform(HumanBodyBones.LeftUpperArm);
        Transform rSh = animator.GetBoneTransform(HumanBodyBones.RightUpperArm);
        if (lSh == null || rSh == null) return;
        Vector3 up = Vector3.up;
        Vector3 right = Vector3.ProjectOnPlane(rSh.position - lSh.position, up).normalized;
        Vector3 fwd = Vector3.Cross(right, up).normalized;

        // Keyposes (directions normalized by KeyPose.Blend's Slerp inputs being unit-ish).
        var ready = new KeyPose(
            (fwd * 0.35f - up * 0.95f + right * 0.25f).normalized,  // right upper arm: down-front
            (fwd * 0.7f - up * 0.6f).normalized,                    // forearm: reaching front
            (up * 0.8f + fwd * 0.6f + right * 0.35f).normalized,    // pole leaning forward, hoop clear of the head
            4f, 0f);
        var load = new KeyPose(
            (up * 0.75f + right * 0.6f - fwd * 0.3f).normalized,    // upper arm up over the RIGHT shoulder
            (up * 0.7f + right * 0.35f - fwd * 0.65f).normalized,   // elbow cocks the pole behind the shoulder
            (-fwd * 0.85f + up * 0.4f + right * 0.35f).normalized,  // pole points back over the right shoulder
            -SpineArchDeg, SpineTwistLoadDeg);
        var scoop = new KeyPose(
            (fwd * 0.95f - up * 0.5f - right * 0.1f).normalized,    // swing lands ahead, drifting slightly left
            (fwd * 1.0f - up * 0.35f - right * 0.05f).normalized,
            (fwd * 0.85f - up * 0.5f - right * 0.15f).normalized,   // net slams down-forward
            SpinePitchFwdDeg, -4f);

        (Seg seg, float u) = PhaseAt(t);
        KeyPose pose = seg switch
        {
            Seg.Windup => KeyPose.Blend(ready, load, u),
            Seg.Hold => load,
            Seg.Whip => KeyPose.Blend(load, scoop, u),
            _ => KeyPose.Blend(scoop, ready, u),
        };

        // Torso: spine points up (perpendicular to both axes used), so axis rotations are safe.
        RotateBone(animator, HumanBodyBones.Spine, Quaternion.AngleAxis(pose.SpinePitch * 0.5f, right) * Quaternion.AngleAxis(pose.SpineTwist * 0.5f, up));
        RotateBone(animator, HumanBodyBones.Chest, Quaternion.AngleAxis(pose.SpinePitch * 0.5f, right) * Quaternion.AngleAxis(pose.SpineTwist * 0.5f, up));
        RotateBone(animator, HumanBodyBones.Neck, Quaternion.AngleAxis(-pose.SpinePitch * 0.7f, right)); // eyes on target

        // Legs: light stagger keyed off the torso amounts (load = weight back, scoop = weight front).
        float loadAmt = Mathf.InverseLerp(4f, -SpineArchDeg, pose.SpinePitch);   // 0 ready → 1 loaded
        float scoopAmt = Mathf.InverseLerp(4f, SpinePitchFwdDeg, pose.SpinePitch);
        RotateBone(animator, HumanBodyBones.RightUpperLeg, Quaternion.AngleAxis(14f * loadAmt - 6f * scoopAmt + 4f, right));
        RotateBone(animator, HumanBodyBones.LeftUpperLeg, Quaternion.AngleAxis(-12f * loadAmt + 4f * scoopAmt + 4f, right));
        RotateBone(animator, HumanBodyBones.RightLowerLeg, Quaternion.AngleAxis(-10f * loadAmt - 6f, right));
        RotateBone(animator, HumanBodyBones.LeftLowerLeg, Quaternion.AngleAxis(-8f * loadAmt - 6f * scoopAmt - 6f, right));

        // ---- Right arm: aim segments, then FIX THE BEND PLANE. FromToRotation is minimal-rotation
        // and leaves whatever roll the previous pose had — across frames the elbow wandered and the
        // arms read as flailing/folding/twisting. StabilizeBendPlane rolls the upper arm about its
        // own axis so the elbow always points to the same authored side, every frame.
        Vector3 elbowHintR = (-up * 0.6f + right * 0.8f).normalized; // right elbow: out and down
        AimSegment(animator, HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm, pose.UpperR, 1f);
        StabilizeBendPlane(animator, HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand, elbowHintR);
        AimSegment(animator, HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand, pose.LowerR, 1f);

        Vector3 poleDir = pose.Pole;
        OrientHand(animator, HumanBodyBones.RightHand, poleDir, -right, 1f);

        // Left hand GRABS the pole above the right hand: 2-pass CCD onto the pole line, with its
        // own bend-plane fix (left elbow points out-down on the LEFT side).
        Transform? rHand = animator.GetBoneTransform(HumanBodyBones.RightHand);
        if (rHand != null)
        {
            Vector3 grip = rHand.position + poleDir * GripSeparation;
            Vector3 elbowHintL = (-up * 0.6f - right * 0.8f).normalized;
            for (int pass = 0; pass < 2; pass++)
            {
                Transform? lShoulder = animator.GetBoneTransform(HumanBodyBones.LeftUpperArm);
                Transform? lElbow = animator.GetBoneTransform(HumanBodyBones.LeftLowerArm);
                Transform? lWrist = animator.GetBoneTransform(HumanBodyBones.LeftHand);
                if (lShoulder == null || lElbow == null || lWrist == null) break;
                AimSegment(animator, HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm, grip - lShoulder.position, 1f);
                StabilizeBendPlane(animator, HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftHand, elbowHintL);
                AimSegment(animator, HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftHand, grip - lElbow.position, 1f);
            }
            OrientHand(animator, HumanBodyBones.LeftHand, poleDir, right, 1f);
        }
    }

    /// <summary>Rolls the upper bone about its own aim axis so the bend plane (upper × lower) faces
    /// <paramref name="elbowHint"/> — a constant authored side for the elbow. Kills the frame-to-frame
    /// twist wander FromToRotation leaves behind (it controls direction, never roll).</summary>
    private static void StabilizeBendPlane(Animator animator, HumanBodyBones upperId, HumanBodyBones lowerId, HumanBodyBones endId, Vector3 elbowHint)
    {
        Transform? upper = animator.GetBoneTransform(upperId);
        Transform? lower = animator.GetBoneTransform(lowerId);
        Transform? end = animator.GetBoneTransform(endId);
        if (upper == null || lower == null || end == null) return;

        Vector3 axis = (lower.position - upper.position).normalized;          // upper-arm aim axis
        Vector3 curElbowDir = Vector3.ProjectOnPlane(end.position - lower.position, axis);
        Vector3 wantElbowDir = Vector3.ProjectOnPlane(elbowHint, axis);
        if (curElbowDir.sqrMagnitude < 1e-6f || wantElbowDir.sqrMagnitude < 1e-6f) return;

        // The forearm continues past the elbow, so the elbow "points" opposite the forearm's
        // off-axis component — roll so that component lands opposite the hint.
        float angle = Vector3.SignedAngle(curElbowDir, -wantElbowDir, axis);
        upper.rotation = Quaternion.AngleAxis(angle, axis) * upper.rotation;
    }

    /// <summary>Fully orients a hand: local +Y (the pole axis — how NetVisual.BuildNet mounts) onto
    /// <paramref name="poleDir"/>, local +Z (palm normal on this rig) toward <paramref name="palmDir"/>.</summary>
    private static void OrientHand(Animator animator, HumanBodyBones handId, Vector3 poleDir, Vector3 palmDir, float weight)
    {
        Transform? hand = animator.GetBoneTransform(handId);
        if (hand == null) return;
        Vector3 palmOrtho = Vector3.ProjectOnPlane(palmDir, poleDir);
        if (palmOrtho.sqrMagnitude < 1e-4f) return;
        Quaternion target = Quaternion.LookRotation(palmOrtho.normalized, poleDir);
        hand.rotation = Quaternion.Slerp(hand.rotation, target, Mathf.Clamp01(weight));
    }

    /// <summary>Rotates <paramref name="boneId"/> so the direction to its child bone matches
    /// <paramref name="targetDir"/>, blended by weight (0 = bind pose, 1 = full aim).</summary>
    private static void AimSegment(Animator animator, HumanBodyBones boneId, HumanBodyBones childId, Vector3 targetDir, float weight)
    {
        Transform? bone = animator.GetBoneTransform(boneId);
        Transform? child = animator.GetBoneTransform(childId);
        if (bone == null || child == null || targetDir.sqrMagnitude < 1e-4f) return;
        Vector3 current = (child.position - bone.position).normalized;
        Quaternion aim = Quaternion.FromToRotation(current, targetDir.normalized);
        bone.rotation = Quaternion.Slerp(Quaternion.identity, aim, Mathf.Clamp01(weight)) * bone.rotation;
    }

    private static void RotateBone(Animator animator, HumanBodyBones boneId, Quaternion worldRot)
    {
        Transform? bone = animator.GetBoneTransform(boneId);
        if (bone != null) bone.rotation = worldRot * bone.rotation;
    }

    private static void CollectBones(Transform node, string path, List<(Transform, string, Quaternion, Vector3)> bones)
    {
        foreach (Transform child in node)
        {
            string childPath = string.IsNullOrEmpty(path) ? child.name : path + "/" + child.name;
            bones.Add((child, childPath, child.localRotation, child.localPosition));
            CollectBones(child, childPath, bones);
        }
    }
}
