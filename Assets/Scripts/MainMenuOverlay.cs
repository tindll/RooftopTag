#nullable enable

using Game.AI;
using Game.CameraSystem;
using Game.Movement;
using Game.Rules;
using Game.UI;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Main-menu overlay: difficulty select, chaser-count select, PLAY, and a collapsed-by-default
/// CONTROLS dropdown whose rows are live rebind buttons (shared flow: <see cref="KeyRebinder"/>),
/// styled entirely off <see cref="GameUIStyle"/> (100% OnGUI, no UI Toolkit — see that
/// file's remarks). Shown at round start and whenever the Esc-pause menu's Quit button returns
/// here (see <see cref="SettingsMenu"/>). Reuses the exact freeze mechanism the pause menu already
/// uses — <c>Time.timeScale = 0</c> plus <see cref="ThirdPersonCameraRig.CursorUnlocked"/>/
/// <see cref="ThirdPersonCameraRig.SuppressAutoRelock"/> — rather than inventing a second one.
///
/// Deliberately has no namespace and lives outside any custom asmdef (compiles into
/// Assembly-CSharp), same as <see cref="SettingsMenu"/>/<see cref="TagArenaBootstrap"/>, which
/// attach it live at runtime — see <see cref="PlaygroundBootstrap"/>'s remarks (mirrored on
/// PlaygroundBuilder.cs) for why: this environment's headless Unity cannot reliably resolve
/// custom-asmdef script types when deserializing a saved scene. Assembly-CSharp already sees
/// Game.UI/Game.Movement without an explicit asmdef reference — it auto-references every
/// autoReferenced asmdef in the project, same reason SettingsMenu.cs already used InputAction here.
///
/// Only ever attached by <see cref="TagArenaBootstrap"/>, so it's naturally absent from the
/// headless self-play harness (<c>SelfPlayTests.cs</c> builds bot agents directly in code and
/// never calls into any bootstrap) and from the movement playground (<see cref="PlaygroundBootstrap"/>
/// never attaches it) — both inherently safe with zero extra guarding.
/// </summary>
public sealed class MainMenuOverlay : MonoBehaviour
{
    // 0 is a valid choice: with no chasers (and typically Unlimited time) the arena becomes a
    // free-roam space for testing movement/animation with nothing hunting the player.
    private static readonly int[] ChaserCounts = { 0, 1, 3, 5, 10 };

    // Card geometry, design-space (GameUIStyle.Scale takes it to real pixels @1080p/1440p/ultrawide).
    // Left column, not centered — see PHASE 3 note. Height is computed per open state: the CONTROLS
    // dropdown is the one thing that changes row count at runtime, so the card grows by exactly its
    // rows when expanded (fixed budget otherwise, same reasoning as before).
    private const float CardX = 90f;
    private const float CardY = 110f;
    private const float CardWidth = 460f;
    private const float CardBaseHeight = 384f;
    private const float CardPad = 28f;
    private const float SlideInDuration = 0.35f;

    // CONTROLS dropdown rebind rows (design units).
    private const float RebindRowHeight = 34f;
    private const float RebindRowGap = 6f;
    private const int RebindRowCount = 6; // Jump, Slide, Sprint, Interact, Lunge, Tag

    private TagArenaBootstrap _bootstrap = null!;
    private RoundController _roundController = null!;
    private ThirdPersonCameraRig _cameraRig = null!;

    // Both components already live on this same GameObject (TagArenaBootstrap builds
    // PlayerInputProvider/TagAgent onto playerRoot before adding this overlay to it) — fetched here
    // rather than threaded through Configure, so the CONTROLS dropdown reads/rebinds the REAL
    // actions of the current local player instead of hardcoding key names.
    private PlayerInputProvider? _input;
    private TagAgent? _localAgent;

    private bool _open;

    /// <summary>Whether the overlay is currently drawn/frozen-behind. Read by RoundController to
    /// gate HUD banners (round-start / countdown) that would otherwise render on top of this menu.</summary>
    public bool IsOpen => _open;

    private int _chaserIndex;
    private bool _unlimitedTime;
    private bool _controlsExpanded; // CONTROLS dropdown, collapsed by default
    private float _openedAtUnscaled;

    public void Configure(TagArenaBootstrap bootstrap, RoundController roundController, ThirdPersonCameraRig cameraRig)
    {
        _bootstrap = bootstrap;
        _roundController = roundController;
        _cameraRig = cameraRig;
        _input = GetComponent<PlayerInputProvider>();
        _localAgent = GetComponent<TagAgent>();
        _chaserIndex = System.Array.IndexOf(ChaserCounts, bootstrap.TaggerCount);
        if (_chaserIndex < 0) _chaserIndex = 0;
        _unlimitedTime = bootstrap.UnlimitedTime;
    }

    private void Start() => ShowMenu();

