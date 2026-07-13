#nullable enable

using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Game.EditorTools
{
    /// <summary>
    /// Fixes the "flailing arms/legs" Mixamo retarget bug. Root cause: CharacterImportPostprocessor
    /// forced avatarSetup=CreateFromThisModel on EVERY FBX, so each of the 16 animation clips (and
    /// the pest_control model) built its OWN Humanoid avatar with its OWN T-pose reference. Those
    /// per-file T-poses disagree (Tripo meshes author an A-pose, not a clean T-pose), so muscle
    /// retarget applies a per-bone offset — worst on the arms — and the limbs flail.
    ///
    /// Fix: pick ONE canonical avatar (raccoon) and make every other rigged FBX — the pest_control
    /// model and all animation clips — Copy From Other Avatar off it. With a single shared T-pose
    /// reference, retarget offset is zero and the clips play as authored on both characters.
    ///
    /// Run this ONCE from the menu after the assets are imported (the Editor can be open — it
    /// reimports live). Regenerating the avatars by hand via Rig → Configure Avatar is the manual
    /// equivalent; this just does it for all 17 files in the right order.
    /// </summary>
    public static class FixCharacterRetarget
    {
        private const string CharactersRoot = "Assets/Art/Characters";
        private const string CanonicalModel = CharactersRoot + "/Resources/raccoon.fbx";

        [MenuItem("Tools/RooftopTag/Fix Character Rig Retarget (unify avatars)")]
        public static void Run()
        {
            // 1. Ensure the canonical model owns a freshly-built Humanoid avatar.
            var canonical = AssetImporter.GetAtPath(CanonicalModel) as ModelImporter;
            if (canonical == null)
            {
                Debug.LogError($"RETARGET_FIX: canonical model not found at {CanonicalModel}");
                return;
            }
            canonical.animationType = ModelImporterAnimationType.Human;
            canonical.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
            canonical.SaveAndReimport();

            Avatar? sourceAvatar = AssetDatabase
                .LoadAllAssetsAtPath(CanonicalModel)
                .OfType<Avatar>()
                .FirstOrDefault();
            if (sourceAvatar == null)
            {
                Debug.LogError("RETARGET_FIX: raccoon avatar did not generate — is the model a valid Humanoid?");
                return;
            }

            // 2. Every OTHER rigged FBX copies that single avatar (shared T-pose = no retarget offset).
            var targets = new List<string>();
            foreach (string guid in AssetDatabase.FindAssets("t:Model", new[] { CharactersRoot }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (!path.EndsWith(".fbx")) continue;
                if (path == CanonicalModel) continue; // it IS the source
                targets.Add(path);
            }

            int fixedCount = 0;
            foreach (string path in targets)
            {
                if (AssetImporter.GetAtPath(path) is not ModelImporter mi) continue;
                mi.animationType = ModelImporterAnimationType.Human;
                mi.avatarSetup = ModelImporterAvatarSetup.CopyFromOtherAvatar;
                mi.sourceAvatar = sourceAvatar;
                mi.SaveAndReimport();
                fixedCount++;
                Debug.Log($"RETARGET_FIX: {Path.GetFileName(path)} -> Copy From raccoonAvatar");
            }

            AssetDatabase.Refresh();
            Debug.Log($"RETARGET_FIX_DONE: unified {fixedCount} FBX(s) onto raccoonAvatar. " +
                      "Enter Play mode and check walk/run — arms/legs should no longer flail.");
        }
    }
}
