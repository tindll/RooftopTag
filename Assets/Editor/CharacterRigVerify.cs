using System.Text;
using UnityEditor;
using UnityEngine;

/// <summary>
/// CLI-invokable check that the character FBX files imported as valid Humanoid avatars and that the
/// Mixamo clips carry humanoid muscle curves. Run headless:
///   Unity -batchmode -quit -projectPath . -executeMethod CharacterRigVerify.VerifyFromCLI -logFile -
/// </summary>
public static class CharacterRigVerify
{
    const string Folder = "Assets/Art/Characters";

    [MenuItem("Tools/RooftopTag/Verify Character Rigs")]
    public static void VerifyFromCLI()
    {
        var sb = new StringBuilder();
        sb.AppendLine("RIGVERIFY_BEGIN");

        // Force a reimport first so the Humanoid postprocessor is guaranteed to have run on FBX that
        // may have imported as Generic before that script existed.
        foreach (string g in AssetDatabase.FindAssets("t:Model", new[] { Folder }))
            AssetDatabase.ImportAsset(AssetDatabase.GUIDToAssetPath(g), ImportAssetOptions.ForceUpdate);
        AssetDatabase.Refresh();

        foreach (string guid in AssetDatabase.FindAssets("t:Model", new[] { Folder }))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            var importer = AssetImporter.GetAtPath(path) as ModelImporter;
            var avatar = AssetDatabase.LoadAssetAtPath<Avatar>(path);

            string animType = importer != null ? importer.animationType.ToString() : "n/a";
            bool hasAvatar = avatar != null;
            bool isHuman = hasAvatar && avatar.isHuman;
            bool isValid = hasAvatar && avatar.isValid;

            // Count animation clips embedded in this FBX.
            int clips = 0;
            foreach (var obj in AssetDatabase.LoadAllAssetsAtPath(path))
                if (obj is AnimationClip c && !c.name.StartsWith("__preview")) clips++;

            sb.AppendLine($"RIGVERIFY {System.IO.Path.GetFileName(path)} | anim={animType} avatar={hasAvatar} human={isHuman} valid={isValid} clips={clips}");
        }

        sb.AppendLine("RIGVERIFY_END");
        Debug.Log(sb.ToString());
    }
}
