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
    private TagRulesConfig _tagConfig = null!;

    /// <summary>Current bot difficulty. Mirrors the serialized field so restarts stay consistent with the last live change.</summary>
    public BotDifficulty Difficulty => difficulty;

    /// <summary>Current chaser (tagger) count — mirrors the shared runtime config so the main menu can read the live value when re-showing.</summary>
    public int TaggerCount => _tagConfig.taggerCount;

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

    /// <summary>
    /// Sets the chaser count on the shared runtime <see cref="TagRulesConfig"/> instance (the same
    /// object reference <see cref="RoundController"/> was Configure'd with). No immediate re-roll
    /// here — RoundController.AssignRoles reads taggerCount fresh on every StartRound call, so the
    /// caller (MainMenuOverlay's Play button) just follows this with StartRound().
    /// </summary>
    public void ApplyTaggerCount(int newTaggerCount) => _tagConfig.taggerCount = newTaggerCount;

    private void Awake()
    {
        var tagConfig = ScriptableObject.CreateInstance<TagRulesConfig>();
        tagConfig.forcePlayerAsRunner = forcePlayerAsRunner;
        _tagConfig = tagConfig;
        var movementConfig = ScriptableObject.CreateInstance<MovementConfig>();
        var botConfig = ScriptableObject.CreateInstance<BotConfig>();
        // Both scenes that use this bootstrap (Tag Arena, Rooftop Arena) build on the same branching
        // RooftopArena topology now — the old linear-corridor graph builder was removed, as it had
        // no caller left through this path.
        ParkourGraph graph = Game.AI.RooftopGraphBuilder.Build(movementConfig);

        var roundControllerGo = new GameObject("RoundController");
        RoundController roundController = roundControllerGo.AddComponent<RoundController>();
        roundController.Configure(tagConfig);
        _roundController = roundController;
        _graph = graph;
        _botConfig = botConfig;

        var animController = Resources.Load<RuntimeAnimatorController>("CharacterAnimator");

        PlayerInputProvider inputProvider = playerRoot.AddComponent<PlayerInputProvider>();
        CharacterMotor playerMotor = playerRoot.AddComponent<CharacterMotor>();
        playerMotor.Configure(groundMask, wallMask, cameraYawPivot);
        TagAgent playerAgent = playerRoot.AddComponent<TagAgent>();
        var (playerRenderer, playerProcedural, playerBridge) = CharacterModelAttacher.Attach(playerRoot, "raccoon", playerMotor, animController);
        playerAgent.Configure(tagConfig, playerMotor, playerRenderer, isLocalPlayer: true, proceduralBody: playerProcedural,
            bridge: playerBridge, animController: animController, modelResourceName: "raccoon");
        playerAgent.SetRoundController(roundController);
        roundController.RegisterAgent(playerAgent, isLocalPlayer: true);

        ThirdPersonCameraRig rig = cameraRig.AddComponent<ThirdPersonCameraRig>();
        rig.Configure(playerMotor, mainCamera, cameraYawPivot, groundMask);
        roundController.SetCameraRig(rig);

        var mainMenu = playerRoot.AddComponent<MainMenuOverlay>();
        mainMenu.Configure(this, roundController, rig);
        playerRoot.AddComponent<SettingsMenu>().Configure(inputProvider, rig, roundController, this, mainMenu);

        var bots = new System.Collections.Generic.List<ParkourBotInput>(botRoots.Length);
        foreach (GameObject botRoot in botRoots)
        {
            ParkourBotInput botInput = botRoot.AddComponent<ParkourBotInput>();
            CharacterMotor botMotor = botRoot.AddComponent<CharacterMotor>();
            botMotor.Configure(groundMask, wallMask, null);
            TagAgent botAgent = botRoot.AddComponent<TagAgent>();
            var (botRenderer, botProcedural, botBridge) = CharacterModelAttacher.Attach(botRoot, "pest_control", botMotor, animController);
            botAgent.Configure(tagConfig, botMotor, botRenderer, isLocalPlayer: false, proceduralBody: botProcedural,
                bridge: botBridge, animController: animController, modelResourceName: "pest_control");
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
            if (marker.kind == InteractableMarker.Kind.TrashCan)
            {
                // Literal duration/value mirror TagRulesConfig.eatDuration* defaults — this
                // bootstrap has no config access yet, and RoundController re-drives progress with
                // its own config at runtime anyway, so these are just the component's static
                // value/duration.
                GameObject? glow = marker.transform.Find("TrashCanGlow")?.gameObject;
                marker.gameObject.AddComponent<TrashCanInteractable>()
                    .Initialize(marker.tier, marker.tier == 2 ? 5f : 2.5f, marker.tier == 2 ? 2 : 1, glow);
            }
            else if (marker.kind == InteractableMarker.Kind.Ladder)
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
