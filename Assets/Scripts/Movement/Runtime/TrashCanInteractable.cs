#nullable enable

using UnityEngine;

namespace Game.Movement;

/// <summary>Per-can state holder for a trash can pickup: tier/value/eat-duration plus round state
/// (active/eaten/progress) and its visuals. The bin body + floating arrow indicator are shown ONLY
/// while this can is an active objective, so a bin appears only where there is a live objective —
/// inactive and eaten cans hide entirely (body, zone, and the solid root collider).</summary>
public sealed class TrashCanInteractable : MonoBehaviour
{
    private GameObject? _body;
    private GameObject? _zone;
    private Collider? _rootCollider;

    public void Initialize(int tier, float eatDuration, int value, GameObject? body, GameObject? zone)
    {
        Tier = tier;
        EatDuration = eatDuration;
        Value = value;
        _body = body;
        _zone = zone;
        // Prefab-path bins carry their solid collider on this root object; primitive fallbacks carry
        // it on the body child (hidden with the body). Grabbing it here lets SetVisible drop the
        // physical obstacle too, so a hidden inactive can is not an invisible wall.
        _rootCollider = GetComponent<Collider>();
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
        SetVisible(true);
    }

    public void ResetForRound()
    {
        IsActive = false;
        IsEaten = false;
        Progress = 0f;
        SetVisible(false);
    }

    public void MarkEaten()
    {
        IsActive = false;
        IsEaten = true;
        Progress = 1f;
        SetVisible(false);
    }

    private void SetVisible(bool on)
    {
        if (_body != null) _body.SetActive(on);
        if (_zone != null) _zone.SetActive(on);
        if (_rootCollider != null) _rootCollider.enabled = on;
    }
}
