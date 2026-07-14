#nullable enable

using Game.AI;
using Game.CameraSystem;
using Game.Movement;
using Game.Rules;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// IMGUI settings + pause overlay. F1 toggles a rebind/sensitivity window: rebind Jump/Slide/
/// Sprint/Interact and adjust mouse/keyboard camera sensitivity, persisted to PlayerPrefs. Escape
/// toggles a pause menu (Resume / Restart round / Settings / Quit) that freezes gameplay via
/// <c>Time.timeScale = 0</c> and frees the cursor so the buttons are clickable. F1 was chosen for
/// the settings window because it's untouched by every other binding in this project (WASD/mouse/
/// space/left-ctrl/left-shift/E in <see cref="PlayerInputProvider"/>, arrow-keys in
/// <see cref="ThirdPersonCameraRig"/>, R for playground/round reset), so it can never shadow an
/// existing control. Escape is owned exclusively here now — <see cref="ThirdPersonCameraRig"/>
/// used to read Escape directly to free the cursor; it now exposes
/// <see cref="ThirdPersonCameraRig.CursorUnlocked"/> and
/// <see cref="ThirdPersonCameraRig.SuppressAutoRelock"/> for this class to drive instead, so
/// Escape is read in exactly one place.
///
/// Deliberately has no namespace and lives outside any custom asmdef (compiles into
/// Assembly-CSharp), same as <see cref="PlaygroundBootstrap"/>/<see cref="TagArenaBootstrap"/>
/// which attach it live at runtime — see their remarks for why (this environment's headless Unity
/// cannot reliably resolve custom-asmdef script types when deserializing a saved scene).
///
/// Only ever attached to the local player's input/camera pair by the bootstraps, so it's naturally
/// absent from the headless self-play harness (<c>SelfPlayTests.cs</c> builds bot agents directly
/// in code and never calls into either bootstrap), matching how <c>RoundController</c>'s minimap
/// is gated behind <c>isLocalPlayer</c> for the same reason.
/// </summary>
public sealed class SettingsMenu : MonoBehaviour
{
    private const string MouseSensitivityPrefKey = "RooftopTag.Settings.MouseSensitivity";
    private const string KeyboardTurnSpeedPrefKey = "RooftopTag.Settings.KeyboardTurnSpeed";

    private const float MinMouseSensitivity = 0.5f;
    private const float MaxMouseSensitivity = 10f;
    private const float MinKeyboardTurnSpeed = 30f;
    private const float MaxKeyboardTurnSpeed = 300f;

    private PlayerInputProvider _input = null!;
    private ThirdPersonCameraRig _cameraRig = null!;
    private RoundController? _roundController;

    // Null when this menu is attached by PlaygroundBootstrap (no bots exist there) — the bot
    // difficulty row is only drawn when a bootstrap callback is actually wired up.
    private TagArenaBootstrap? _botDifficultyBootstrap;

    // Null when this menu is attached by PlaygroundBootstrap (no main menu exists there) — the
    // pause menu's Quit button falls back to a real Application.Quit in that case.
    private MainMenuOverlay? _mainMenu;

    private bool _open;
    private Rect _windowRect = new(20, 20, 320, 10);

    private bool _paused;

    // Fixed id: only one local player (and therefore one SettingsMenu instance) exists per scene,
    // so there's no risk of colliding with another IMGUI window in this project's OnGUI-only HUD.
    private const int PauseWindowId = 847022;

    // RebindingOperation is a nested type of InputActionRebindingExtensions, not a top-level type
    // in UnityEngine.InputSystem — hence the qualified name here despite the `using` above.
    private InputActionRebindingExtensions.RebindingOperation? _activeRebind;
    private InputAction? _rebindingAction;

    private float _mouseSensitivity;
    private float _keyboardTurnSpeed;

