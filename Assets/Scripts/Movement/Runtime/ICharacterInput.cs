using UnityEngine;

namespace Game.Movement;

/// <summary>
/// Per-tick input contract driving the character simulation. Local player input, bot AI,
/// and future network input all implement this so the same <see cref="CharacterMotor"/>
/// consumes them identically.
/// </summary>
public interface ICharacterInput
{
    Vector2 Move { get; }
    Vector2 Look { get; }
    bool JumpHeld { get; }
    bool JumpPressed { get; }
    bool SlideHeld { get; }
    bool SprintHeld { get; }
    bool InteractPressed { get; }

    /// <summary>Called once per fixed simulation step to snapshot/consume edge-triggered state.</summary>
    void Tick(float deltaTime);
}
