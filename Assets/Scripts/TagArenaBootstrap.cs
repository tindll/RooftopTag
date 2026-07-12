#nullable enable

using Game.AI;
using Game.CameraSystem;
using Game.Movement;
using Game.Rules;
using UnityEngine;

/// <summary>
/// M2 tag-arena equivalent of <see cref="PlaygroundBootstrap"/>: attaches every custom-asmdef
/// component live, at runtime, instead of relying on the scene to have them pre-attached (see
/// <see cref="PlaygroundBootstrap"/>'s remarks for why). Spawns one human-controlled agent and
/// N bot agents, wires them all into a single <see cref="RoundController"/>, and wires up the
/// shared ladder/swing markers exactly as the movement playground does.
/// </summary>
public sealed class TagArenaBootstrap : MonoBehaviour
{
    [SerializeField] private GameObject playerRoot = null!;
    [SerializeField] private GameObject cameraRig = null!;
    [SerializeField] private Camera mainCamera = null!;
    [SerializeField] private Transform cameraYawPivot = null!;
    [SerializeField] private GameObject[] botRoots = null!;
    [SerializeField] private int groundMask = ~0;
    [SerializeField] private int wallMask = ~0;
    [SerializeField] private BotDifficulty difficulty = BotDifficulty.Skilled;
    // Default true matches TagRulesConfig's own default (the "chase me" scenes: player always
    // Runner, hunted by the bot Taggers). The real 12-agent Tag Arena overrides this to false so
    // the player is assigned a role like any other agent, per CLAUDE.md's actual ruleset.
    [SerializeField] private bool forcePlayerAsRunner = true;

    // Kept alive past Awake so ApplyDifficulty can re-Configure every bot at runtime (e.g. from the
    // settings menu's live "Bot difficulty" row) without rebuilding the round.
    private readonly System.Collections.Generic.List<(ParkourBotInput input, TagAgent agent)> _bots = new();
    private RoundController _roundController = null!;
    private ParkourGraph? _graph;
    private BotConfig _botConfig = null!;

    /// <summary>Current bot difficulty. Mirrors the serialized field so restarts stay consistent with the last live change.</summary>
    public BotDifficulty Difficulty => difficulty;

    /// <summary>
    /// Re-configures every spawned bot with a new difficulty immediately (ParkourBotInput.Configure
    /// is instant — no restart needed) and updates the serialized field so a scene restart keeps
    /// using the newly selected difficulty.
    /// </summary>
    public void ApplyDifficulty(BotDifficulty newDifficulty)
    {
        difficulty = newDifficulty;
        foreach ((ParkourBotInput input, TagAgent agent) in _bots)
            input.Configure(agent, _roundController, _graph, _botConfig, newDifficulty);
    }

    private void Awake()
    {
        var tagConfig = ScriptableObject.CreateInstance<TagRulesConfig>();
        tagConfig.forcePlayerAsRunner = forcePlayerAsRunner;
        var movementConfig = ScriptableObject.CreateInstance<MovementConfig>();
        var botConfig = ScriptableObject.CreateInstance<BotConfig>();
        // Both scenes that use this bootstrap (Tag Arena, Rooftop Arena) build on the same branching
        // RooftopArena topology now — the old linear-corridor graph (TagArenaParkourGraphBuilder) has
        // no caller left through this path.
        ParkourGraph graph = Game.AI.RooftopGraphBuilder.Build(movementConfig);

        var roundControllerGo = new GameObject("RoundController");
        RoundController roundController = roundControllerGo.AddComponent<RoundController>();
        roundController.Configure(tagConfig);
        _roundController = roundController;
        _graph = graph;
        _botConfig = botConfig;

        PlayerInputProvider inputProvider = playerRoot.AddComponent<PlayerInputProvider>();
        CharacterMotor playerMotor = playerRoot.AddComponent<CharacterMotor>();
        playerMotor.Configure(groundMask, wallMask, cameraYawPivot);
        TagAgent playerAgent = playerRoot.AddComponent<TagAgent>();
        playerAgent.Configure(tagConfig, playerMotor, playerRoot.GetComponentInChildren<Renderer>(), isLocalPlayer: true);
        playerAgent.SetRoundController(roundController);
        roundController.RegisterAgent(playerAgent, isLocalPlayer: true);

        ThirdPersonCameraRig rig = cameraRig.AddComponent<ThirdPersonCameraRig>();
        rig.Configure(playerMotor, mainCamera, cameraYawPivot, groundMask);
        roundController.SetCameraRig(rig);

        playerRoot.AddComponent<SettingsMenu>().Configure(inputProvider, rig, this);

        var bots = new System.Collections.Generic.List<ParkourBotInput>(botRoots.Length);
        foreach (GameObject botRoot in botRoots)
        {
            ParkourBotInput botInput = botRoot.AddComponent<ParkourBotInput>();
            CharacterMotor botMotor = botRoot.AddComponent<CharacterMotor>();
            botMotor.Configure(groundMask, wallMask, null);
            TagAgent botAgent = botRoot.AddComponent<TagAgent>();
            botAgent.Configure(tagConfig, botMotor, botRoot.GetComponentInChildren<Renderer>(), isLocalPlayer: false);
            botAgent.SetRoundController(roundController);
            botInput.Configure(botAgent, roundController, graph, botConfig, difficulty);
            roundController.RegisterAgent(botAgent, isLocalPlayer: false);
            bots.Add(botInput);
            _bots.Add((botInput, botAgent));
        }

        var debugVisualizerGo = new GameObject("ParkourDebugVisualizer");
        debugVisualizerGo.AddComponent<ParkourDebugVisualizer>().Configure(graph, bots);

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
}
