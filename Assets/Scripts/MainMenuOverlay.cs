#nullable enable

using Game.AI;
using Game.CameraSystem;
using Game.Movement;
using Game.Rules;
using Game.UI;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Main-menu overlay: title, difficulty select, chaser-count select, scene switch, PLAY and Quit
/// game, styled entirely off <see cref="GameUIStyle"/> (100% OnGUI, no UI Toolkit — see that
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
    // Left column, not centered — see PHASE 3 note. CardHeight is a fixed budget generous enough for
    // title + 4 option rows + PLAY/Quit + the 5-row legend; there's slack at the bottom rather than
    // measuring content dynamically (nothing here changes row count at runtime).
    private const float CardX = 90f;
    private const float CardY = 110f;
    private const float CardWidth = 460f;
    private const float CardHeight = 720f;
    private const float CardPad = 28f;
    private const float SlideInDuration = 0.35f;

    private TagArenaBootstrap _bootstrap = null!;
    private RoundController _roundController = null!;
    private ThirdPersonCameraRig _cameraRig = null!;

    // Both components already live on this same GameObject (TagArenaBootstrap builds
    // PlayerInputProvider/TagAgent onto playerRoot before adding this overlay to it) — fetched here
    // rather than threaded through Configure, so the key legend can read the REAL bindings instead
    // of hardcoding key names.
    private PlayerInputProvider? _input;
    private TagAgent? _localAgent;

    private bool _open;
    private int _chaserIndex;
    private bool _unlimitedTime;
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

        GameUIStyle.Panel(new Rect(x, CardY, CardWidth, CardHeight));

        float contentX = x + CardPad;
        float contentWidth = CardWidth - CardPad * 2f;
        float y = CardY + CardPad;

        var titleStyle = GameUIStyle.Label(GameUIStyle.Display, TextAnchor.MiddleLeft, FontStyle.Bold);
        titleStyle.normal.textColor = GameUIStyle.Text;
        GUI.Label(GameUIStyle.Scaled(new Rect(contentX, y, contentWidth, 84f)), "ROOFTOP TAG", titleStyle);
        y += 84f + 4f;

        GUI.DrawTexture(GameUIStyle.Scaled(new Rect(contentX, y, 160f, 5f)), GameUIStyle.GradientTex, ScaleMode.StretchToFill, true);
        y += 5f + 22f;

        y = DrawDifficultyRow(contentX, y, contentWidth);
        y = DrawChaserRow(contentX, y, contentWidth);
        y = DrawTimeRow(contentX, y, contentWidth);
        y = DrawSceneRow(contentX, y, contentWidth);
        y += 12f;

        Color playPrev = GUI.color;
        GUI.color = new Color(GameUIStyle.Accent.r, GameUIStyle.Accent.g, GameUIStyle.Accent.b, t);
        if (GameUIStyle.Button(new Rect(contentX, y, contentWidth, 60f), "PLAY")) Play();
        GUI.color = playPrev;
        y += 60f + 10f;

        if (GameUIStyle.Button(new Rect(contentX, y, 130f, 34f), "Quit game")) QuitGame();
        y += 34f + 26f;

        DrawLegend(contentX, y, contentWidth);

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

    private static float DrawSceneRow(float x, float y, float width)
    {
        const float rowHeight = 46f;
        const float labelWidth = 90f;

        var labelStyle = GameUIStyle.Label(GameUIStyle.Body, TextAnchor.MiddleLeft);
        labelStyle.normal.textColor = GameUIStyle.TextDim;
        GUI.Label(GameUIStyle.Scaled(new Rect(x, y, labelWidth, rowHeight)), "Scene", labelStyle);

        float segWidth = (width - labelWidth - 8f) * 0.5f;
        if (GameUIStyle.Button(new Rect(x + labelWidth, y + 4f, segWidth, rowHeight - 8f), "Rooftop"))
            LoadScene("RooftopArena");
        if (GameUIStyle.Button(new Rect(x + labelWidth + segWidth + 8f, y + 4f, segWidth, rowHeight - 8f), "Playground"))
            LoadScene("MovementPlayground");

        return y + rowHeight + 10f;
    }

    /// <summary>5-row compact legend: closes the "is left-click lunge or tag?" gap by reading the
    /// actual live bindings off TagAgent/PlayerInputProvider rather than hardcoding key names.</summary>
    private void DrawLegend(float x, float y, float width)
    {
        var headerStyle = GameUIStyle.Label(GameUIStyle.Caption, TextAnchor.MiddleLeft, FontStyle.Bold);
        headerStyle.normal.textColor = GameUIStyle.TextDim;
        GUI.Label(GameUIStyle.Scaled(new Rect(x, y, width, 18f)), "CONTROLS", headerStyle);
        y += 18f + 8f;

        var keyStyle = GameUIStyle.Label(GameUIStyle.Caption, TextAnchor.MiddleLeft);
        keyStyle.normal.textColor = GameUIStyle.Text;
        var bindStyle = GameUIStyle.Label(GameUIStyle.Caption, TextAnchor.MiddleRight);
        bindStyle.normal.textColor = GameUIStyle.AccentBright;

        DrawLegendRow(x, y, width, "Lunge", _localAgent?.LungeAction, keyStyle, bindStyle); y += 24f;
        DrawLegendRow(x, y, width, "Tag", _localAgent?.TagAction, keyStyle, bindStyle); y += 24f;
        DrawLegendRow(x, y, width, "Jump", _input?.JumpAction, keyStyle, bindStyle); y += 24f;
        DrawLegendRow(x, y, width, "Slide", _input?.SlideAction, keyStyle, bindStyle); y += 24f;
        DrawLegendRow(x, y, width, "Vault / grab", _input?.InteractAction, keyStyle, bindStyle);
    }

    private static void DrawLegendRow(float x, float y, float width, string label, InputAction? action, GUIStyle keyStyle, GUIStyle bindStyle)
    {
        GUI.Label(GameUIStyle.Scaled(new Rect(x, y, width * 0.5f, 20f)), label, keyStyle);
        string binding = action?.GetBindingDisplayString(0) ?? "-";
        GUI.Label(GameUIStyle.Scaled(new Rect(x + width * 0.5f, y, width * 0.5f, 20f)), binding, bindStyle);
    }

    private static void LoadScene(string sceneName) =>
        UnityEngine.SceneManagement.SceneManager.LoadScene(sceneName);

    private void Play()
    {
        _bootstrap.ApplyTaggerCount(ChaserCounts[_chaserIndex]);
        _bootstrap.ApplyUnlimitedTime(_unlimitedTime);
        _open = false;
        Time.timeScale = 1f;
        _cameraRig.CursorUnlocked = false;
        _cameraRig.SuppressAutoRelock = false;
        _roundController.StartMatch(); // fresh best-of-5 on every PLAY press, not just a standalone round
        _cameraRig.SnapToTarget();
    }

    private static void QuitGame()
    {
        Application.Quit();
#if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;
#endif
    }

    private static BotDifficulty CycleDifficulty(BotDifficulty current, int step)
    {
        var values = (BotDifficulty[])System.Enum.GetValues(typeof(BotDifficulty));
        int index = System.Array.IndexOf(values, current);
        int next = (index + step + values.Length) % values.Length;
        return values[next];
    }
}
