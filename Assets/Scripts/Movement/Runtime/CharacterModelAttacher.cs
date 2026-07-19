#nullable enable

using UnityEngine;
using UnityEngine.Rendering;

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
    // Static-quadruped fit (raccoon_quad.glb, no rig): sized by body LENGTH, not height — the
    // biped 1.8m height rule would balloon a long low animal. Length chosen so the raccoon still
    // fits under the netHitRadius trap dome (1.8m across) and reads chunky next to 1.8m taggers.
    private const float QuadrupedBodyLength = 1.6f;
    // Yaw applied to the mesh so its nose points down the rig's +Z (travel direction) — the Tripo
    // glb faces -Z natively (verified in-scene: tail toward the agent's forward at yaw 0).
    private const float QuadrupedFacingYawDeg = 180f;

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

        Animator rigAnimator = model.GetComponentInChildren<Animator>();
        if (rigAnimator == null) return AttachStaticQuadruped(root, model, motor);

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

        Animator animator = rigAnimator;
        animator.runtimeAnimatorController = controller;
        animator.applyRootMotion = false;
        var bridge = root.AddComponent<CharacterAnimatorBridge>();
        bridge.Configure(motor, animator);

        // Ragdoll: real-model path only (the procedural capsule has no bones to build from), and never
        // headless — self-play runs 12 agents and must not pay for ~11 Rigidbodies each for a ragdoll
        // no one will ever see. Built inert; nothing about the agent changes until CharacterRagdoll
        // .Activate. Deliberately NOT returned in the tuple: the tuple's three members all feed
        // TagAgent.Configure, whereas the ragdoll's only consumer wants it at ragdoll-time from
        // whatever it already has (the agent root), so a GetComponent there beats threading a fourth
        // member through two call sites and a Configure signature.
        // RE-USED, not re-added, across model swaps: TagAgent.SwapModel destroys the CharacterModel
        // child (taking the old bones with it) and re-enters here, and Destroy is deferred to end of
        // frame — so destroying + re-adding this component would leave two of them live for a frame
        // and GetComponent could hand out the doomed one. Build() resets its own state instead.
        if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null)
        {
            CharacterRagdoll ragdoll = root.GetComponent<CharacterRagdoll>() ?? root.AddComponent<CharacterRagdoll>();
            ragdoll.Build(animator, motor, bridge);
        }

        return (model.GetComponentInChildren<SkinnedMeshRenderer>(), false, bridge);
    }

    /// <summary>
    /// Unrigged quadruped path (no Animator in the prefab, e.g. raccoon_quad.glb): wraps the mesh
    /// in a "CharacterModel" wrapper (the transform TagAgent's net-trap wiggle and SwapModel own)
    /// with a "QuadBody" child (the transform <see cref="QuadrupedPresenter"/> animates), fits it
    /// by body length, grounds its feet, and swaps the glTFast material for URP/Lit so TagAgent's
    /// role tint/emission path works unchanged. No Animator bridge and no ragdoll — both are
    /// humanoid-rig constructs; returns a null bridge, which every caller already tolerates.
    /// </summary>
    private static (Renderer? renderer, bool procedural, CharacterAnimatorBridge? bridge) AttachStaticQuadruped(
        GameObject root, GameObject model, CharacterMotor motor)
    {
        var wrapper = new GameObject("CharacterModel");
        wrapper.transform.SetParent(root.transform, false);
        // Steal the instantiated prefab's wrapper name for the wrapper; the mesh becomes QuadBody.
        model.name = "QuadBody";
        model.transform.SetParent(wrapper.transform, false);
        model.transform.localRotation = Quaternion.Euler(0f, QuadrupedFacingYawDeg, 0f);

        var rends = model.GetComponentsInChildren<Renderer>();
        if (rends.Length > 0)
        {
            Bounds mb = rends[0].bounds;
            foreach (Renderer r in rends) mb.Encapsulate(r.bounds);
            float length = Mathf.Max(mb.size.x, mb.size.z);
            if (length > 0.01f) model.transform.localScale *= QuadrupedBodyLength / length;

            // Re-read bounds post-scale and drop the feet onto the rig's ground plane (the glb
            // pivot sits at the body centre, not underfoot like the biped FBXs).
            mb = rends[0].bounds;
            foreach (Renderer r in rends) mb.Encapsulate(r.bounds);
            model.transform.localPosition += Vector3.up * (root.transform.position.y - mb.min.y);

            // glTFast's shader ignores the URP/Lit tint+emission properties TagAgent drives;
            // rebuild each material as URP/Lit around the imported base texture (same recipe as
            // the city-building relight pass).
            Shader lit = Shader.Find("Universal Render Pipeline/Lit");
            if (lit != null)
            {
                foreach (Renderer r in rends)
                {
                    Material src = r.sharedMaterial;
                    if (src == null || src.shader == lit) continue;
                    Texture? baseTex = src.mainTexture;
                    if (baseTex == null && src.HasProperty("baseColorTexture")) baseTex = src.GetTexture("baseColorTexture");
                    if (baseTex == null && src.HasProperty("_BaseMap")) baseTex = src.GetTexture("_BaseMap");
                    var replacement = new Material(lit) { mainTexture = baseTex, color = Color.white };
                    replacement.SetFloat("_Smoothness", 0.25f);
                    r.material = replacement;
                }
            }
        }

        Transform capsule = root.transform.Find("Body");
        if (capsule != null) capsule.gameObject.SetActive(false);

        var presenter = wrapper.AddComponent<QuadrupedPresenter>();
        presenter.Configure(motor, model.transform);

        return (model.GetComponentInChildren<Renderer>(), false, null);
    }
}