    /// <summary>
    /// Wires the menu to the local player's concrete input provider and camera rig. Must be called
    /// once, right after they are created. <paramref name="roundController"/> is null in scenes with
    /// no round (e.g. the movement playground) — the pause menu's Restart button is disabled there.
    /// <paramref name="botDifficultyBootstrap"/> is optional — pass it only when bots exist (Tag
    /// Arena / Rooftop Arena) to enable the live "Bot difficulty" row; PlaygroundBootstrap has no
    /// bots and omits it, so the row stays hidden there.
    /// <paramref name="mainMenu"/> is optional — pass it only where a <see cref="MainMenuOverlay"/>
    /// exists (Tag Arena / Rooftop Arena), so the pause menu's Quit button returns there instead of
    /// calling Application.Quit; PlaygroundBootstrap has no main menu and omits it.
    /// </summary>
    public void Configure(PlayerInputProvider input, ThirdPersonCameraRig cameraRig, RoundController? roundController, TagArenaBootstrap? botDifficultyBootstrap = null, MainMenuOverlay? mainMenu = null)
    {
        _input = input;
        _cameraRig = cameraRig;
        _roundController = roundController;
        _botDifficultyBootstrap = botDifficultyBootstrap;
        _mainMenu = mainMenu;
    }

    private void Start()
    {
        _mouseSensitivity = PlayerPrefs.GetFloat(MouseSensitivityPrefKey, _cameraRig.MouseSensitivity);
        _keyboardTurnSpeed = PlayerPrefs.GetFloat(KeyboardTurnSpeedPrefKey, _cameraRig.KeyboardTurnSpeed);
        _cameraRig.MouseSensitivity = _mouseSensitivity;
        _cameraRig.KeyboardTurnSpeed = _keyboardTurnSpeed;
    }

    private void Update()
    {
        if (Keyboard.current != null && Keyboard.current.f1Key.wasPressedThisFrame)
            _open = !_open;

        if (Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame)
            TogglePause();
    }

    private void OnDestroy()
    {
        _activeRebind?.Dispose();

        // Safety net: never leave a scene unload/round end with gameplay frozen.
        Time.timeScale = 1f;
    }

    // Fixed id: only one local player (and therefore one SettingsMenu instance) exists per scene,
    // so there's no risk of colliding with another IMGUI window in this project's OnGUI-only HUD.
    private const int WindowId = 847021;

    private void OnGUI()
    {
        if (_paused)
        {
            var pauseRect = new Rect(Screen.width / 2f - 110, Screen.height / 2f - 100, 220, 10);
            GUILayout.Window(PauseWindowId, pauseRect, DrawPauseWindow, "Paused");
        }

        if (_open)
            _windowRect = GUILayout.Window(WindowId, _windowRect, DrawWindow, "Settings (F1)");
    }

    private void TogglePause()
    {
        if (_paused) Resume();
        else Pause();
    }

    private void Pause()
    {
        _paused = true;
        Time.timeScale = 0f;
        _cameraRig.CursorUnlocked = true;
        _cameraRig.SuppressAutoRelock = true;
    }

    private void Resume()
    {
        _paused = false;
        Time.timeScale = 1f;
        _cameraRig.CursorUnlocked = false;
        _cameraRig.SuppressAutoRelock = false;
    }

    private void DrawPauseWindow(int id)
    {
        if (GUILayout.Button("Resume")) Resume();

        GUI.enabled = _roundController != null;
        if (GUILayout.Button("Restart round"))
        {
            _roundController!.StartRound();
            _cameraRig.SnapToTarget();
            Resume();
        }
        GUI.enabled = true;

        if (GUILayout.Button("Settings")) _open = true;

        if (GUILayout.Button("Quit"))
        {
            if (_mainMenu != null)
            {
                // Return to the main menu instead of exiting the process. Only flip _paused here
                // (not the full Resume()) — ShowMenu already drives the identical frozen-timeScale
                // + free-cursor state Resume() would otherwise stomp back to "playing".
                _paused = false;
                _mainMenu.ShowMenu();
            }
            else
            {
                Application.Quit();
#if UNITY_EDITOR
                UnityEditor.EditorApplication.isPlaying = false;
#endif
            }
        }
    }

    private void DrawWindow(int id)
    {
        GUILayout.Label("Keybinds", GUI.skin.box);
        DrawRebindRow("Jump", _input.JumpAction);
        DrawRebindRow("Slide", _input.SlideAction);
        DrawRebindRow("Sprint", _input.SprintAction);
        DrawRebindRow("Interact", _input.InteractAction);

        GUILayout.Space(8);
        GUILayout.Label("Sensitivity", GUI.skin.box);
        DrawSensitivitySliders();

        if (_botDifficultyBootstrap != null)
        {
            GUILayout.Space(8);
            GUILayout.Label("Bots", GUI.skin.box);
            DrawBotDifficultyRow(_botDifficultyBootstrap);
        }

        GUILayout.Space(8);
        if (GUILayout.Button("Close")) _open = false;

        GUI.DragWindow(new Rect(0, 0, 10000, 20));
    }

