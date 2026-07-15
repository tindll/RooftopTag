using System.Linq;
using UnityEditor;

/// <summary>
/// Auto-regenerates CharacterAnimator.controller whenever an animation FBX under
/// <see cref="BuildCharacterAnimator.AnimFolder"/> is (re)imported, added, moved, or deleted.
///
/// The controller is a GENERATED artifact that references AnimationClip sub-assets inside the FBXs.
/// A reimport of those FBXs (adding clips, a Library rebuild, an import-setting change) can leave the
/// committed controller pointing at stale clip references — the "animations break, rebuild fixes it"
/// symptom. Rebuilding on the triggering import keeps the controller in sync automatically instead of
/// relying on a manual Tools/RooftopTag/Build Character Animator every time.
/// </summary>
public sealed class CharacterAnimatorAutoBuilder : AssetPostprocessor
{
    static bool _pending;

    static void OnPostprocessAllAssets(
        string[] imported, string[] deleted, string[] moved, string[] movedFrom)
    {
        // Ignore the reimports Build itself fires (ForceUpdate on every model) — reacting to those
        // would recurse. IsBuilding is true for the whole synchronous Build call.
        if (BuildCharacterAnimator.IsBuilding) return;
        if (_pending) return;

        bool clipsChanged = imported.Concat(deleted).Concat(moved).Any(IsAnimationFbx);
        if (!clipsChanged) return;

        // Defer to the next editor tick: rebuilding (which reimports assets) must run OUTSIDE this
        // import callback, and delayCall also debounces a multi-file import batch into one rebuild.
        _pending = true;
        EditorApplication.delayCall += () =>
        {
            _pending = false;
            BuildCharacterAnimator.Build();
        };
    }

    static bool IsAnimationFbx(string path) =>
        path.StartsWith(BuildCharacterAnimator.AnimFolder) &&
        path.EndsWith(".fbx", System.StringComparison.OrdinalIgnoreCase);
}