    private void OnDestroy()
    {
        KeyRebinder.Cancel();

        // Safety net: never leave a scene unload/teardown with gameplay frozen — same rationale as
        // SettingsMenu.OnDestroy (e.g. PlayMode tests that load then unload this scene mid-suite).
        Time.timeScale = 1f;
    }

    /// <summary>Freezes the round and (re)shows the menu. Called at startup and by SettingsMenu's
    /// pause-menu Quit button. Restarts the round so the arena doesn't sit mid-round behind the menu.</summary>
    public void ShowMenu()
    {
        _open = true;
        _openedAtUnscaled = Time.unscaledTime; // drives the card's UIEase slide/fade-in
        Time.timeScale = 0f;
        _cameraRig.CursorUnlocked = true;
        _cameraRig.SuppressAutoRelock = true;
        _roundController.StartRound();
        _cameraRig.SnapToTarget();
    }

    private void OnGUI()
    {
        if (!_open) return;

        float t = UIEase.Since(_openedAtUnscaled, SlideInDuration);
        DrawBackdrop(t);
        DrawCard(t);
    }

    // Presentation-only dim + vignette behind the card so the live scene reads as an intentional
    // backdrop rather than the game just being frozen mid-frame. ponytail: no camera orbit — this
    // rig's yaw is driven every LateUpdate off live look input (see ThirdPersonCameraRig), and while
    // paused Time.deltaTime is 0 so even its keyboard-turn path goes dead; animating it without
    // fighting that would mean adding an unscaled-time turn path to the rig itself, which is more
    // than the ~20-line budget for a menu-only flourish. Skipped; add if the rig grows an
    // unscaled-time hook for another reason first.
    private static void DrawBackdrop(float t)
    {
        Color prev = GUI.color;
        GUI.color = new Color(GameUIStyle.PanelBg.r, GameUIStyle.PanelBg.g, GameUIStyle.PanelBg.b, 0.35f * t);
        GUI.DrawTexture(new Rect(0f, 0f, Screen.width, Screen.height), GameUIStyle.HairlineTex, ScaleMode.StretchToFill);
        GUI.color = prev;
        GameUIStyle.Vignette(0.8f * t);
    }

    private void DrawCard(float t)
    {
        float x = Mathf.Lerp(-CardWidth - 40f, CardX, t); // slides in from off-screen left
        Color prevColor = GUI.color;
        GUI.color = new Color(1f, 1f, 1f, t); // fades in alongside the slide

        float cardHeight = CardBaseHeight + (_controlsExpanded ? RebindRowCount * (RebindRowHeight + RebindRowGap) : 0f);
        GameUIStyle.Panel(new Rect(x, CardY, CardWidth, cardHeight));

        float contentX = x + CardPad;
        float contentWidth = CardWidth - CardPad * 2f;
        float y = CardY + CardPad;

        GUI.DrawTexture(GameUIStyle.Scaled(new Rect(contentX, y, 160f, 5f)), GameUIStyle.GradientTex, ScaleMode.StretchToFill, true);
        y += 5f + 22f;

        y = DrawDifficultyRow(contentX, y, contentWidth);
        y = DrawChaserRow(contentX, y, contentWidth);
        y = DrawTimeRow(contentX, y, contentWidth);
        y += 12f;

        Color playPrev = GUI.color;
        GUI.color = new Color(GameUIStyle.Accent.r, GameUIStyle.Accent.g, GameUIStyle.Accent.b, t);
        if (GameUIStyle.Button(new Rect(contentX, y, contentWidth, 60f), "PLAY")) Play();
        GUI.color = playPrev;
        y += 60f + 16f;

        DrawControlsDropdown(contentX, y, contentWidth);

        GUI.color = prevColor;
    }

    /// <summary>Segmented "label &lt; value &gt;" row shared by difficulty/chasers/round-time. Returns
    /// the y cursor for the next row.</summary>
    private static float DrawOptionRow(float x, float y, float width, string label, string value, System.Action onPrev, System.Action onNext)
    {
        const float rowHeight = 46f;
        const float arrowSize = 34f;
        float arrowY = y + (rowHeight - arrowSize) * 0.5f;

        var labelStyle = GameUIStyle.Label(GameUIStyle.Body, TextAnchor.MiddleLeft);
        labelStyle.normal.textColor = GameUIStyle.TextDim;
        GUI.Label(GameUIStyle.Scaled(new Rect(x, y, 120f, rowHeight)), label, labelStyle);

        if (GameUIStyle.Button(new Rect(x + 128f, arrowY, arrowSize, arrowSize), "<")) onPrev();

        var valueStyle = GameUIStyle.Label(GameUIStyle.Body, TextAnchor.MiddleCenter, FontStyle.Bold);
        valueStyle.normal.textColor = GameUIStyle.AccentBright;
        float valueX = x + 128f + arrowSize + 8f;
        float valueWidth = width - 128f - arrowSize * 2f - 16f;
        GUI.Label(GameUIStyle.Scaled(new Rect(valueX, y, valueWidth, rowHeight)), value, valueStyle);

        if (GameUIStyle.Button(new Rect(x + width - arrowSize, arrowY, arrowSize, arrowSize), ">")) onNext();

        return y + rowHeight + 10f;
    }

