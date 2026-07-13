using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

/// <summary>
/// Generates Assets/Art/Characters/CharacterAnimator.controller from the imported Mixamo clips,
/// wired to CharacterMotor's state via four parameters:
///   Speed (float)         — horizontal m/s, drives the grounded idle/walk/run blend
///   VerticalSpeed (float) — rigidbody Y, drives the airborne jump/fall blend
///   MotorState (int)      — the MotorState enum value, selects the active state
///   AirDiving (bool)      — reserved for a dedicated dive pose later
/// One controller drives both characters (shared humanoid avatar).
/// Run headless: Unity -batchmode -quit -executeMethod BuildCharacterAnimator.Build
/// </summary>
public static class BuildCharacterAnimator
{
    const string Folder = "Assets/Art/Characters";
    const string AnimFolder = Folder + "/Animations";
    // In Resources so the bootstrap can Resources.Load it at runtime.
    const string OutPath = Folder + "/Resources/CharacterAnimator.controller";

    [MenuItem("Tools/RooftopTag/Build Character Animator")]
    public static void Build()
    {
        // Make sure loop/humanoid import settings are current before we reference the clips.
        foreach (string g in AssetDatabase.FindAssets("t:Model", new[] { AnimFolder }))
            AssetDatabase.ImportAsset(AssetDatabase.GUIDToAssetPath(g), ImportAssetOptions.ForceUpdate);
        AssetDatabase.Refresh();

        var ctrl = AnimatorController.CreateAnimatorControllerAtPath(OutPath);
        ctrl.AddParameter("Speed", AnimatorControllerParameterType.Float);
        ctrl.AddParameter("VerticalSpeed", AnimatorControllerParameterType.Float);
        ctrl.AddParameter("MotorState", AnimatorControllerParameterType.Int);
        ctrl.AddParameter("AirDiving", AnimatorControllerParameterType.Bool);
        ctrl.AddParameter("Flipping", AnimatorControllerParameterType.Bool);

        var sm = ctrl.layers[0].stateMachine;

        // Grounded: 1D blend on horizontal speed.
        var grounded = ctrl.CreateBlendTreeInController("Grounded", out BlendTree groundTree, 0);
        groundTree.blendType = BlendTreeType.Simple1D;
        groundTree.blendParameter = "Speed";
        groundTree.AddChild(Clip("Idle"), 0f);
        groundTree.AddChild(Clip("Walking"), 4f);
        groundTree.AddChild(Clip("Running"), 8f);

        // Airborne: 1D blend on vertical speed (rising = jump, falling = fall).
        var airborne = ctrl.CreateBlendTreeInController("Airborne", out BlendTree airTree, 0);
        airTree.blendType = BlendTreeType.Simple1D;
        airTree.blendParameter = "VerticalSpeed";
        airTree.AddChild(Clip("Falling Idle"), -3f);
        airTree.AddChild(Clip("Jump"), 3f);

        var sliding = Simple(sm, "Sliding", Clip("Running Slide"));
        var wallRun = Simple(sm, "WallRunning", Clip("Wall Run"));
        var mantling = Simple(sm, "Mantling", Clip("Climbing Up Wall"));
        var vaulting = Simple(sm, "Vaulting", Clip("Climbing To Top"));
        vaulting.speed = 1.5f; // stand-in for a real vault — sped up so the climb reads as a quick hop
        var climbing = Simple(sm, "Climbing", Clip("Rope Climb"));
        var ladder = Simple(sm, "OnLadder", Clip("Climbing Ladder"));
        var swing = Simple(sm, "OnSwing", Clip("Rope Swinging"));
        var wallHook = Simple(sm, "WallHook", Clip("Rope Swinging")); // reuse hang pose

        // Occasional flourish: a front flip that replaces the normal jump/fall pose while airborne,
        // chosen at random by the bridge (which holds the Flipping bool for the clip's length).
        var frontFlip = Simple(sm, "FrontFlip", Clip("Front Flip"));

        sm.defaultState = grounded;

        // AnyState → each state, selected by the MotorState int (see MotorState enum order).
        Any(sm, grounded, 0);
        Any(sm, sliding, 1);
        // Airborne only when NOT flipping; the flip owns the airborne window when rolled.
        AddAirborne(sm, airborne, flipping: false);
        AddAirborne(sm, frontFlip, flipping: true);
        Any(sm, wallRun, 3);
        Any(sm, mantling, 4);
        Any(sm, vaulting, 5);
        Any(sm, climbing, 6);
        Any(sm, ladder, 7);
        Any(sm, swing, 8);
        Any(sm, wallHook, 9);

        EditorUtility.SetDirty(ctrl);
        AssetDatabase.SaveAssets();
        Debug.Log($"ANIMATOR_BUILT states={sm.states.Length + 2} params={ctrl.parameters.Length} at {OutPath}");
    }

    static AnimatorState Simple(AnimatorStateMachine sm, string name, Motion clip)
    {
        var st = sm.AddState(name);
        st.motion = clip;
        return st;
    }

    static void Any(AnimatorStateMachine sm, AnimatorState target, int stateValue)
    {
        var t = sm.AddAnyStateTransition(target);
        t.hasExitTime = false;
        t.duration = 0.08f;
        t.canTransitionToSelf = false;
        t.AddCondition(AnimatorConditionMode.Equals, stateValue, "MotorState");
    }

    // Airborne (MotorState == 2) split by the Flipping bool so the flip and the normal fall/jump
    // pose never fight over the same AnyState trigger.
    static void AddAirborne(AnimatorStateMachine sm, AnimatorState target, bool flipping)
    {
        var t = sm.AddAnyStateTransition(target);
        t.hasExitTime = false;
        t.duration = flipping ? 0.05f : 0.08f;
        t.canTransitionToSelf = false;
        t.AddCondition(AnimatorConditionMode.Equals, 2, "MotorState");
        t.AddCondition(flipping ? AnimatorConditionMode.If : AnimatorConditionMode.IfNot, 0, "Flipping");
    }

    static AnimationClip Clip(string fileNoExt)
    {
        string path = $"{AnimFolder}/{fileNoExt}.fbx";
        foreach (var o in AssetDatabase.LoadAllAssetsAtPath(path))
            if (o is AnimationClip c && !c.name.StartsWith("__preview"))
                return c;
        Debug.LogError($"ANIMATOR_MISSING_CLIP {path}");
        return null;
    }
}
