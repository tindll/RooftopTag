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
        "Idle", "Walking", "Running", "Running Slide", "Rope Swinging",
        "Wall Run", "Climbing Ladder", "Rope Climb", "Falling Idle",
    };

    void OnPreprocessAnimation()
    {
        if (!assetPath.StartsWith(CharacterFolder)) return;

        var importer = (ModelImporter)assetImporter;
        bool loop = LoopClips.Contains(Path.GetFileNameWithoutExtension(assetPath));
        var clips = importer.defaultClipAnimations;
        for (int i = 0; i < clips.Length; i++)
            clips[i].loopTime = loop;
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
