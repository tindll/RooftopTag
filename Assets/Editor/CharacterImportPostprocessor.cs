using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Forces every FBX under Assets/Art/Characters to import as a Humanoid rig so the Tripo character
/// meshes and the Mixamo animation clips share Unity's humanoid abstraction and retarget onto each
/// other. Without this, each FBX defaults to Generic and Mixamo clips won't drive the Tripo skeleton.
/// Also marks the continuous locomotion clips as looping.
/// </summary>
public sealed class CharacterImportPostprocessor : AssetPostprocessor
{
    const string CharacterFolder = "Assets/Art/Characters";

    // Clips that should loop (continuous motion). Everything else stays one-shot (jump, landing,
    // mantle, vault). Keyed by FBX file name without extension.
    static readonly HashSet<string> LoopClips = new()
    {
        // New Mixamo X Bot locomotion + hold clips (continuous motion).
        "Fast Run", "Walking", "X Bot@Walking Backwards",
        "X Bot@Left Strafe", "X Bot@Right Strafe",
        "X Bot@Falling Idle", "X Bot@Rope Swinging",
        "X Bot@Climbing Ladder", "X Bot@Freehang Climb",
        "X Bot@Idle",
        // Running Slide is NOT here — its loop flags are set explicitly in the slide branch below
        // (loopTime + loopPose on the tight low-glide window), not via this whole-clip loop set.
        // The old "never loop the slide" note stood because looping the whole 46-frame clip wrapped
        // from the stand-up recovery back to the run-up mid-slide and snapped the limbs (the "flail").
        // That no longer applies now the trim is a tight deep-crouch sub-window (see SlideFirst/Last).
        // Legacy names kept so older/re-added clips still loop if present.
        "Idle", "Running", "Rope Swinging",
        "Climbing Ladder", "Rope Climb", "Falling Idle",
    };

    // Running Slide trim knobs. The Mixamo clip is 46 frames (30fps). Per-frame Hips/Head world-Y,
    // measured by CharacterPreviewShot.SlideFrames (humanoid-retargeted onto the raccoon):
    //   frames  0- 2  standing         (hips ~0.40, head ~0.72)
    //   frames  3- 9  drop into slide  (hips 0.39 -> 0.11)
    //   frames 10-22  LOW GLIDE plateau (hips 0.08-0.13, head 0.27-0.33) — deepest ~f11/f16
    //   frames 23-45  stand back up    (hips 0.15 -> 0.40)
    // The prior trim (18-40) sat ENTIRELY on the stand-up ramp, so a slide that outlasted the clip
    // froze on frame 40 — the MOST upright pose — while CharacterMotor kept the capsule shrunk to
    // 0.5x + recentred: feet ended up below the lowered root → "sink into floor, sliding standing
    // up" (Bug A). Fix: trim to the flat deep-crouch plateau (10-22) and LOOP it with loop-pose ON
    // (loopTime + loopPose, set in OnPreprocessAnimation below). The window is low start-to-end and
    // near-static, so the loop-pose-matched wrap is seamless — a long slide reads as a continuous
    // low glide instead of freezing on a stood-up frame. Re-render via SlideFrames if retuning.
    const string SlideClip = "X Bot@Running Slide";
    const int SlideFirstFrame = 10; // first frame of the low-glide plateau (settled deep crouch)
    const int SlideLastFrame = 22;  // last plateau frame before the stand-up recovery (f23+) begins

    // Static prop bins living under the Characters folder — no skeleton/animation, must stay
    // Generic or Unity throws trying to build a Humanoid avatar for them.
    static readonly HashSet<string> StaticPropBins = new() { "big_bin", "small_bin" };

    void OnPreprocessAnimation()
    {
        if (!assetPath.StartsWith(CharacterFolder)) return;
        if (StaticPropBins.Contains(Path.GetFileNameWithoutExtension(assetPath))) return;

        var importer = (ModelImporter)assetImporter;
        string fileName = Path.GetFileNameWithoutExtension(assetPath);
        bool loop = LoopClips.Contains(fileName);
        var clips = importer.defaultClipAnimations;
        for (int i = 0; i < clips.Length; i++)
        {
            clips[i].loopTime = loop;
            if (fileName == SlideClip)
            {
                clips[i].firstFrame = SlideFirstFrame;
                clips[i].lastFrame = SlideLastFrame;
                // Loop the tight low-glide window with loop-pose matching so a slide that outlasts
                // the clip cycles a seamless continuous low crouch instead of freezing on one frame
                // (loopPose serializes as loopBlend in the .meta). See SlideFirst/LastFrame notes.
                clips[i].loopTime = true;
                clips[i].loopPose = true;
            }
        }
        if (clips.Length > 0)
            importer.clipAnimations = clips;
    }

    void OnPreprocessModel()
    {
        if (!assetPath.StartsWith(CharacterFolder)) return;

        var importer = (ModelImporter)assetImporter;

        // Static prop bins (trash cans) sit in this folder but have no rig — leave them Generic
        // and skip before the Humanoid avatar setup below, which would fail/misbuild for them.
        if (StaticPropBins.Contains(Path.GetFileNameWithoutExtension(assetPath)))
        {
            importer.animationType = ModelImporterAnimationType.Generic;
            return;
        }

        importer.animationType = ModelImporterAnimationType.Human;
        // Each FBX builds its own Humanoid avatar. Copy-From-Other-Avatar is NOT usable here: the
        // Tripo models and the Mixamo clips have different transform hierarchies (model root under
        // 'Armature', each clip root under its own '<ClipName>' node), and copy requires an exact
        // hierarchy match. Humanoid retarget bridges the difference through muscle space instead —
        // provided each avatar's T-pose is correct. The flail comes from the Tripo MODELS importing
        // in an A-pose; fix that per-model with Rig > Configure Avatar > Enforce T-Pose (the clips'
        // Mixamo T-poses are already correct).
        importer.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;

        // Tripo embeds textures/materials inside the FBX; import them in-place (Unity 6 references the
        // embedded textures directly — "External" material location was removed). Extract via the
        // model's Materials tab later only if you need to hand-edit them.
        importer.materialImportMode = ModelImporterMaterialImportMode.ImportStandard;
    }
}
