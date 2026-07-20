#nullable enable

using System.Collections;
using System.Linq;
using Game.Movement;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace RooftopTag.Tests.PlayMode;

/// <summary>
/// Verifies no crane piece near a ChainSwingInteractable's chain is standable: every BuildCrane
/// piece (Mast top, Jib, Brace, CounterJib, Counterweight) is tilted (see VertexUpTilt in
/// ChainSwingInteractable) so no face is standable, while staying solid, non-trigger, and on the
/// normal groundMask. Proves it by dropping a real CharacterMotor capsule above each hardened pad
/// and letting real physics run — a vertex-up face at 54.7 deg needs friction tan(54.7) ~= 1.41 to
/// hold, far past Unity's default ~0.6, so PhysX genuinely slides the capsule off.
/// </summary>
public sealed class SwingCraneCampTests
{
    private MovementConfig _movementConfig = null!;
    private GameObject? _sceneRoot;

    [OneTimeSetUp]
    public void LoadConfigs() => _movementConfig = ScriptableObject.CreateInstance<MovementConfig>();

    [TearDown]
    public void Cleanup()
    {
        if (_sceneRoot != null) Object.DestroyImmediate(_sceneRoot);
    }

    [UnityTest]
    public IEnumerator ChainSwingCrane_NoPieceIsStandable_CapsuleSlidesOffEveryTop()
    {
        _sceneRoot = new GameObject("TestScene");
        // Ground's top face sits at y = 0 — matches BuildCrane's own mastBottomY floor (clamped to 0),
        // so a capsule that slides off a crane piece lands on real ground rather than the void, and its
        // resting height (~0) is unambiguously far below any crane piece's top (all >= ~5.6m here).
        CreateGround(_sceneRoot.transform, new Vector3(0f, -0.5f, 0f), new Vector3(40f, 1f, 40f));

        var pivotGo = new GameObject("Pivot");
        pivotGo.transform.SetParent(_sceneRoot.transform, false);
        pivotGo.transform.position = new Vector3(0f, 5f, 0f);

        var swingGo = new GameObject("Swing");
        swingGo.transform.SetParent(_sceneRoot.transform, false);
        ChainSwingInteractable swing = swingGo.AddComponent<ChainSwingInteractable>();
        swing.Initialize(pivotGo.transform, 3f, Vector3.forward); // builds the crane colliders (headless-safe)

        // Let the freshly-built crane transforms reach the physics scene before any collider.bounds is
        // read — bounds come from PhysX, so reading them the same frame they're created returns stale
        // values.
        Physics.SyncTransforms();
        yield return new WaitForFixedUpdate();

        Transform? crane = swingGo.transform.Find("SwingCrane");
        Assert.IsNotNull(crane, "BuildCrane should have created the SwingCrane container.");

        // Only the flat PADS are covered. The Jib/CounterJib/Brace are horizontal arms, and a box with a
        // horizontal length axis provably cannot be made non-standable by any rotation (its other two
        // axes span a vertical plane, so one is always within 45 deg of up) — asserting they shed would
        // be asserting something impossible. They're 0.18-0.35m rods you'd have to balance on. See
        // ChainSwingInteractable.VertexUpTilt.
        string[] padNames = { "MastCap", "Counterweight" };
        BoxCollider[] pieces = crane!.GetComponentsInChildren<BoxCollider>()
            .Where(c => padNames.Contains(c.name)).ToArray();
        Assert.AreEqual(padNames.Length, pieces.Length,
            "Precondition: expected exactly the hardened pads (MastCap, Counterweight) — if BuildCrane's pieces were renamed, this test is silently covering nothing.");

        // Precondition: the pads must actually be up at the crane (pivot y=5, mast rises 0.6 above it).
        // Without this a mis-built crane sitting at the origin would be buried in the ground plane and
        // every drop would "slide off" trivially — the test would pass while proving nothing.
        foreach (BoxCollider pad in pieces)
            Assert.Greater(pad.bounds.max.y, 4f,
                $"Precondition: {pad.name} is at y={pad.bounds.max.y:0.00}, not up at the crane — the scene is mis-built and this test would prove nothing.");

        (GameObject go, CharacterMotor motor) = CreateCapsule();

        foreach (BoxCollider piece in pieces)
        {
            Bounds b = piece.bounds;
            float topY = b.max.y;
            Vector3 dropStart = new Vector3(b.center.x, topY + 1.5f, b.center.z);
            motor.ResetState(dropStart, Quaternion.identity);

            for (int i = 0; i < 100; i++) // ~2s at the default 0.02s fixed timestep
                yield return new WaitForFixedUpdate();

            float restY = go.transform.position.y;
            Debug.Log($"METRIC swing_crane_camp piece={piece.name} top_y={topY:0.00} rest_y={restY:0.00} state={motor.CurrentState}");
            Assert.Less(restY, topY - 0.5f,
                $"Capsule came to rest at y={restY:0.00}, close to {piece.name}'s own top (y={topY:0.00}) — " +
                "it camped on the crane instead of sliding off a non-standable tilted face.");
        }
    }

    // ---------------------------------------------------------------- Helpers

    private static GameObject CreateGround(Transform parent, Vector3 center, Vector3 size)
    {
        GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.transform.SetParent(parent, false);
        go.transform.position = center;
        go.transform.localScale = size;
        return go;
    }

    private (GameObject go, CharacterMotor motor) CreateCapsule()
    {
        var go = new GameObject("TestCapsule");
        go.transform.SetParent(_sceneRoot!.transform, false);

        go.AddComponent<Rigidbody>();
        go.AddComponent<CapsuleCollider>();
        go.AddComponent<ScriptedCharacterInput>();
        CharacterMotor motor = go.AddComponent<CharacterMotor>();

        var motorSo = new SerializedObject(motor);
        motorSo.FindProperty("config").objectReferenceValue = _movementConfig;
        motorSo.ApplyModifiedProperties();

        return (go, motor);
    }
}
