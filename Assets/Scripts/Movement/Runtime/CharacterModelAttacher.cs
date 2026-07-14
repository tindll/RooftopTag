#nullable enable

using UnityEngine;

namespace Game.Movement;

/// <summary>
/// Replaces a placeholder capsule with a rigged, animated character model loaded from Resources,
/// adds the Animator bridge, and returns the renderer to tint plus a flag for skipping the
/// procedural capsule presentation. Falls back to the capsule (procedural = true) if the model or
/// controller assets are missing, so the game still runs.
/// Shared by TagArenaBootstrap (initial spawn, in Assembly-CSharp) and TagAgent (role-swap
/// conversions, in the Game.Rules asmdef) — lives here in Game.Movement, which both can reference,
/// rather than in TagArenaBootstrap itself, since Game.Rules cannot reference back into
/// Assembly-CSharp.
/// </summary>
public static class CharacterModelAttacher
{
    public static (Renderer? renderer, bool procedural, CharacterAnimatorBridge? bridge) Attach(
        GameObject root, string resourceName, CharacterMotor motor, RuntimeAnimatorController? controller)
    {
        var prefab = Resources.Load<GameObject>(resourceName);
        if (prefab == null || controller == null)
        {
            Debug.LogWarning($"Character model '{resourceName}' or CharacterAnimator not found in Resources — using capsule.");
            return (root.GetComponentInChildren<Renderer>(), true, null);
        }

        GameObject model = Object.Instantiate(prefab, root.transform);
        model.name = "CharacterModel";
        model.transform.localPosition = Vector3.zero;
        model.transform.localRotation = Quaternion.identity;

        // Tripo exports ~1 m tall; scale up to a ~1.8 m character (matches the old capsule height).
        var rends = model.GetComponentsInChildren<Renderer>();
        if (rends.Length > 0)
        {
            Bounds mb = rends[0].bounds;
            foreach (Renderer r in rends) mb.Encapsulate(r.bounds);
            if (mb.size.y > 0.01f) model.transform.localScale *= 1.8f / mb.size.y;
        }

        // Hide the placeholder capsule body but keep it in the hierarchy (harmless, and avoids
        // disturbing anything that assumed a "Body" child exists).
        Transform capsule = root.transform.Find("Body");
        if (capsule != null) capsule.gameObject.SetActive(false);

        Animator animator = model.GetComponentInChildren<Animator>();
        animator.runtimeAnimatorController = controller;
        animator.applyRootMotion = false;
        var bridge = root.AddComponent<CharacterAnimatorBridge>();
        bridge.Configure(motor, animator);

        return (model.GetComponentInChildren<SkinnedMeshRenderer>(), false, bridge);
    }
}