    private float DrawDifficultyRow(float x, float y, float width)
    {
        BotDifficulty current = _bootstrap.Difficulty;
        return DrawOptionRow(x, y, width, "Difficulty", current.ToString(),
            () => _bootstrap.ApplyDifficulty(CycleDifficulty(current, -1)),
            () => _bootstrap.ApplyDifficulty(CycleDifficulty(current, 1)));
    }

    private float DrawChaserRow(float x, float y, float width) =>
        DrawOptionRow(x, y, width, "Chasers", ChaserCounts[_chaserIndex].ToString(),
            () => _chaserIndex = (_chaserIndex - 1 + ChaserCounts.Length) % ChaserCounts.Length,
            () => _chaserIndex = (_chaserIndex + 1) % ChaserCounts.Length);

    private float DrawTimeRow(float x, float y, float width) =>
        // Only two values, so both arrows do the same toggle — keeps the row visually consistent
        // with the others rather than a one-off single button.
        DrawOptionRow(x, y, width, "Round time", _unlimitedTime ? "Unlimited" : "Timed",
            () => _unlimitedTime = !_unlimitedTime,
            () => _unlimitedTime = !_unlimitedTime);

    /// <summary>Collapsed-by-default CONTROLS dropdown: a full-width header row toggles it; when
    /// expanded, every row is a live rebind button — Jump/Slide/Sprint/Interact off
    /// <see cref="PlayerInputProvider"/>, Lunge/Tag off the local <see cref="TagAgent"/> — all
    /// through the same <see cref="KeyRebinder"/> flow the settings menu uses.</summary>
    private void DrawControlsDropdown(float x, float y, float width)
    {
        if (GameUIStyle.Button(new Rect(x, y, width, 44f), _controlsExpanded ? "CONTROLS  ▾" : "CONTROLS  ▸"))
            _controlsExpanded = !_controlsExpanded;
        y += 44f + RebindRowGap;

        if (!_controlsExpanded) return;

        DrawRebindRow(x, ref y, width, "Jump", _input?.JumpAction);
        DrawRebindRow(x, ref y, width, "Slide", _input?.SlideAction);
        DrawRebindRow(x, ref y, width, "Sprint", _input?.SprintAction);
        DrawRebindRow(x, ref y, width, "Vault / grab", _input?.InteractAction);
        DrawRebindRow(x, ref y, width, "Lunge", _localAgent?.LungeAction);
        DrawRebindRow(x, ref y, width, "Tag", _localAgent?.TagAction);
    }

    /// <summary>Label left, binding button right; clicking the binding starts a
    /// <see cref="KeyRebinder"/> listen ("press a key...", Escape cancels).</summary>
    private static void DrawRebindRow(float x, ref float y, float width, string label, InputAction? action)
    {
        var labelStyle = GameUIStyle.Label(GameUIStyle.Body, TextAnchor.MiddleLeft);
        labelStyle.normal.textColor = GameUIStyle.TextDim;
        GUI.Label(GameUIStyle.Scaled(new Rect(x, y, width * 0.45f, RebindRowHeight)), label, labelStyle);

        var buttonRect = new Rect(x + width * 0.45f, y, width * 0.55f, RebindRowHeight);
        bool listening = action != null && KeyRebinder.Listening == action;
        string text = action == null ? "-" : listening ? "press a key..." : action.GetBindingDisplayString(0);

        GUI.enabled = action != null && (listening || KeyRebinder.Listening == null);
        if (GameUIStyle.Button(buttonRect, text) && !listening) KeyRebinder.Start(action!);
        GUI.enabled = true;

        y += RebindRowHeight + RebindRowGap;
    }

    private void Play()
    {
        KeyRebinder.Cancel(); // never leave a rebind listen running behind live gameplay
        _bootstrap.ApplyTaggerCount(ChaserCounts[_chaserIndex]);
        _bootstrap.ApplyUnlimitedTime(_unlimitedTime);
        _open = false;
        Time.timeScale = 1f;
        _cameraRig.CursorUnlocked = false;
        _cameraRig.SuppressAutoRelock = false;
        _roundController.StartMatch(); // fresh best-of-5 on every PLAY press, not just a standalone round
        _cameraRig.SnapToTarget();
    }

    private static BotDifficulty CycleDifficulty(BotDifficulty current, int step)
    {
        var values = (BotDifficulty[])System.Enum.GetValues(typeof(BotDifficulty));
        int index = System.Array.IndexOf(values, current);
        int next = (index + step + values.Length) % values.Length;
        return values[next];
    }
}
