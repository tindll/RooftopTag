#nullable enable

using Game.AI;
using Game.CameraSystem;
using Game.Movement;
using Game.Rules;
using Game.UI;
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
    private const string MasterVolumePrefKey = "RooftopTag.Settings.MasterVolume";
    private const string FullscreenPrefKey = "RooftopTag.Settings.Fullscreen";
    private const string ResolutionIndexPrefKey = "RooftopTag.Settings.ResolutionIndex";

    private const float MinMouseSensitivity = 0.5f;
    private const float MaxMouseSensitivity = 10f;
    private const float MinKeyboardTurnSpeed = 30f;
    private const float MaxKeyboardTurnSpeed = 300f;

    // Four common presets, cycled by a button — IMGUI has no real dropdown and this is the whole
    // resolution picker, so a fixed short list beats building one.
    private static readonly Vector2Int[] Resolutions =
    {
        new(1280, 720), new(1600, 900), new(1920, 1080), new(2560, 1440),
    };

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

    /// <summary>Whether either the F1 settings window or the Esc pause menu is currently drawn.
    /// Read by RoundController to gate HUD banners (round-start / countdown) that would otherwise
    /// render on top of them.</summary>
    public bool IsOpen => _open || _paused;

    private bool _paused;
    // Captured on Pause() — the vignette eases in off this, on unscaled time (see UIEase remarks).
    private float _pausedAt;

    // Resolved lazily on first keypress (not per-frame) — null until then. The kill-cam replay is
    // ~4.2s and skippable with click/Space, so blocking pause/settings for its duration is acceptable
    // and intended: it and Time.timeScale=0 (pause/end-screen) are the two owners of Time.timeScale,
    // and they must never overlap or one strands the game holding the other's value.
    private KillCamPlayback? _killCamPlayback;

    private float _mouseSensitivity;
    private float _keyboardTurnSpeed;
    private float _masterVolume;
    private bool _fullscreen;
    private int _resolutionIndex;

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

        // No mixer/global-volume system exists yet — AudioListener.volume IS the master gate every
        // other source (GameAudio's per-category volumes included) plays through.
        _masterVolume = PlayerPrefs.GetFloat(MasterVolumePrefKey, AudioListener.volume);
        AudioListener.volume = _masterVolume;

        _fullscreen = PlayerPrefs.GetInt(FullscreenPrefKey, Screen.fullScreen ? 1 : 0) != 0;
        Screen.fullScreen = _fullscreen;

        _resolutionIndex = Mathf.Clamp(PlayerPrefs.GetInt(ResolutionIndexPrefKey, 2), 0, Resolutions.Length - 1);
    }

    private void Update()
    {
        if (Keyboard.current != null && Keyboard.current.f1Key.wasPressedThisFrame)
        {
            _killCamPlayback ??= FindAnyObjectByType<KillCamPlayback>();
            if (_killCamPlayback is not { IsPlaying: true }) _open = !_open;
        }

        // KeyRebinder.EscapeConsumedThisFrame: an Escape that just cancelled a rebind listen (here
        // or in the main menu's CONTROLS dropdown) must not ALSO toggle the pause menu.
        if (Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame && !KeyRebinder.EscapeConsumedThisFrame)
        {
            _killCamPlayback ??= FindAnyObjectByType<KillCamPlayback>();
            if (_killCamPlayback is not { IsPlaying: true }) TogglePause();
        }
    }

    private void OnDestroy()
    {
        KeyRebinder.Cancel();

        // Safety net: never leave a scene unload/round end with gameplay frozen.
        Time.timeScale = 1f;
    }

    private void OnGUI()
    {
        // Vignette only when actually frozen (Pause()) — F1's settings window can be opened live,
        // mid-round, to rebind on the fly, and the scene behind it should keep reading as playing.
        if (_paused)
            GameUIStyle.Vignette(0.85f * UIEase.Since(_pausedAt, 0.25f));

        if (_open)
            DrawSettingsCard();
        else if (_paused)
            DrawPauseCard();
    }

    private void TogglePause()
    {
        if (_paused) Resume();
        else Pause();
    }

    private void Pause()
    {
        _paused = true;
        _pausedAt = Time.unscaledTime;
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

    private const float RowHeight = 40f;
    private const float RowGap = 10f;

    private void DrawPauseCard()
    {
        const float width = 360f;
        const float pad = 28f;
        const float titleH = 54f;
        const float buttonH = 52f;
        const float gap = 12f;
        const int buttonCount = 4;
        float height = pad * 2f + titleH + buttonCount * buttonH + (buttonCount - 1) * gap;

        float x = (GameUIStyle.DesignWidth - width) / 2f;
        float top = (GameUIStyle.DesignHeight - height) / 2f;
        GameUIStyle.Panel(new Rect(x, top, width, height));

        GUIStyle titleStyle = GameUIStyle.Label(GameUIStyle.Title, TextAnchor.MiddleCenter, FontStyle.Bold);
        titleStyle.normal.textColor = GameUIStyle.Text;
        GUI.Label(GameUIStyle.Scaled(new Rect(x, top + pad - 6f, width, titleH)), "PAUSED", titleStyle);

        float innerX = x + pad;
        float innerWidth = width - pad * 2f;
        float y = top + pad + titleH;

        if (GameUIStyle.Button(new Rect(innerX, y, innerWidth, buttonH), "Resume")) Resume();
        y += buttonH + gap;

        GUI.enabled = _roundController != null;
        if (GameUIStyle.Button(new Rect(innerX, y, innerWidth, buttonH), "Restart round"))
        {
            _roundController!.StartRound();
            _cameraRig.SnapToTarget();
            Resume();
        }
        GUI.enabled = true;
        y += buttonH + gap;

        if (GameUIStyle.Button(new Rect(innerX, y, innerWidth, buttonH), "Settings")) _open = true;
        y += buttonH + gap;

        if (GameUIStyle.Button(new Rect(innerX, y, innerWidth, buttonH), "Quit"))
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

    private void DrawSettingsCard()
    {
        const float width = 560f;
        const float pad = 28f;
        bool showBots = _botDifficultyBootstrap != null;
        // Fixed content budget (rows are a known, non-user-variable set) rather than measuring twice —
        // the background panel must be drawn before the rows that sit on top of it.
        float height = 760f + (showBots ? 96f : 0f);

        float x = (GameUIStyle.DesignWidth - width) / 2f;
        float top = Mathf.Max(30f, (GameUIStyle.DesignHeight - height) / 2f);
        GameUIStyle.Panel(new Rect(x, top, width, height));

        float innerX = x + pad;
        float innerWidth = width - pad * 2f;
        float y = top + pad;

        GUIStyle titleStyle = GameUIStyle.Label(GameUIStyle.Title, TextAnchor.MiddleLeft, FontStyle.Bold);
        titleStyle.normal.textColor = GameUIStyle.Text;
        GUI.Label(GameUIStyle.Scaled(new Rect(innerX, y, innerWidth, 44f)), "SETTINGS", titleStyle);
        y += 54f;

        SectionHeader(innerX, ref y, innerWidth, "CONTROLS");
        DrawRebindRow(innerX, ref y, innerWidth, "Jump", _input.JumpAction);
        DrawRebindRow(innerX, ref y, innerWidth, "Slide", _input.SlideAction);
        DrawRebindRow(innerX, ref y, innerWidth, "Sprint", _input.SprintAction);
        DrawRebindRow(innerX, ref y, innerWidth, "Interact", _input.InteractAction);
        y += 10f;

        SectionHeader(innerX, ref y, innerWidth, "CAMERA");
        DrawSensitivityRows(innerX, ref y, innerWidth);
        y += 10f;

        SectionHeader(innerX, ref y, innerWidth, "AUDIO");
        DrawMasterVolumeRow(innerX, ref y, innerWidth);
        y += 10f;

        SectionHeader(innerX, ref y, innerWidth, "DISPLAY");
        DrawDisplayRows(innerX, ref y, innerWidth);

        if (showBots)
        {
            y += 10f;
            SectionHeader(innerX, ref y, innerWidth, "BOTS");
            DrawBotDifficultyRow(innerX, ref y, innerWidth, _botDifficultyBootstrap!);
        }

        y += 8f;
        if (GameUIStyle.Button(new Rect(innerX, y, innerWidth, 48f), "Close")) _open = false;
    }

    /// <summary>Caption-size dim label + a hairline underneath. Advances <paramref name="y"/> past both.</summary>
    private static void SectionHeader(float x, ref float y, float width, string title)
    {
        GUIStyle style = GameUIStyle.Label(GameUIStyle.Caption, TextAnchor.MiddleLeft, FontStyle.Bold);
        style.normal.textColor = GameUIStyle.TextDim;
        GUI.Label(GameUIStyle.Scaled(new Rect(x, y, width, 20f)), title, style);
        y += 22f;

        Color prev = GUI.color;
        GUI.color = GameUIStyle.Hairline;
        GUI.DrawTexture(GameUIStyle.Scaled(new Rect(x, y, width, 1f)), GameUIStyle.HairlineTex);
        GUI.color = prev;
        y += 12f;
    }

    /// <summary>Two-column row: label on the left, caller-drawn control (design-space rect) on the
    /// right. Advances <paramref name="y"/> by one row + gap.</summary>
    private static void Row(float x, ref float y, float width, string label, System.Action<Rect> drawControl)
    {
        GUIStyle labelStyle = GameUIStyle.Label(GameUIStyle.Body);
        labelStyle.normal.textColor = GameUIStyle.Text;
        GUI.Label(GameUIStyle.Scaled(new Rect(x, y, width * 0.42f, RowHeight)), label, labelStyle);

        drawControl(new Rect(x + width * 0.42f, y, width * 0.58f, RowHeight));
        y += RowHeight + RowGap;
    }

    private void DrawRebindRow(float x, ref float y, float width, string label, InputAction action)
    {
        Row(x, ref y, width, label, controlRect =>
        {
            const float buttonW = 110f;
            var textRect = new Rect(controlRect.x, controlRect.y, controlRect.width - buttonW - 8f, controlRect.height);
            var buttonRect = new Rect(controlRect.xMax - buttonW, controlRect.y, buttonW, controlRect.height);

            GUIStyle bindStyle = GameUIStyle.Label(GameUIStyle.Body, TextAnchor.MiddleLeft, FontStyle.Bold);
            bool listening = KeyRebinder.Listening == action;
            if (listening)
            {
                // Pulse toward AccentBright on unscaled time — pause/settings freeze Time.timeScale,
                // so anything easing off Time.time would sit dead still while this is on screen.
                float pulse = 0.5f + 0.5f * Mathf.Sin(Time.unscaledTime * 6f);
                bindStyle.normal.textColor = Color.Lerp(GameUIStyle.TextDim, GameUIStyle.AccentBright, pulse);
                GUI.Label(GameUIStyle.Scaled(textRect), "press a key...", bindStyle);
            }
            else
            {
                bindStyle.normal.textColor = GameUIStyle.Accent;
                GUI.Label(GameUIStyle.Scaled(textRect), action.GetBindingDisplayString(0), bindStyle);
            }

            GUI.enabled = KeyRebinder.Listening == null;
            if (GameUIStyle.Button(buttonRect, "Rebind")) KeyRebinder.Start(action);
            GUI.enabled = true;
        });
    }

    private void DrawSensitivityRows(float x, ref float y, float width)
    {
        Row(x, ref y, width, $"Mouse sensitivity: {_mouseSensitivity:0.0}", controlRect =>
        {
            float v = GameUIStyle.Slider(controlRect, _mouseSensitivity, MinMouseSensitivity, MaxMouseSensitivity);
            if (Mathf.Approximately(v, _mouseSensitivity)) return;
            _mouseSensitivity = v;
            _cameraRig.MouseSensitivity = v;
            PlayerPrefs.SetFloat(MouseSensitivityPrefKey, v);
        });

        Row(x, ref y, width, $"Keyboard turn speed: {_keyboardTurnSpeed:0}", controlRect =>
        {
            float v = GameUIStyle.Slider(controlRect, _keyboardTurnSpeed, MinKeyboardTurnSpeed, MaxKeyboardTurnSpeed);
            if (Mathf.Approximately(v, _keyboardTurnSpeed)) return;
            _keyboardTurnSpeed = v;
            _cameraRig.KeyboardTurnSpeed = v;
            PlayerPrefs.SetFloat(KeyboardTurnSpeedPrefKey, v);
        });
    }

    private void DrawMasterVolumeRow(float x, ref float y, float width)
    {
        Row(x, ref y, width, $"Master volume: {Mathf.RoundToInt(_masterVolume * 100f)}%", controlRect =>
        {
            float v = GameUIStyle.Slider(controlRect, _masterVolume, 0f, 1f);
            if (Mathf.Approximately(v, _masterVolume)) return;
            _masterVolume = v;
            AudioListener.volume = v;
            PlayerPrefs.SetFloat(MasterVolumePrefKey, v);
        });
    }

    private void DrawDisplayRows(float x, ref float y, float width)
    {
        Row(x, ref y, width, "Fullscreen", controlRect =>
        {
            if (!GameUIStyle.Button(controlRect, _fullscreen ? "On" : "Off")) return;
            _fullscreen = !_fullscreen;
            Screen.fullScreen = _fullscreen;
            PlayerPrefs.SetInt(FullscreenPrefKey, _fullscreen ? 1 : 0);
        });

        Row(x, ref y, width, "Resolution", controlRect =>
        {
            Vector2Int res = Resolutions[_resolutionIndex];
            if (!GameUIStyle.Button(controlRect, $"{res.x} x {res.y}")) return;
            _resolutionIndex = (_resolutionIndex + 1) % Resolutions.Length;
            Vector2Int next = Resolutions[_resolutionIndex];
            Screen.SetResolution(next.x, next.y, _fullscreen);
            PlayerPrefs.SetInt(ResolutionIndexPrefKey, _resolutionIndex);
        });
    }

    /// <summary>Cycles Casual/Skilled/Scary and applies instantly (ParkourBotInput.Configure is instant — no restart needed).</summary>
    private void DrawBotDifficultyRow(float x, ref float y, float width, TagArenaBootstrap bootstrap)
    {
        BotDifficulty current = bootstrap.Difficulty;
        Row(x, ref y, width, $"Bot difficulty: {current}", controlRect =>
        {
            const float buttonW = 44f;
            var prevRect = new Rect(controlRect.x, controlRect.y, buttonW, controlRect.height);
            var nextRect = new Rect(controlRect.xMax - buttonW, controlRect.y, buttonW, controlRect.height);
            if (GameUIStyle.Button(prevRect, "<")) bootstrap.ApplyDifficulty(CycleDifficulty(current, -1));
            if (GameUIStyle.Button(nextRect, ">")) bootstrap.ApplyDifficulty(CycleDifficulty(current, 1));
        });
    }

    private static BotDifficulty CycleDifficulty(BotDifficulty current, int step)
    {
        var values = (BotDifficulty[])System.Enum.GetValues(typeof(BotDifficulty));
        int index = System.Array.IndexOf(values, current);
        int next = (index + step + values.Length) % values.Length;
        return values[next];
    }

}
