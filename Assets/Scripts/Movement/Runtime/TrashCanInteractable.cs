#nullable enable

using UnityEngine;

namespace Game.Movement;

/// <summary>Per-can state holder for a trash can pickup: tier/value/eat-duration plus round state (active/eaten/progress) and its glow visual.</summary>
public sealed class TrashCanInteractable : MonoBehaviour
{
    private GameObject? _glowVisual;

    public void Initialize(int tier, float eatDuration, int value, GameObject? glowVisual)
    {
        Tier = tier;
        EatDuration = eatDuration;
        Value = value;
        _glowVisual = glowVisual;
    }

    public bool IsActive { get; private set; }
    public bool IsEaten { get; private set; }
    public float Progress { get; set; }
    public int Value { get; private set; }
    public float EatDuration { get; private set; }
    public int Tier { get; private set; }
    public Vector3 Position => transform.position;

    public void Activate()
    {
        IsActive = true;
        IsEaten = false;
        Progress = 0f;
        SetGlow(true);
    }

    public void ResetForRound()
    {
        IsActive = false;
        IsEaten = false;
        Progress = 0f;
        SetGlow(false);
    }

    public void MarkEaten()
    {
        IsActive = false;
        IsEaten = true;
        Progress = 1f;
        SetGlow(false);
    }

    private void SetGlow(bool on)
    {
        if (_glowVisual != null) _glowVisual.SetActive(on);
    }
}