    private void DrawRebindRow(string label, InputAction action)
    {
        GUILayout.BeginHorizontal();
        GUILayout.Label(label, GUILayout.Width(70));

        string bindingText = _rebindingAction == action ? "press a key..." : action.GetBindingDisplayString(0);
        GUILayout.Label(bindingText, GUILayout.Width(110));

        GUI.enabled = _rebindingAction == null;
        if (GUILayout.Button("Rebind", GUILayout.Width(70)))
            StartRebind(action);
        GUI.enabled = true;

        GUILayout.EndHorizontal();
    }

    /// <summary>Cycles Casual/Skilled/Scary and applies instantly (ParkourBotInput.Configure is instant — no restart needed).</summary>
    private void DrawBotDifficultyRow(TagArenaBootstrap bootstrap)
    {
        GUILayout.BeginHorizontal();
        GUILayout.Label("Difficulty (live)", GUILayout.Width(140));

        BotDifficulty current = bootstrap.Difficulty;
        if (GUILayout.Button("<", GUILayout.Width(30)))
            bootstrap.ApplyDifficulty(CycleDifficulty(current, -1));

        GUILayout.Label(current.ToString(), GUILayout.Width(70));

        if (GUILayout.Button(">", GUILayout.Width(30)))
            bootstrap.ApplyDifficulty(CycleDifficulty(current, 1));

        GUILayout.EndHorizontal();
    }

    private static BotDifficulty CycleDifficulty(BotDifficulty current, int step)
    {
        var values = (BotDifficulty[])System.Enum.GetValues(typeof(BotDifficulty));
        int index = System.Array.IndexOf(values, current);
        int next = (index + step + values.Length) % values.Length;
        return values[next];
    }

    private void DrawSensitivitySliders()
    {
        GUILayout.BeginHorizontal();
        GUILayout.Label($"Mouse: {_mouseSensitivity:0.0}", GUILayout.Width(140));
        float newMouse = GUILayout.HorizontalSlider(_mouseSensitivity, MinMouseSensitivity, MaxMouseSensitivity);
        GUILayout.EndHorizontal();
        if (!Mathf.Approximately(newMouse, _mouseSensitivity))
        {
            _mouseSensitivity = newMouse;
            _cameraRig.MouseSensitivity = newMouse;
            PlayerPrefs.SetFloat(MouseSensitivityPrefKey, newMouse);
        }

        GUILayout.BeginHorizontal();
        GUILayout.Label($"Keyboard turn: {_keyboardTurnSpeed:0}", GUILayout.Width(140));
        float newTurn = GUILayout.HorizontalSlider(_keyboardTurnSpeed, MinKeyboardTurnSpeed, MaxKeyboardTurnSpeed);
        GUILayout.EndHorizontal();
        if (!Mathf.Approximately(newTurn, _keyboardTurnSpeed))
        {
            _keyboardTurnSpeed = newTurn;
            _cameraRig.KeyboardTurnSpeed = newTurn;
            PlayerPrefs.SetFloat(KeyboardTurnSpeedPrefKey, newTurn);
        }
    }

    /// <summary>
    /// Interactively rebinds the action's keyboard binding (index 0 — every rebindable action here
    /// is built keyboard-first with a gamepad binding appended at index 1, see
    /// <see cref="PlayerInputProvider"/>; the settings menu only remaps the keyboard side).
    /// The action is disabled for the duration, matching the pattern used by Unity's own
    /// RebindingUI sample (rebinding an enabled action throws).
    /// </summary>
    private void StartRebind(InputAction action)
    {
        _activeRebind?.Cancel();

        _rebindingAction = action;
        action.Disable();

        _activeRebind = action.PerformInteractiveRebinding(bindingIndex: 0)
            .WithControlsExcluding("<Mouse>/position")
            .WithControlsExcluding("<Mouse>/delta")
            .WithCancelingThrough("<Keyboard>/escape")
            .OnComplete(_ =>
            {
                FinishRebind(action);
                _input.SavePersistedBindingOverrides();
            })
            .OnCancel(_ => FinishRebind(action))
            .Start();
    }

    private void FinishRebind(InputAction action)
    {
        _activeRebind?.Dispose();
        _activeRebind = null;
        _rebindingAction = null;
        action.Enable();
    }
}
