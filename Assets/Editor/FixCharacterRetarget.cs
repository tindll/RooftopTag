#nullable enable

using System.IO;
using UnityEditor;
using UnityEngine;

namespace Game.EditorTools
{
    /// <summary>
    /// Resets every rigged FBX under Assets/Art/Characters to Humanoid + CreateFromThisModel and
    /// reimports. Use it to clear a broken Copy-From-Other-Avatar state (the Tripo models and Mixamo
    /// clips have different transform hierarchies, so copy fails with a "Rig Configuration mis-match"
    /// and leaves the clips without a valid avatar).
    ///
    /// This does NOT by itself fix flailing limbs — that comes from the Tripo MODELS importing in an
    /// A-pose, which makes their auto-generated Humanoid T-pose wrong on the arms. After running this,
    /// fix each model manually: select raccoon.fbx (and pest_control.fbx) > Rig tab > Configure
    /// Avatar > Pose > Enforce T-Pose, correct any drooped arm/leg, then Apply. The Mixamo clips'
    /// own T-poses are already correct, so once the model T-pose is right the retarget is clean.
    /// </summary>
    public static class FixCharacterRetarget
    {
        private const string CharactersRoot = "Assets/Art/Characters";

        [MenuItem("Tools/RooftopTag/Reset Character Rigs (CreateFromThisModel)")]
        public static void Run()
        {
            int count = 0;
            foreach (string guid in AssetDatabase.FindAssets("t:Model", new[] { CharactersRoot }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (!path.EndsWith(".fbx")) continue;
                if (AssetImporter.GetAtPath(path) is not ModelImporter mi) continue;

                mi.animationType = ModelImporterAnimationType.Human;
                mi.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
                mi.sourceAvatar = null;
                mi.SaveAndReimport();
                count++;
                Debug.Log($"RIG_RESET: {Path.GetFileName(path)} -> CreateFromThisModel");
            }

            AssetDatabase.Refresh();
            Debug.Log($"RIG_RESET_DONE: reset {count} FBX(s). Next: Configure Avatar > Enforce T-Pose " +
                      "on raccoon.fbx and pest_control.fbx to fix the arm/leg flail.");
        }
    }
}
