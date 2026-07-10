using Game.Movement;
using UnityEngine;

namespace RooftopTag.Tests.PlayMode;

/// <summary>Deterministic <see cref="ICharacterInput"/> double for driving <see cref="CharacterMotor"/> in tests.</summary>
public sealed class ScriptedCharacterInput : MonoBehaviour, ICharacterInput
{
    private bool _pendingJumpPressed;
    private bool _pendingInteractPressed;

    public Vector2 Move { get; set; }
    public Vector2 Look { get; set; }
    public bool JumpHeld { get; set; }
    public bool SlideHeld { get; set; }
    public bool SprintHeld { get; set; } = true;
    public bool JumpPressed { get; private set; }
    public bool InteractPressed { get; private set; }

    public void PressJump() => _pendingJumpPressed = true;
    public void PressInteract() => _pendingInteractPressed = true;

    public void Tick(float deltaTime)
    {
        JumpPressed = _pendingJumpPressed;
        _pendingJumpPressed = false;

        InteractPressed = _pendingInteractPressed;
        _pendingInteractPressed = false;
    }
}
