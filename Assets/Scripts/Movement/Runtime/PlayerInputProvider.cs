#nullable enable

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Game.Movement;

/// <summary>
/// Concrete <see cref="ICharacterInput"/> backed by the new Input System. Actions are built
/// in code (no .inputactions asset) so bindings stay simple and reviewable; rebinding can be
/// layered on later via the standard InputAction rebinding API without touching this contract.
/// </summary>
public sealed class PlayerInputProvider : MonoBehaviour, ICharacterInput
{
    private InputAction? _move;
    private InputAction? _look;
    private InputAction? _jump;
    private InputAction? _slide;
    private InputAction? _sprint;
    private InputAction? _interact;

    private bool _pendingJumpPressed;
    private bool _pendingInteractPressed;

    public Vector2 Move { get; private set; }
    public Vector2 Look { get; private set; }
    public bool JumpHeld { get; private set; }
    public bool JumpPressed { get; private set; }
    public bool SlideHeld { get; private set; }
    public bool SprintHeld { get; private set; }
    public bool InteractPressed { get; private set; }

    // Exposed for the settings menu's rebind UI. These are the same concrete InputActions driving
    // the simulation above — rebinding one of them takes effect immediately for gameplay, no extra
    // wiring needed. Only the button actions are exposed; Move/Look are composite/analog and not
    // remapped key-by-key by the settings menu.
    public InputAction JumpAction => _jump!;
    public InputAction SlideAction => _slide!;
    public InputAction SprintAction => _sprint!;
    public InputAction InteractAction => _interact!;

    // PlayerPrefs persistence for rebound keyboard bindings. Keyed by action name so each action's
    // overrides round-trip independently; missing/empty entries just mean "use the built-in default".
    private const string BindingPrefsPrefix = "RooftopTag.Bind.";

    /// <summary>Saves every rebindable action's current binding overrides to PlayerPrefs. Call after a rebind completes.</summary>
    public void SavePersistedBindingOverrides()
    {
        foreach (InputAction action in RebindableActions)
            PlayerPrefs.SetString(BindingPrefsPrefix + action.name, action.SaveBindingOverridesAsJson());
        PlayerPrefs.Save();
    }

    private IEnumerable<InputAction> RebindableActions
    {
        get
        {
            yield return _jump!;
            yield return _slide!;
            yield return _sprint!;
            yield return _interact!;
        }
    }

    private void LoadPersistedBindingOverrides()
    {
        foreach (InputAction action in RebindableActions)
        {
            string json = PlayerPrefs.GetString(BindingPrefsPrefix + action.name, "");
            if (!string.IsNullOrEmpty(json))
                action.LoadBindingOverridesFromJson(json);
        }
    }

    private void Awake()
    {
        _move = new InputAction("Move", InputActionType.Value);
        _move.AddCompositeBinding("2DVector")
            .With("Up", "<Keyboard>/w")
            .With("Down", "<Keyboard>/s")
            .With("Left", "<Keyboard>/a")
            .With("Right", "<Keyboard>/d");
        _move.AddBinding("<Gamepad>/leftStick");

        _look = new InputAction("Look", InputActionType.Value);
        _look.AddBinding("<Mouse>/delta");
        _look.AddBinding("<Gamepad>/rightStick");

        _jump = new InputAction("Jump", InputActionType.Button, "<Keyboard>/space");
        _jump.AddBinding("<Gamepad>/buttonSouth");

        _slide = new InputAction("Slide", InputActionType.Button, "<Keyboard>/leftCtrl");
        _slide.AddBinding("<Gamepad>/buttonEast");

        _sprint = new InputAction("Sprint", InputActionType.Button, "<Keyboard>/leftShift");
        _sprint.AddBinding("<Gamepad>/leftStickPress");

        _interact = new InputAction("Interact", InputActionType.Button, "<Keyboard>/e");
        _interact.AddBinding("<Gamepad>/buttonWest");

        _jump.performed += _ => _pendingJumpPressed = true;
        _interact.performed += _ => _pendingInteractPressed = true;

        LoadPersistedBindingOverrides();
    }

    private void OnEnable()
    {
        _move?.Enable();
        _look?.Enable();
        _jump?.Enable();
        _slide?.Enable();
        _sprint?.Enable();
        _interact?.Enable();
    }

    private void OnDisable()
    {
        _move?.Disable();
        _look?.Disable();
        _jump?.Disable();
        _slide?.Disable();
        _sprint?.Disable();
        _interact?.Disable();
    }

    private void OnDestroy()
    {
        _move?.Dispose();
        _look?.Dispose();
        _jump?.Dispose();
        _slide?.Dispose();
        _sprint?.Dispose();
        _interact?.Dispose();
    }

    public void Tick(float deltaTime)
    {
        Move = _move?.ReadValue<Vector2>() ?? Vector2.zero;
        Look = _look?.ReadValue<Vector2>() ?? Vector2.zero;
        JumpHeld = _jump?.IsPressed() ?? false;
        SlideHeld = _slide?.IsPressed() ?? false;
        SprintHeld = _sprint?.IsPressed() ?? false;

        JumpPressed = _pendingJumpPressed;
        _pendingJumpPressed = false;

        InteractPressed = _pendingInteractPressed;
        _pendingInteractPressed = false;
    }
}
