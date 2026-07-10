#nullable enable

using UnityEngine;

/// <summary>
/// Placeholder for a ladder or swing chain in the scene file. Deliberately has no namespace and
/// lives outside any custom asmdef (compiles into the default Assembly-CSharp), because this
/// environment's headless Unity cannot reliably resolve script types from custom asmdefs when
/// deserializing a saved scene (see <c>PlaygroundBootstrap</c> remarks) — default-assembly,
/// no-namespace scripts are unaffected. <see cref="PlaygroundBootstrap"/> finds these at runtime
/// and attaches the real <c>LadderInteractable</c>/<c>ChainSwingInteractable</c> live.
/// </summary>
public sealed class InteractableMarker : MonoBehaviour
{
    public enum Kind
    {
        Ladder,
        Swing,
    }

    public Kind kind;
    public Transform? pointA;
    public Transform? pointB;
    public float length;
    public Vector3 outwardDirection = Vector3.forward;
}
