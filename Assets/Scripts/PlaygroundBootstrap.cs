#nullable enable

using Game.CameraSystem;
using Game.Movement;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Attaches every custom-asmdef component live, at runtime, instead of relying on the scene
/// file to have them pre-attached via serialization. Deliberately has no namespace and lives
/// outside any custom asmdef (compiles into the default Assembly-CSharp) for the same reason as
/// <see cref="InteractableMarker"/>: this environment's headless Unity cannot reliably resolve
/// custom-asmdef script types when deserializing a saved scene (confirmed via
/// <c>MonoScript.GetClass()</c> returning null for them, and scene-embedded components of those
/// types silently failing to attach — non-deterministically, even with a correct guid reference).
/// AddComponent&lt;T&gt;() at runtime does not go through that broken path (it resolves the type
/// directly from the loaded assembly, not via Unity's serialization bridge), so building the
/// gameplay object graph here rather than baking it into the scene sidesteps the issue entirely.
///
/// Also owns the playground's dev-only "reset" key (R): teleports the player back to spawn,
/// since falling off a ledge otherwise means a slow climb back up or reloading the scene.
/// </summary>
public sealed class PlaygroundBootstrap : MonoBehaviour
{
    [SerializeField] private GameObject playerRoot = null!;
    [SerializeField] private GameObject cameraRig = null!;
    [SerializeField] private Camera mainCamera = null!;
    [SerializeField] private Transform cameraYawPivot = null!;
    [SerializeField] private int groundMask = ~0;
    [SerializeField] private int wallMask = ~0;

    private CharacterMotor _motor = null!;
    private ThirdPersonCameraRig _cameraRig = null!;
    private Vector3 _spawnPosition;
    private Quaternion _spawnRotation;

    private void Awake()
    {
        _spawnPosition = playerRoot.transform.position;
        _spawnRotation = playerRoot.transform.rotation;

        PlayerInputProvider inputProvider = playerRoot.AddComponent<PlayerInputProvider>();
        _motor = playerRoot.AddComponent<CharacterMotor>();
        _motor.Configure(groundMask, wallMask, cameraYawPivot);

        _cameraRig = cameraRig.AddComponent<ThirdPersonCameraRig>();
        _cameraRig.Configure(_motor, mainCamera, cameraYawPivot, groundMask);

        playerRoot.AddComponent<SettingsMenu>().Configure(inputProvider, _cameraRig);

        foreach (InteractableMarker marker in FindObjectsByType<InteractableMarker>(FindObjectsInactive.Exclude))
        {
            if (marker.kind == InteractableMarker.Kind.Ladder)
            {
                LadderInteractable ladder = marker.gameObject.AddComponent<LadderInteractable>();
                ladder.Initialize(marker.pointA!, marker.pointB!, marker.outwardDirection);
            }
            else
            {
                ChainSwingInteractable swing = marker.gameObject.AddComponent<ChainSwingInteractable>();
                // Old playground swing markers leave outwardDirection unset/zero → default to forward
                // so the corridor swing behaves identically; rooftop swing markers carry a real exit dir.
                Vector3 exitDir = marker.outwardDirection.sqrMagnitude > 0.001f ? marker.outwardDirection : Vector3.forward;
                swing.Initialize(marker.pointA!, marker.length, exitDir);
            }

            Destroy(marker);
        }
    }

    private void Update()
    {
        if (Keyboard.current != null && Keyboard.current.rKey.wasPressedThisFrame)
        {
            _motor.ResetState(_spawnPosition, _spawnRotation);
            _cameraRig.SnapToTarget();
        }
    }
}
