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
    public const string AnimFolder = Folder + "/Animations";
    // In Resources so the bootstrap can Resources.Load it at runtime.
    const string OutPath = Folder + "/Resources/CharacterAnimator.controller";

    /// <summary>True while <see cref="Build"/> runs. The auto-rebuild postprocessor
    /// (CharacterAnimatorAutoBuilder) checks this so Build's own ForceUpdate reimports don't
    /// re-trigger it recursively.</summary>
    public static bool IsBuilding { get; private set; }

    [MenuItem("Tools/RooftopTag/Build Character Animator")]
    public static void Build()
    {
        IsBuilding = true;
        try { BuildInternal(); }
        finally { IsBuilding = false; }
    }

    static void BuildInternal()
    {
        // Make sure loop/humanoid import settings are current before we reference the clips.
        foreach (string g in AssetDatabase.FindAssets("t:Model", new[] { AnimFolder }))
            AssetDatabase.ImportAsset(AssetDatabase.GUIDToAssetPath(g), ImportAssetOptions.ForceUpdate);
        AssetDatabase.Refresh();

        var ctrl = AnimatorController.CreateAnimatorControllerAtPath(OutPath);
        ctrl.AddParameter("Speed", AnimatorControllerParameterType.Float);
        // Local-space velocity components (m/s): + forward / + right. Drive the 2D grounded blend so
        // strafing (A/D) and backpedalling (S) animate correctly even though the player body stays
        // locked to the camera facing (see CharacterMotor.UpdateFacing).
        ctrl.AddParameter("ForwardSpeed", AnimatorControllerParameterType.Float);
        ctrl.AddParameter("StrafeSpeed", AnimatorControllerParameterType.Float);
        ctrl.AddParameter("VerticalSpeed", AnimatorControllerParameterType.Float);
        ctrl.AddParameter("MotorState", AnimatorControllerParameterType.Int);
        ctrl.AddParameter("AirDiving", AnimatorControllerParameterType.Bool);
        ctrl.AddParameter("Flipping", AnimatorControllerParameterType.Bool);
        ctrl.AddParameter("Diving", AnimatorControllerParameterType.Bool);
        // Catching selects the tagger's finishing-move variant of the dive (DivingCatch) over the
        // generic DiveRoll. Both share the Diving bool/window; Catching just picks the clip.
        ctrl.AddParameter("Catching", AnimatorControllerParameterType.Bool);
        // Eating (bin objective): Eating held for the whole crouch sequence; EatStop pulses the stand-up.
        ctrl.AddParameter("Eating", AnimatorControllerParameterType.Bool);
        ctrl.AddParameter("EatStop", AnimatorControllerParameterType.Bool);
        ctrl.AddParameter("EatStart", AnimatorControllerParameterType.Trigger);

        var sm = ctrl.layers[0].stateMachine;

        // Grounded: 2D freeform-directional blend over local velocity (strafe X, forward Z). Idle at
        // the centre, forward walk→run up +Z, backpedal down -Z, strafes on ±X.
        var grounded = ctrl.CreateBlendTreeInController("Grounded", out BlendTree groundTree, 0);
        groundTree.blendType = BlendTreeType.FreeformDirectional2D;
        groundTree.blendParameter = "StrafeSpeed";
        groundTree.blendParameterY = "ForwardSpeed";
        // Thresholds track MovementConfig walk (3.5) / sprint (7) so a clip is fully weighted at its speed.
        groundTree.AddChild(Clip("X Bot@Idle", "Idle", "Walking"), new Vector2(0f, 0f)); // Idle missing → Walking stopgap
        groundTree.AddChild(Clip("Walking"), new Vector2(0f, 3.5f));
        groundTree.AddChild(Clip("Fast Run", "Running"), new Vector2(0f, 7f));
        groundTree.AddChild(Clip("X Bot@Walking Backwards", "Walking Backwards"), new Vector2(0f, -3.5f));
        groundTree.AddChild(Clip("X Bot@Left Strafe", "Left Strafe"), new Vector2(-3.5f, 0f));
        groundTree.AddChild(Clip("X Bot@Right Strafe", "Right Strafe"), new Vector2(3.5f, 0f));

        // Airborne: 1D blend on vertical speed (rising = jump, falling = fall).
        var airborne = ctrl.CreateBlendTreeInController("Airborne", out BlendTree airTree, 0);
        airTree.blendType = BlendTreeType.Simple1D;
        airTree.blendParameter = "VerticalSpeed";
        airTree.AddChild(Clip("X Bot@Falling Idle", "Falling Idle"), -3f);
        airTree.AddChild(Clip("X Bot@Jumping", "Jump", "Jumping"), 3f);

        // Slide clip missing → dive-roll stopgap (reads as a floor tumble).
        var sliding = Simple(sm, "Sliding", Clip("X Bot@Running Slide", "Running Slide", "X Bot@Stand To Roll"));
        // The clip is import-trimmed to the tight low-glide plateau (frames 10-22) and LOOPS with
        // loop-pose on (CharacterImportPostprocessor), so no state cycleOffset is needed — the state
        // plays the deep-crouch glide and, if the slide outlasts the clip, seamlessly cycles that low
        // pose instead of freezing on a stood-up recovery frame (Bug A fix).
        // Wall-run was removed from CharacterMotor on this line, so MotorState has no WallRunning value
        // and everything from Mantling on shifted down by one — the Any() indices below match the live enum.
        // Mantle/vault clip playback is SYNCED to the motor's transition durations. At 1x the clip's
        // "pull up" beat landed long after CharacterMotor had already placed the body on the ledge
        // (motor mantle = 0.3s, vault 0.08-0.18s speed-scaled; the clip is seconds long), so the model
        // visibly hauled itself up thin air. speed = clipLength / targetSeconds compresses the whole
        // clip into roughly the motor's window. Targets sit a touch above the motor durations so the
        // last frames ease out during the landing instead of hard-cutting.
        const float MantleAnimTargetSeconds = 0.4f;  // motor mantleDuration 0.3 + a short settle tail
        const float VaultAnimTargetSeconds = 0.25f;  // motor vault cap 0.18 + tail; a quick hop-over
        AnimationClip mantleClip = Clip("X Bot@Braced Hang To Crouch", "Climbing To Top");
        var mantling = Simple(sm, "Mantling", mantleClip);
        if (mantleClip != null) mantling.speed = mantleClip.length / MantleAnimTargetSeconds;
        // Vault clip missing → braced-hang mantle stopgap so it reads as a quick hop-over.
        // "Vault" listed first so Clip() self-heals (and logs the stopgap) once that clip is imported.
        AnimationClip vaultClip = Clip("Vault", "X Bot@Braced Hang To Crouch", "Climbing To Top");
        var vaulting = Simple(sm, "Vaulting", vaultClip);
        if (vaultClip != null) vaulting.speed = vaultClip.length / VaultAnimTargetSeconds;
        var climbing = Simple(sm, "Climbing", Clip("X Bot@Freehang Climb", "Climbing Up Wall", "Rope Climb"));
        var ladder = Simple(sm, "OnLadder", Clip("X Bot@Climbing Ladder", "Climbing Ladder"));
        var swing = Simple(sm, "OnSwing", Clip("X Bot@Rope Swinging", "Rope Swinging"));
        // Wall grab = hang on the wall. Prefer the dedicated "Hanging Idle" clip; the Freehang Climb
        // brace-hold is the fallback until it's imported (Clip() self-heals and logs the stopgap).
        var wallHook = Simple(sm, "WallHook", Clip("Hanging Idle", "X Bot@Freehang Climb", "Rope Swinging"));

        // Front flip: replaces the normal jump/fall pose while airborne. Driven by CharacterAnimatorBridge,
        // which sets the Flipping bool the moment a runner double-jumps (and holds it for the clip length).
        var frontFlip = Simple(sm, "FrontFlip", Clip("X Bot@Front Flip", "Front Flip"));
        frontFlip.speed = 1.6f; // sped up so the flip snaps to the double-jump, but eased back a touch from 2x per feel-test

        // Dive roll: a tagger's committed lunge. Driven by the bridge's Diving bool (held for the clip
        // length) so the grounded/airborne AnyState transitions can't yank it back mid-roll.
        // Prefer the dedicated "X Bot@Dive Roll" clip (a real forward dive) over the old
        // Stand-To-Roll stopgap; the stopgaps stay as fallbacks so Clip() self-heals if it's removed.
        var diveRoll = Simple(sm, "DiveRoll", Clip("X Bot@Dive Roll", "X Bot@Stand To Roll", "Dive Roll"));
        // Dive Roll is import-trimmed (CharacterImportPostprocessor: DiveFirstFrame=41, DiveLastFrame=108,
        // 67 frames @30fps = 2.233s) to the launch-coil-bottom through upright-recovery window, cutting
        // the frozen standing wind-up (f0-19) and the dead idle-settle tail (f108-118). The motor's
        // committed dive window is a fixed 0.8s (diveDuration / CharacterAnimatorBridge.DiveHoldSeconds),
        // so the trimmed clip must be played back at trimmedSeconds/0.8 to land the whole roll inside
        // that window: 2.233s / 0.8s ≈ 2.79x. (If the trim length or the dive window changes, recompute
        // this from the new frame range / window — it is NOT a fixed constant.)
        diveRoll.speed = 2.233f / 0.8f;

        // Diving catch: the TAGGER's finishing-move variant of the lunge (same BeginDive, same tag
        // window — only the clip differs). Import-trimmed (CharacterImportPostprocessor: CatchFirstFrame=12,
        // CatchLastFrame=72, 60 frames @30fps = 2.000s) to the launch → dive → floor-catch-impact window,
        // cutting the standing windup (f0-11) and the dead lie-still prone tail (f72-108). Same 0.8s
        // committed-dive window as the roll (diveDuration / CharacterAnimatorBridge.DiveHoldSeconds), so
        // it is played back at trimmedSeconds/0.8 to land the whole catch inside that window:
        // 2.000s / 0.8s = 2.5x. (Recompute from the frame range / window if either changes — NOT a fixed constant.)
        // STOPGAP (user): the DivingCatch clip doesn't read right on the pest_control model, so the
        // catch state plays the FIRST HALF of the proven trimmed Dive Roll instead — launch coil into
        // the airborne forward dive, which reads as "diving after you". speed covers half the trimmed
        // clip (2.233s / 2) across the 0.8s dive window, so the state exits mid-clip right at the
        // dive's deepest point, before the roll-recovery half ever plays. The DivingCatch clip stays
        // as the fallback candidate so restoring it later is just re-ordering this list (and putting
        // the 2.000/0.8 speed back).
        var divingCatch = Simple(sm, "DivingCatch", Clip("X Bot@Dive Roll", "DivingCatch"));
        divingCatch.speed = 2.233f / 2f / 0.8f;

        // Eating (bin objective): Standing To Crouched -> Crouching Idle (loop) -> Crouched To Standing.
        // Crouching Idle loops (CharacterImportPostprocessor); the two transitions are one-shots. Driven
        // by the bridge's Eating bool (held through the WHOLE sequence, incl. the stand-up) plus EatStop
        // (pulsed when eating actually ends). Holding Eating true through the exit is what keeps the
        // locomotion AnyState (guarded IfNot Eating below) from snatching the stand-up mid-play.
        var eatEnter = Simple(sm, "EatEnter", Clip("Standing To Crouched"));
        var eatLoop  = Simple(sm, "EatLoop", Clip("Crouching Idle"));
        var eatExit  = Simple(sm, "EatExit", Clip("Crouched To Standing"));
        eatEnter.speed = 1.5f; // snappy crouch-down (user: "fast standing to crouched")

        sm.defaultState = grounded;

        // Dive roll owns the moment whenever Diving is set (but NOT Catching — the tagger's finishing
        // catch takes that case), over any locomotion state. The other AnyState transitions guard
        // IfNot Diving, which already protects BOTH dive states (Catching implies Diving), so no extra
        // guard is needed on them.
        var diveT = sm.AddAnyStateTransition(diveRoll);
        diveT.hasExitTime = false;
        diveT.duration = 0.05f;
        diveT.canTransitionToSelf = false;
        diveT.AddCondition(AnimatorConditionMode.If, 0, "Diving");
        diveT.AddCondition(AnimatorConditionMode.IfNot, 0, "Catching");

        // Diving catch owns the moment when a TAGGER's lunge fires at a catchable victim (Diving AND
        // Catching), over any locomotion state and over the plain dive roll.
        var catchT = sm.AddAnyStateTransition(divingCatch);
        catchT.hasExitTime = false;
        catchT.duration = 0.05f;
        catchT.canTransitionToSelf = false;
        catchT.AddCondition(AnimatorConditionMode.If, 0, "Diving");
        catchT.AddCondition(AnimatorConditionMode.If, 0, "Catching");

        // AnyState → EatEnter when eating begins (and not already standing back up).
        var eatT = sm.AddAnyStateTransition(eatEnter);
        eatT.hasExitTime = false;
        eatT.duration = 0.15f;
        eatT.canTransitionToSelf = false;
        eatT.AddCondition(AnimatorConditionMode.If, 0, "EatStart");
        // Crouch-down finishes → hold the rummage loop.
        var eatEnterToLoop = eatEnter.AddTransition(eatLoop);
        eatEnterToLoop.hasExitTime = true;
        eatEnterToLoop.exitTime = 0.85f;
        eatEnterToLoop.duration = 0.1f;
        // Eating ends (EatStop pulse) → stand back up. Eating stays true through this clip (bridge hold),
        // so the grounded AnyState can't cut it short.
        var eatLoopToExit = eatLoop.AddTransition(eatExit);
        eatLoopToExit.hasExitTime = false;
        eatLoopToExit.duration = 0.1f;
        eatLoopToExit.AddCondition(AnimatorConditionMode.If, 0, "EatStop");
        // EatExit has no explicit exit: when the bridge finally drops Eating (after the stand-up), the
        // grounded AnyState (IfNot Eating) fires and returns to locomotion.

        // AnyState → each state, selected by the MotorState int (see MotorState enum order).
        // Grounded also requires NOT diving so the dive roll isn't interrupted while on the ground.
        var groundT = sm.AddAnyStateTransition(grounded);
        groundT.hasExitTime = false;
        groundT.duration = 0.08f;
        groundT.canTransitionToSelf = false;
        groundT.AddCondition(AnimatorConditionMode.Equals, 0, "MotorState");
        groundT.AddCondition(AnimatorConditionMode.IfNot, 0, "Diving");
        groundT.AddCondition(AnimatorConditionMode.IfNot, 0, "Eating"); // don't yank the eat crouch back to locomotion

        Any(sm, sliding, 1);
        // Airborne only when NOT flipping; the flip owns the airborne window when rolled.
        AddAirborne(sm, airborne, flipping: false);
        AddAirborne(sm, frontFlip, flipping: true);
        Any(sm, mantling, 3);
        Any(sm, vaulting, 4);
        Any(sm, climbing, 5);
        Any(sm, ladder, 6);
        Any(sm, swing, 7);
        Any(sm, wallHook, 8);

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
        // Never interrupt a committed dive roll — same guard as groundT (Bug C).
        t.AddCondition(AnimatorConditionMode.IfNot, 0, "Diving");
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
        // Never interrupt a committed dive roll — same guard as groundT (Bug C). A dive that goes
        // airborne (e.g. off a ledge) must finish its roll pose, not snap to the jump/fall blend.
        t.AddCondition(AnimatorConditionMode.IfNot, 0, "Diving");
        t.AddCondition(AnimatorConditionMode.IfNot, 0, "Eating"); // eating owns the pose (defensive; ground-probe flicker)
    }

    // Returns the first candidate clip that exists on disk. Later candidates are stopgaps for a
    // preferred clip that hasn't been imported yet; using a fallback logs a warning so missing
    // source clips stay visible in the build output.
    static AnimationClip Clip(params string[] candidates)
    {
        for (int i = 0; i < candidates.Length; i++)
        {
            string path = $"{AnimFolder}/{candidates[i]}.fbx";
            foreach (var o in AssetDatabase.LoadAllAssetsAtPath(path))
                if (o is AnimationClip c && !c.name.StartsWith("__preview"))
                {
                    if (i > 0)
                        Debug.LogWarning($"ANIMATOR_CLIP_STOPGAP '{candidates[0]}' missing → using '{candidates[i]}'");
                    return c;
                }
        }
        Debug.LogError($"ANIMATOR_MISSING_CLIP {string.Join(" | ", candidates)}");
        return null;
    }
}
