#nullable enable

using Game.Movement;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Shared interactive-rebind state machine used by both <see cref="SettingsMenu"/> and
/// <see cref="MainMenuOverlay"/> (extracted from SettingsMenu so the main menu's CONTROLS dropdown
/// doesn't copy-paste the listen/complete/cancel flow). Static because at most one listen can ever
/// be active at a time — starting a new one cancels the old — and both menus must see the same
/// "someone is listening" state to grey out their own Rebind buttons.
///
/// Escape cancels the active listen (via WithCancelingThrough). Callers that ALSO act on Escape
/// (SettingsMenu's pause toggle) must check <see cref="EscapeConsumedThisFrame"/> first so the same
/// press doesn't both cancel the listen and toggle their menu — the cancel callback fires during
/// the input update, before MonoBehaviour.Update sees wasPressedThisFrame.
///
/// Persistence: completion saves through <see cref="PlayerInputProvider.SaveBindingOverride"/>, the
/// same PlayerPrefs pattern every rebindable action (PlayerInputProvider's four and TagAgent's
/// Lunge/Tag) loads from at creation time.
/// </summary>
public static class KeyRebinder
{
    // RebindingOperation is a nested type of InputActionRebindingExtensions, not a top-level type
    // in UnityEngine.InputSystem — hence the qualified name here despite the `using` above.
    private static InputActionRebindingExtensions.RebindingOperation? _op;
    private static int _cancelFrame = -1;

    /// <summary>The action currently listening for a new binding, or null when idle.</summary>
    public static InputAction? Listening { get; private set; }

    /// <summary>True only on the frame a listen was cancelled — Escape handlers skip their own
    /// action then, so the cancelling press doesn't also close/toggle a menu.</summary>
    public static bool EscapeConsumedThisFrame => Time.frameCount == _cancelFrame;

    /// <summary>
    /// Interactively rebinds the action's binding at index 0 (every rebindable action in this
    /// project is built keyboard/mouse-first with a gamepad binding appended at index 1 — see
    /// PlayerInputProvider.Awake and TagAgent.Configure; only the keyboard/mouse side is remapped).
    /// The action is disabled for the duration, matching the pattern used by Unity's own
    /// RebindingUI sample (rebinding an enabled action throws).
    /// </summary>
    public static void Start(InputAction action)
    {
        Cancel();
        Listening = action;
        action.Disable();

        _op = action.PerformInteractiveRebinding(bindingIndex: 0)
            .WithControlsExcluding("<Mouse>/position")
            .WithControlsExcluding("<Mouse>/delta")
            .WithCancelingThrough("<Keyboard>/escape")
            .OnComplete(_ =>
            {
                Finish(action);
                PlayerInputProvider.SaveBindingOverride(action);
            })
            .OnCancel(_ =>
            {
                _cancelFrame = Time.frameCount;
                Finish(action);
            })
            .Start();
    }

    /// <summary>Cancels any in-flight listen. Safe to call when idle — used by menu close paths and
    /// OnDestroy so a listen never outlives its UI. Cleanup happens in the OnCancel callback.</summary>
    public static void Cancel() => _op?.Cancel();

    private static void Finish(InputAction action)
    {
        _op?.Dispose();
        _op = null;
        Listening = null;
        action.Enable();
    }
}
