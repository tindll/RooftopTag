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
        TrashCan,
    }

    public Kind kind;
    public Transform? pointA;
    public Transform? pointB;
    public float length;
    public Vector3 outwardDirection = Vector3.forward;
    public int tier;

    // Swing only: false when the editor placed a GLB crane model over this swing, so the bootstrap tells
    // the live ChainSwingInteractable to keep its crane COLLIDERS but skip its renderers (the model draws
    // the crane instead). Set by SceneStyler.CreateGlbCranes at build time; read by the bootstraps.
    public bool craneRenderersVisible = true;
}
