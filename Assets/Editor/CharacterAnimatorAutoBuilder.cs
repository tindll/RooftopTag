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
///
/// STALE-CODE GUARD: when one import batch delivers BOTH an FBX and an edit to the builder script
/// itself (a git pull), the postprocessor callback runs on the OLD compiled assemblies — rebuilding
/// immediately would bake the controller with outdated builder code (exactly how a pulled DivingCatch
/// state went missing). So the rebuild request is persisted in SessionState and only executed when no
/// compile is pending; the InitializeOnLoad hook re-checks after every domain reload, so a rebuild
/// deferred across a recompile still runs — now with the fresh code.
/// </summary>
public sealed class CharacterAnimatorAutoBuilder : AssetPostprocessor
{
    const string PendingKey = "RooftopTag.AnimatorRebuildPending";

    static void OnPostprocessAllAssets(
        string[] imported, string[] deleted, string[] moved, string[] movedFrom)
    {
        // Ignore the reimports Build itself fires (ForceUpdate on every model) — reacting to those
        // would recurse. IsBuilding is true for the whole synchronous Build call.
        if (BuildCharacterAnimator.IsBuilding) return;

        if (!imported.Concat(deleted).Concat(moved).Any(IsAnimationFbx)) return;

        SessionState.SetBool(PendingKey, true);
        ScheduleDeferredBuild();
    }

    // Runs after every domain reload — picks up a rebuild that was deferred because scripts were
    // still compiling when the FBX import landed (see STALE-CODE GUARD in the class summary).
    [InitializeOnLoadMethod]
    static void OnScriptsReloaded() => ScheduleDeferredBuild();

    static void ScheduleDeferredBuild()
    {
        if (!SessionState.GetBool(PendingKey, false)) return;

        // delayCall: rebuilding (which reimports assets) must run OUTSIDE import callbacks, and it
        // also debounces a multi-file import batch into one rebuild.
        EditorApplication.delayCall += () =>
        {
            if (!SessionState.GetBool(PendingKey, false)) return; // already handled by another schedule
            if (EditorApplication.isCompiling)
                return; // stale assemblies — leave the flag set; the post-reload hook re-schedules
            SessionState.SetBool(PendingKey, false);
            BuildCharacterAnimator.Build();
        };
    }

    static bool IsAnimationFbx(string path) =>
        path.StartsWith(BuildCharacterAnimator.AnimFolder) &&
        path.EndsWith(".fbx", System.StringComparison.OrdinalIgnoreCase);
}
