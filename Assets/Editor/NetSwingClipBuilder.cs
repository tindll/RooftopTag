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

    // blend = position along the swing ARC (0 = loaded direction, 1 = scoop direction). Kept at 1
    // through the recoil — the arms lower along the scoop line as weight decays, they must NOT
    // slerp back through "overhead" (that read as a celebration-V, not a follow-through).
    private static (float raise, float scoop, float sweep, float blend) PhaseAt(float t)
    {
        if (t < RaiseEnd)
        {
            float u = t / RaiseEnd;
            float raise = 1f - (1f - u) * (1f - u);
            return (raise, 0f, SweepStartDeg * raise, 0f);
        }
        if (t < HoldEnd) return (1f, 0f, SweepStartDeg, 0f);
        if (t < WhipEnd)
        {
            float u = (t - HoldEnd) / (WhipEnd - HoldEnd);
            float scoop = u * u;
            return (1f - scoop, scoop, Mathf.Lerp(SweepStartDeg, SweepEndDeg, scoop), scoop);
        }
        float v = (t - WhipEnd) / (ClipLength - WhipEnd);
        float settle = 1f - (1f - v) * (1f - v);
        float residual = Mathf.Lerp(1f, ReadyResidual, settle);
        return (0f, residual, Mathf.Lerp(SweepEndDeg, SweepEndDeg * ReadyResidual, settle), 1f);
    }

    // internal: NetSwingFbxExporter drives this directly (single source of truth for the pose math).
    internal static void ApplySwingPose(Animator animator, Transform agent, float t)
    {
        (float raise, float scoop, float sweep, float blendPhase) = PhaseAt(t);
        Vector3 right = agent.right, up = agent.up, fwd = agent.forward;
        Quaternion sweepRot = Quaternion.AngleAxis(sweep, up); // rotates the whole swing plane

        // Torso: spine points up (perpendicular to the pitch axis), so axis rotations are safe here.
        float spinePitch = -SpineArchDeg * raise + SpinePitchFwdDeg * scoop;
        float spineTwist = SpineTwistLoadDeg * raise + sweep * 0.4f;
        RotateBone(animator, HumanBodyBones.Spine, Quaternion.AngleAxis(spinePitch * 0.5f, right) * Quaternion.AngleAxis(spineTwist * 0.5f, up));
        RotateBone(animator, HumanBodyBones.Chest, Quaternion.AngleAxis(spinePitch * 0.5f, right) * Quaternion.AngleAxis(spineTwist * 0.5f, up));
        RotateBone(animator, HumanBodyBones.Neck, Quaternion.AngleAxis(-spinePitch * 0.7f, right)); // eyes on target

        // Legs: bind pose points them down (also perpendicular to the pitch axis) — light stagger.
        RotateBone(animator, HumanBodyBones.RightUpperLeg, Quaternion.AngleAxis(14f * raise - 6f * scoop, right));
        RotateBone(animator, HumanBodyBones.LeftUpperLeg, Quaternion.AngleAxis(-12f * raise + 4f * scoop, right));
        RotateBone(animator, HumanBodyBones.RightLowerLeg, Quaternion.AngleAxis(-10f * raise, right));
        RotateBone(animator, HumanBodyBones.LeftLowerLeg, Quaternion.AngleAxis(-8f * raise - 6f * scoop, right));

        // ---- Arms: direction targets in agent space, blended windup → scoop, then swept about up.
        // Windup: right upper arm points up-and-back over the RIGHT shoulder; scoop: forward-and-down.
        Vector3 upperLoadR = (up * 1.0f - fwd * 0.55f + right * 0.25f).normalized;
        Vector3 upperScoop = (fwd * 1.0f - up * 0.55f).normalized;
        Vector3 lowerLoadR = (up * 0.8f - fwd * 0.9f + right * 0.1f).normalized;
        Vector3 lowerScoop = (fwd * 1.0f - up * 0.35f).normalized;
        // Net pole (right hand local +Y): pokes further behind at load, slams forward-down on the scoop.
        Vector3 poleLoad = (up * 0.55f - fwd * 1.0f).normalized;
        Vector3 poleScoop = (fwd * 0.9f - up * 0.5f).normalized;

        float blend = blendPhase;
        float weight = Mathf.Clamp01(raise + scoop);
        Vector3 poleDir = (sweepRot * Vector3.Slerp(poleLoad, poleScoop, blend)).normalized;

        // Right arm drives the swing.
        AimSegment(animator, HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm,
            sweepRot * Vector3.Slerp(upperLoadR, upperScoop, blend), weight);
        AimSegment(animator, HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand,
            sweepRot * Vector3.Slerp(lowerLoadR, lowerScoop, blend), weight);

        // Hands get FULL orientations, not minimal rotations — FromToRotation kept the bind pose's
        // roll, which left both palms facing OUTWARD. LookRotation pins the pole axis (hand +Y)
        // AND the palm normal: palms face each other across the pole (right palm toward the
        // character's left, left palm back toward the right).
        Vector3 sweptRight = (sweepRot * right).normalized;
        OrientHand(animator, HumanBodyBones.RightHand, poleDir, -sweptRight, weight);

        // Left hand GRABS the pole above the right hand: 2-pass CCD pulls the wrist onto the pole
        // line (direction aiming alone left the hands visibly apart at the top of the swing).
        Transform? rHand = animator.GetBoneTransform(HumanBodyBones.RightHand);
        if (rHand != null)
        {
            Vector3 grip = rHand.position + poleDir * GripSeparation;
            for (int pass = 0; pass < 2; pass++)
            {
                Transform? lShoulder = animator.GetBoneTransform(HumanBodyBones.LeftUpperArm);
                Transform? lElbow = animator.GetBoneTransform(HumanBodyBones.LeftLowerArm);
                Transform? lWrist = animator.GetBoneTransform(HumanBodyBones.LeftHand);
                if (lShoulder == null || lElbow == null || lWrist == null) break;
                AimSegment(animator, HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm, grip - lShoulder.position, weight);
                AimSegment(animator, HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftHand, grip - lElbow.position, weight);
            }
            OrientHand(animator, HumanBodyBones.LeftHand, poleDir, sweptRight, weight);
        }
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
