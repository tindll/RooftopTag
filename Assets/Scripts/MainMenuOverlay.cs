#nullable enable

using Game.AI;
using Game.CameraSystem;
using Game.Rules;
using UnityEngine;

/// <summary>
/// Minimal main-menu overlay: title, difficulty select, chaser-count select, scene switch, PLAY
/// and Quit game. Shown at round start and whenever the Esc-pause menu's Quit button returns here
/// (see <see cref="SettingsMenu"/>). Reuses the exact freeze mechanism the pause menu already
/// uses — <c>Time.timeScale = 0</c> plus <see cref="ThirdPersonCameraRig.CursorUnlocked"/>/
/// <see cref="ThirdPersonCameraRig.SuppressAutoRelock"/> — rather than inventing a second one.
///
/// Deliberately has no namespace and lives outside any custom asmdef (compiles into
/// Assembly-CSharp), same as <see cref="SettingsMenu"/>/<see cref="TagArenaBootstrap"/>, which
/// attach it live at runtime — see <see cref="PlaygroundBootstrap"/>'s remarks (mirrored on
/// PlaygroundBuilder.cs) for why: this environment's headless Unity cannot reliably resolve
/// custom-asmdef script types when deserializing a saved scene.
///
/// Only ever attached by <see cref="TagArenaBootstrap"/>, so it's naturally absent from the
/// headless self-play harness (<c>SelfPlayTests.cs</c> builds bot agents directly in code and
/// never calls into any bootstrap) and from the movement playground (<see cref="PlaygroundBootstrap"/>
/// never attaches it) — both inherently safe with zero extra guarding.
/// </summary>
public sealed class MainMenuOverlay : MonoBehaviour
{
    private static readonly int[] ChaserCounts = { 1, 3, 5, 10 };

    // Fixed id: only one local player (and therefore one MainMenuOverlay instance) exists per
    // scene, so there's no risk of colliding with another IMGUI window in this project's OnGUI-only HUD.
    private const int WindowId = 847024;

    private TagArenaBootstrap _bootstrap = null!;
    private RoundController _roundController = null!;
    private ThirdPersonCameraRig _cameraRig = null!;

    private bool _open;
    private int _chaserIndex;
    private GUIStyle? _titleStyle;

    public void Configure(TagArenaBootstrap bootstrap, RoundController roundController, ThirdPersonCameraRig cameraRig)
    {
        _bootstrap = bootstrap;
        _roundController = roundController;
        _cameraRig = cameraRig;
        _chaserIndex = System.Array.IndexOf(ChaserCounts, bootstrap.TaggerCount);
        if (_chaserIndex < 0) _chaserIndex = 0;
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
        Time.timeScale = 0f;
        _cameraRig.CursorUnlocked = true;
        _cameraRig.SuppressAutoRelock = true;
        _roundController.StartRound();
        _cameraRig.SnapToTarget();
    }

    private void OnGUI()
    {
        if (!_open) return;

        _titleStyle ??= new GUIStyle(GUI.skin.label) { fontSize = 20, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };

        var rect = new Rect(Screen.width / 2f - 130, Screen.height / 2f - 120, 260, 10);
        GUILayout.Window(WindowId, rect, DrawWindow, "Rooftop Tag");
    }

    private void DrawWindow(int id)
    {
        GUILayout.Label("ROOFTOP TAG", _titleStyle);
        GUILayout.Space(8);

        DrawDifficultyRow();
        DrawChaserCountRow();
        DrawSceneRow();

        GUILayout.Space(8);
        if (GUILayout.Button("PLAY")) Play();
        if (GUILayout.Button("Quit game")) QuitGame();

        GUI.DragWindow(new Rect(0, 0, 10000, 20));
    }

    private void DrawDifficultyRow()
    {
        GUILayout.BeginHorizontal();
        GUILayout.Label("Difficulty", GUILayout.Width(90));
        BotDifficulty current = _bootstrap.Difficulty;
        if (GUILayout.Button("<", GUILayout.Width(30))) _bootstrap.ApplyDifficulty(CycleDifficulty(current, -1));
        GUILayout.Label(current.ToString(), GUILayout.Width(70));
        if (GUILayout.Button(">", GUILayout.Width(30))) _bootstrap.ApplyDifficulty(CycleDifficulty(current, 1));
        GUILayout.EndHorizontal();
    }

    private void DrawChaserCountRow()
    {
        GUILayout.BeginHorizontal();
        GUILayout.Label("Chasers", GUILayout.Width(90));
        if (GUILayout.Button("<", GUILayout.Width(30))) _chaserIndex = (_chaserIndex - 1 + ChaserCounts.Length) % ChaserCounts.Length;
        GUILayout.Label(ChaserCounts[_chaserIndex].ToString(), GUILayout.Width(70));
        if (GUILayout.Button(">", GUILayout.Width(30))) _chaserIndex = (_chaserIndex + 1) % ChaserCounts.Length;
        GUILayout.EndHorizontal();
    }

    private void DrawSceneRow()
    {
        GUILayout.BeginHorizontal();
        GUILayout.Label("Scene", GUILayout.Width(90));
        if (GUILayout.Button("Rooftop")) LoadScene("RooftopArena");
        if (GUILayout.Button("Playground")) LoadScene("MovementPlayground");
        GUILayout.EndHorizontal();
    }

    private static void LoadScene(string sceneName) =>
        UnityEngine.SceneManagement.SceneManager.LoadScene(sceneName);

    private void Play()
    {
        _bootstrap.ApplyTaggerCount(ChaserCounts[_chaserIndex]);
        _open = false;
        Time.timeScale = 1f;
        _cameraRig.CursorUnlocked = false;
        _cameraRig.SuppressAutoRelock = false;
        _roundController.StartRound();
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
