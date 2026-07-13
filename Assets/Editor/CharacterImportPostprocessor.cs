using System.Collections.Generic;
using System.IO;
using System.Linq;
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

    // One canonical Humanoid avatar for the whole character set. The Tripo meshes author an A-pose,
    // so letting each FBX build its own avatar (CreateFromThisModel) gave 17 disagreeing T-pose
    // references and the Mixamo clips retargeted into flailing arms/legs. Instead: raccoon owns the
    // avatar; every other model + every animation clip copies it, so all share one T-pose and
    // retarget offset is zero. See FixCharacterRetarget for the one-shot menu that applies this.
    const string CanonicalModel = CharacterFolder + "/Resources/raccoon.fbx";

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

        if (assetPath == CanonicalModel)
        {
            importer.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
        }
        else
        {
            // Copy the shared raccoon avatar so this FBX's clips retarget through one consistent
            // T-pose. Fall back to CreateFromThisModel only if raccoon hasn't imported yet (a clean
            // full reimport where order isn't guaranteed) — run Tools/RooftopTag/Fix Character Rig
            // Retarget once afterwards to unify everything in the right order.
            var source = AssetDatabase
                .LoadAllAssetsAtPath(CanonicalModel)
                .OfType<Avatar>()
                .FirstOrDefault();
            if (source != null)
            {
                importer.avatarSetup = ModelImporterAvatarSetup.CopyFromOther;
                importer.sourceAvatar = source;
            }
            else
            {
                importer.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
            }
        }

        // Tripo embeds textures/materials inside the FBX; import them in-place (Unity 6 references the
        // embedded textures directly — "External" material location was removed). Extract via the
        // model's Materials tab later only if you need to hand-edit them.
        importer.materialImportMode = ModelImporterMaterialImportMode.ImportStandard;
    }
}
