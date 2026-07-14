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
        // Running Slide is deliberately NOT here: the slide is a one-shot, not continuous motion.
        // Looping it wrapped the clip back to its run-up mid-slide (the state can outlast the clip),
        // which snapped the limbs — the in-game "flail". Non-looping, it holds on the final frame.
        // Legacy names kept so older/re-added clips still loop if present.
        "Idle", "Running", "Rope Swinging",
        "Climbing Ladder", "Rope Climb", "Falling Idle",
    };

    // Running Slide trim knobs. The Mixamo clip is 46 frames: it opens with a multi-stride run-up
    // and ends standing back up. In-game slides are brief, so trim at import to just the slide
    // itself — start past the run-up, end before the stand-up recovery. With the clip non-looping,
    // a slide that outlasts it then freezes on the LOW SLIDE pose instead of the run-up or the
    // stood-up recovery. Tune these two if the entry/freeze pose reads wrong.
    const string SlideClip = "X Bot@Running Slide";
    const int SlideFirstFrame = 18; // past the ~40% run-up
    const int SlideLastFrame = 40;  // before the stand-up recovery

    void OnPreprocessAnimation()
    {
        if (!assetPath.StartsWith(CharacterFolder)) return;

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
            }
        }
        if (clips.Length > 0)
            importer.clipAnimations = clips;
    }

    void OnPreprocessModel()
    {
        if (!assetPath.StartsWith(CharacterFolder)) return;

        var importer = (ModelImporter)assetImporter;
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
