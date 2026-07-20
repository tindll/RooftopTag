#nullable enable

using UnityEngine;

namespace Game.MapGeometry;

/// <summary>
/// Single source of truth for the city's night visual theme. Presentation-only — must never
/// influence simulation. Instantiated via CreateInstance at build time (the defaults ARE the
/// theme); scenes never persist a reference to a config asset (see PlaygroundBuilder's remarks
/// on the deserialization bug). Organising rule: the MOON is the only cool key light, the CITY
/// supplies every warm accent (windows, billboards, horizon dome). Field names under "Sun"
/// still say sun for shader/script compatibility (SceneStyler.ApplyEnvironment, GradientSkybox).
/// </summary>
[CreateAssetMenu(fileName = "VisualThemeConfig", menuName = "RooftopTag/Visual Theme Config")]
public sealed class VisualThemeConfig : ScriptableObject
{
    [Header("Sky")]
    /// <summary>Indigo-blue, the darkest value in the whole theme (the top of the night sky). It has
    /// to bottom out this hard for the rest of the pass to work — every window, billboard and rim trim
    /// below is sold by CONTRAST against this, not by its own brightness. Must stay darker than
    /// <see cref="skyMid"/> and <see cref="skyHorizon"/> (band monotonicity), and the
    /// skyHorizon == skyGround == fogColor seam rule must hold — see <see cref="skyHorizon"/>.</summary>
    public Color skyZenith = new Color32(0x0C, 0x12, 0x2A, 0xFF);
    /// <summary>Dark blue sitting between <see cref="skyZenith"/> and <see cref="skyHorizon"/>.
    /// Must stay strictly between them in value so the sky darkens monotonically from horizon to
    /// zenith with no band inversion.</summary>
    public Color skyMid = new Color32(0x1B, 0x24, 0x45, 0xFF);
    /// <summary>The sky AT the horizon, and this is why it must equal <see cref="fogColor"/>: Unity's
    /// fog does NOT apply to the skybox, so wherever fully-fogged geometry meets the sky it meets an
    /// UNFOGGED colour, and any mismatch reads as a seam along the horizon. Matching them is the
    /// physical truth (distant haze IS what you see at the horizon) and it is what makes the ground
    /// slab's far edge — 99% fogged, so already pure fogColor — dissolve into the sky instead of ending
    /// at a visible line. A city light dome genuinely is "the haze, seen edge-on", so horizon == fog is
    /// a literal physical claim, not just a convenient match. The punch at the horizon still comes
    /// from the moon disc the skybox shader ADDS on top, and skyMid still drives the gradient above.
    /// Keep these two and fogColor equal — retuning one alone re-opens the seam.</summary>
    public Color skyHorizon = new Color32(0x2C, 0x37, 0x60, 0xFF);
    /// <summary>Below-horizon sky. Must also equal <see cref="fogColor"/>, same reason as
    /// <see cref="skyHorizon"/>. Costs nothing to keep matched: the ground slab spans past the skyline,
    /// so in RooftopArena this is only visible in the ~4-degree sliver between the slab's far edge and
    /// the horizon. (In MovementPlayground, which builds no slab, it reads as a flat haze floor.)</summary>
    public Color skyGround = new Color32(0x2C, 0x37, 0x60, 0xFF);
    /// <summary>Where <see cref="skyMid"/> lands as a fraction of the way up the sky. Kept low so the
    /// horizon->mid transition compresses down toward the horizon — the shape of a real light dome: a
    /// city's glow is a tight band hugging the skyline that falls off fast, not a gradient washing a
    /// third of the way to the zenith. Raising it bleeds the dome high enough to read as unfinished
    /// dusk rather than night.</summary>
    [Range(0.05f, 0.9f)] public float skyMidPoint = 0.25f;

    [Header("Sun — this is the MOON now (see the class remarks on why the names stayed)")]
    /// <summary>The single most load-bearing number in the theme: direct light on an UP-facing surface
    /// scales with sin(elevation), on a wall with cos(elevation). At 34deg, sin(34)=0.56 vs cos(34)=0.83
    /// — roofs (where you play, and where the ledge trims live) get proportionally far more direct light
    /// than walls do, which is what lets <see cref="sunIntensity"/> sit low enough for the window grids
    /// to have something dark to pop against without starving the roofs.
    ///
    /// Not pushed higher despite the street: lighting the canyon FLOOR would need the shadow throw
    /// (22m drop / tan(elevation)) to fall under a ~9m street, i.e. elevation > 68deg — a moon that steep
    /// is out of frame for a third-person camera and would flatten every surface into a top-down wash.
    /// The street is carried by <see cref="ambientSky"/> and the road tones instead (see constraint notes
    /// there). What 34deg does give the ground is a 33m throw, so the open ground BEYOND the play cluster
    /// catches real moonlight while the cluster's own canyons stay in shadow — bright city floor out
    /// there, dark canyon where you are, for free.</summary>
    public float sunElevationDegrees = 34f;
    public float sunAzimuthDegrees = -35f;
    /// <summary>Cool blue-white. Drives BOTH the directional light's colour and the skybox's _SunColor
    /// (the disc itself), so this one knob is the moon as a light and the moon as an object. This is the
    /// ONLY cool key in the scene (the organising rule — see class remarks), so every warm thing left is
    /// therefore unambiguously a city light.</summary>
    public Color sunColor = new Color32(0xB9, 0xC9, 0xEC, 0xFF);
    /// <summary>It is a moon, so a low key light. Read together with <see cref="sunElevationDegrees"/> —
    /// neither number means anything alone; the low intensity is what the elevated angle is there to
    /// compensate for on roofs.
    ///
    /// The knock-on worth knowing before touching anything emissive: this low a key light multiplies the
    /// apparent strength of every emissive in the theme by a large factor against a near-black ambient.
    /// That is why the window, billboard and rim/bloom intensities elsewhere in this file are tuned low
    /// — they only have to read against this key, not shout over a bright sky.</summary>
    public float sunIntensity = 0.55f;
    /// <summary>Shader pow() exponent for the moon disc: higher = smaller, sharper disc. A moon is a
    /// small hard-edged object, unlike a sun's big soft glow, so this is kept high. 1400 gives ~1.8deg —
    /// still ~3.6x the real moon's 0.5deg, deliberately: a truly correct disc lands on ~2 pixels and
    /// aliases into a sparkle.</summary>
    public float sunDiscSize = 1400f;

    [Header("Ambient (trilight)")]
    /// <summary>NOTE FOR ANYONE RETUNING THE STREET: Unity's Trilight ambient is sampled by a surface's
    /// NORMAL, so ambientSky lights every UP-FACING surface — which means THIS is the street's light, not
    /// <see cref="ambientGround"/> (that one only lights undersides). The road at y=-25 faces up, gets no
    /// direct moon at all (see sunElevationDegrees on why 34deg cannot reach a canyon floor), and is
    /// therefore lit by this value and this value alone.
    ///
    /// Kept moderate rather than crushed toward black: it is doing double duty as the roofs' fill AND as
    /// the sole thing making the street-death sequence watchable. Night is sold here by cool HUE and by
    /// the black sky and popping windows around it — not by driving this toward zero, which would take
    /// the street with it. Do not lower this without raising sidewalkColor to match.</summary>
    public Color ambientSky = new Color32(0x40, 0x4E, 0x82, 0xFF);
    /// <summary>Lights VERTICAL surfaces (normals at the horizon), i.e. the facades. Kept low and cool
    /// (tinted toward fogColor's violet), faintly lifting the walls off black as the city's own dome
    /// rather than a strong bounce that would keep every wall glowing — a facade that stays this dark
    /// is what leaves the window grids something dark to pop against.</summary>
    public Color ambientEquator = new Color32(0x33, 0x3C, 0x62, 0xFF);
    /// <summary>DOWN-facing surfaces only — ledge undersides, overhangs, the cosmetic masses' soffits,
    /// cloud bellies. Despite the name this is NOT the street's light (see <see cref="ambientSky"/>); it
    /// is the light the street throws back UP. Kept faintly warm rather than going cool with everything
    /// else, because that is what it physically is: sodium bounce off tarmac. It buys the clouds'
    /// undersides a free warm underlight against the cool moonlit tops.</summary>
    public Color ambientGround = new Color32(0x2A, 0x24, 0x30, 0xFF);

    [Header("Fog & street haze")]
    /// <summary>NOT neutral black, and that is the entire point: a real city at night sits under a
    /// LIGHT DOME, and a dark blue-grey with a faint warm lift reads as that dome where pure black reads
    /// as a missing skybox. Mixed as a warm sodium glow into a deep night blue — cool overall, but lifted
    /// just off the blue axis so it reads as air with a city under it.
    ///
    /// This is the highest-leverage colour in the file: it is skyHorizon AND skyGround (see their
    /// remarks — all three must move together or the horizon seam re-opens), it tints all three haze
    /// planes, and skylineHazeBlend drives the entire backdrop skyline 75% toward it. It is why the far
    /// city does not simply vanish at night: the buildings out there dissolve INTO a visible dome rather
    /// than into black.</summary>
    public Color fogColor = new Color32(0x2C, 0x37, 0x60, 0xFF); // == skyHorizon/skyGround (seam rule)
    /// <summary>Density for EXPONENTIAL-SQUARED fog (SceneStyler.ApplyEnvironment sets the mode; the
    /// two numbers are not interchangeable — read that comment before retuning this).
    ///
    /// Squared fog is flat near and steep far, which is the shape this needs: it has to satisfy two
    /// demands at once that plain exponential fog cannot — hide the world's edge 460m out, AND leave
    /// the ~34m of air between a roof and the street clear enough to read concrete as CONCRETE (plain
    /// exponential is near-linear up close, so buying visibility at the edge costs a heavy wash nearby).
    /// At this density: ~4% fog at 34m, ~98.4% at the 340m skyline, ~99.95% at the 460m ground edge —
    /// good at both ends. Raising this past ~0.007 starts tinting the play area again; below ~0.005 the
    /// ground's far edge climbs back out of the fog and the world-edge returns. Also keep it low enough
    /// that the Kenney building wall closing off the horizon (~160-190m out) doesn't drown into solid
    /// fog before it reads as silhouettes.</summary>
    public float fogDensity = 0.006f;
    public int hazePlaneCount = 3;
    /// <summary>Y of the highest haze plane — must sit strictly BELOW the lowest walkable roof surface.
    /// The construction zone's lowest floor (Con_Yard) is y=1.5, so this must stay under that with
    /// margin: a haze plane coplanar with a roof's top face z-fights as visible shadow-like flicker
    /// whenever the camera moves.</summary>
    public float hazeTopY = 1.0f;
    /// <summary>Spacing between the stacked haze planes, together with <see cref="hazeTopY"/> and
    /// <see cref="hazePlaneCount"/>. At 8 the planes span from y=1 down through the roof-to-street gap
    /// (street at y=-25), so moving the camera actually separates them into layers rather than reading
    /// as one flat lid. Kept large enough to span that gap; roof-to-roof readability doesn't depend on
    /// this since you always stand above all the planes.</summary>
    public float hazeSpacing = 8.0f;
    /// <summary>Base alpha for the stacked haze planes (they multiply: alpha a, 1.5a, 2a). Kept low
    /// because <see cref="fogDensity"/> (expsq) already handles distance fade properly — these planes
    /// exist only to give the canyon parallax as the camera moves, not to carry depth haze themselves.
    /// At 0.03 the stack is felt, not seen. Push it up only if the street reads too crisp from a
    /// rooftop; it is the wrong knob for "the horizon doesn't fade" (that's fogDensity).</summary>
    public float hazeBaseAlpha = 0.03f;
    public float hazePlaneSize = 400f;

    [Header("Concrete palette")]
    /// <summary>Muted, near-neutral greys (a hair cool). Desaturated on purpose: these values never
    /// encode what colour the light is, so the same wall reads warm under a warm light and cool under a
    /// cool one for free — hue/saturation must stay untouched for that to keep working, only value
    /// should ever move here. Read order (wall &lt; floor) must be preserved if either is retuned.
    ///
    /// Ramp surfaces are wood, not concrete — see the "Ramp planks" header (rampWoodColorLight/Dark)
    /// rather than this pair.</summary>
    public Color concreteWall = new Color32(0x6E, 0x6E, 0x71, 0xFF);
    public Color concreteFloor = new Color32(0x83, 0x83, 0x85, 0xFF);
    /// <summary>Per-building brightness variation (seeded, deterministic) so facades don't read as
    /// clones. Kept high enough that neighbouring buildings — which share both a window grid and a tint
    /// family — still read as visually separate structures.</summary>
    [Range(0f, 0.15f)] public float wallValueJitter = 0.08f;

    [Header("Ramp planks (RooftopArena.BuildRamp's wood deck)")]
    /// <summary>Boards laid across the ramp's 3m width — at 4, ~0.75m per board, a believable plank
    /// width. Also the generated wood atlas's band count (see TagArenaMapGeometry.BuildRampPlankAtlas),
    /// so every physical board maps to its own distinct atlas band 1:1.</summary>
    public int rampPlankCount = 4;
    /// <summary>Metres of dark gap between adjacent boards. Narrow on purpose — a real deck groove is a
    /// shadow line, not a visible span; too wide and the boards read as separate floating slats rather
    /// than one deck.</summary>
    public float rampGrooveWidth = 0.04f;
    /// <summary>How far a board's visible side wall drops below its (flush, y=+0.5) top before the
    /// groove floor is guaranteed to appear — i.e. how deep the groove reads. Independent of
    /// <see cref="rampBaseSlabThickness"/> in the code (raising this only makes the shadowed board edge
    /// taller; it can never push a board's TOP off the walkable surface — that top is hard-coded at the
    /// box's own +0.5, see BuildPlankRampMesh's remarks), but the two are tuned to SUM to the ramp's
    /// 0.5m thickness (0.06 + 0.44) so the board wall meets the base slab's top with no gap between
    /// them; pushing the sum below 0.5 leaves a harmless hollow (BuildPlankRampMesh's remarks explain
    /// why it can never become a visible hole), above 0.5 the base slab and board walls overlap.</summary>
    public float rampPlankRaiseHeight = 0.06f;
    /// <summary>Solid slab filling the ramp box from its bottom up to this height — this is what
    /// guarantees the grooves show dark decking rather than a hole to the sky (constraint: "no
    /// see-through holes"). See <see cref="rampPlankRaiseHeight"/> for how the two defaults were
    /// chosen together.</summary>
    public float rampBaseSlabThickness = 0.44f;
    /// <summary>Lighter board tone. Deliberately warm (unlike the cool concrete palette above) — see
    /// the "Ramp planks" gameplay note: ramps are traversal, and warm-against-cool-moonlit-concrete is
    /// what lets a ramp read at a sprint the way the cool rim trims read a ledge.</summary>
    public Color rampWoodColorLight = new Color32(0xA8, 0x7A, 0x4E, 0xFF);
    /// <summary>Darker/weathered board tone — also the atlas's pinned "base" swatch (band 0, see
    /// BuildRampPlankAtlas) that the ramp's hidden faces (underside, ends, groove floor) sample, so
    /// "dark base" in the groove is this colour and not an arbitrary random band.</summary>
    public Color rampWoodColorDark = new Color32(0x5C, 0x40, 0x28, 0xFF);
    /// <summary>[0,1] blend strength of the grain-streak darkening layered over a board's base tone —
    /// 0 is a flat painted plank, 1 crushes the streaks toward black. Kept moderate so the wood stays
    /// legible as colour, not just noise.</summary>
    [Range(0f, 1f)] public float rampGrainStrength = 0.35f;
    /// <summary>Metres of ramp LENGTH one texture tile's V axis covers. The 20 ramps range from short
    /// hops to ~20m runs, and the grain UV is generated as (world length / this value) repeats — see
    /// BuildPlankRampMesh's remarks — so the grain's apparent size on the ground stays this many metres
    /// per repeat on every ramp regardless of its own length, instead of one fixed 0..1 UV stretching
    /// the same texture thinner on the long ramps and fatter on the short ones.</summary>
    public float rampGrainTileLength = 1.2f;
    /// <summary>Square atlas resolution (px). 256 at rampPlankCount=4 bands is 64px per board — enough
    /// for a grain streak to read at the aniso-8 grazing angles a ramp is always seen at, without the
    /// 1024px+ cost the window atlas needs (that one tiles far more buildings, this tiles one deck).</summary>
    public int rampTexturePixels = 256;
    /// <summary>Fixed, like every other seed in the visual pass, so a rebuilt scene is byte-comparable.</summary>
    public int rampWoodSeed = 4417;

    [Header("Building windows")]
    /// <summary>World metres per window cell — this drives both the window COUNT and (with the fractions
    /// below) the window SIZE, so shrinking it does both at once. The builder ROUNDS each face to a whole
    /// number of cells (see TagArenaMapGeometry.CreateBuildingBox), so this is the target spacing, not
    /// the exact one — which is what keeps a window from being clipped in half at a corner or roof lip.
    /// At 1.0, an 8m face gets 8 columns.</summary>
    public float windowSpacingX = 1.0f;
    /// <summary>Vertical cell size — read this as the building's floor-to-floor height. At 1.5, the
    /// 21m Tower column gets ~14 rows.</summary>
    public float windowSpacingY = 1.5f;
    /// <summary>Window size as a fraction of its cell; the remainder is the wall border around it.
    /// Both are kept well under 1 so every cell keeps a border (a window that filled its cell would
    /// merge with its neighbours into continuous glass bands). Net window is ~0.5m wide x ~0.93m tall:
    /// taller than wide, which is what reads as a city window rather than a porthole.</summary>
    [Range(0.1f, 0.9f)] public float windowWidthFraction = 0.5f;
    [Range(0.1f, 0.9f)] public float windowHeightFraction = 0.62f;
    /// <summary>Share of windows with the interior light on. Kept well under a majority: a mostly-dark
    /// facade with scattered lit rooms reads as a lived-in city at night, where a high value reads as
    /// an office block on fire — and at night that failure mode is worse, because a fully-lit facade
    /// becomes the only bright object in frame. 0.45 reads as "the city is awake" while still leaving
    /// most of every facade dark for the lit rooms to punch out of.</summary>
    [Range(0f, 1f)] public float windowLitChance = 0.45f;
    /// <summary>Warm interior glow — the only thing on a facade that emits. An interior light's colour
    /// is independent of time of day, so this stays fixed regardless of the sky. It is the scene's
    /// principal warm accent (see the class remarks' organising rule: cool ambient, warm points of
    /// interest).</summary>
    public Color windowLitColor = new Color32(0xFF, 0xCC, 0x7A, 0xFF);
    /// <summary>Unlit glass: a dark cool multiplier over the wall tint (the albedo atlas MULTIPLIES
    /// concreteWall), so windows stay windows on every per-building tint. An unlit room at night has
    /// nothing behind the glass, so this is kept near-black — a FRACTION of the wall (~8%) rather than 0,
    /// which lands dark windows near-black against the concrete regardless of per-building tint while
    /// still catching a hint of the moon instead of reading as a hole punched in the building.</summary>
    public Color windowDarkColor = new Color32(0x14, 0x16, 0x1C, 0xFF);
    /// <summary>Kept moderate rather than pushed bright: against a low-key night ambient (see
    /// sunIntensity) and with bloom in play, a high value here clips to a white blob, loses the warm hue
    /// that is the entire point, and bleeds into neighbouring windows until a facade is one glowing
    /// smear. 1.5 keeps each lit room a distinct warm rectangle that still crosses bloomThreshold (1.0)
    /// and blooms deliberately rather than helplessly. This is also the bottom rung of the emissive
    /// ladder that carries the scene's hierarchy: windows 1.5 &lt; billboards 2.0 &lt; interactables
    /// 2.6.</summary>
    public float windowEmissiveIntensity = 1.5f;
    /// <summary>The generated atlas is this many cells square, and — because facade UVs are laid out in
    /// cell units and wrapped — this is also the pattern's repeat period in windows. Too small and the
    /// repeat becomes legible across a wide facade: at windowSpacingX=1 an 8m face spends 8 cells, so
    /// this must stay well above that to avoid an obvious repeat every couple of faces.</summary>
    public int windowAtlasCells = 32;
    /// <summary>Texels per cell. atlas size = windowAtlasCells * windowCellPixels, square. Kept high
    /// enough that a cell stretched over a 1-2m face doesn't visibly blur up close. Note the atlas is 4x
    /// the memory per doubling — see MakeAtlas.</summary>
    public int windowCellPixels = 32;
    /// <summary>Seeds the lit/dark pattern. Fixed, like every other seed in the visual pass, so a
    /// rebuilt scene is byte-comparable.</summary>
    public int windowSeed = 8151;

    [Header("GLB building windows (Tripo unlit-glass key, PHASE 2 of the GLB integration plan)")]
    /// <summary>Pixel VALUE (max of R/G/B, 0-1) below which a baked baseColor texel is a GLASS
    /// candidate. The four Tripo GLBs were deliberately prompted with a warm/earthy palette (olive,
    /// brown, khaki, tan) and uniformly dark, cool, unlit glass — see GlbCityKit.BuildLitMaterial's
    /// remarks — so value alone would also catch dark WALL texels (a shadowed brown is dark too),
    /// which is why this is paired with the blue&gt;=red hue check rather than used alone. 0.35 sits
    /// above the glass's near-black paint and below the palette's darkest warm walls, verified against
    /// the actual baked textures by GlbWindowDebug's dumped masks — retune here first if a model's
    /// mask misses windows or over-catches wall.</summary>
    [Range(0f, 1f)] public float glbWindowGlassValueThreshold = 0.35f;
    /// <summary>Minimum accepted glass connected-component size, as a FRACTION of the texture's own
    /// pixel count rather than a fixed pixel count — the four GLBs are not guaranteed to share one
    /// texture resolution, and a fraction is the only version of this knob that survives that.
    /// Anything smaller is classifier noise (a stray dark texel, a shadow speck), not a window, and is
    /// dropped before it can become a glowing crumb.</summary>
    public float glbWindowMinComponentAreaFraction = 0.00008f;
    /// <summary>Minimum area / (bounding-box area) a glass component must have to count as a window.
    /// Guards the risk called out in GlbCityKit.BuildLitMaterial's remarks: the painterly texture's
    /// dark SHADOW CREVICES pass the value/hue key exactly as well as a real window, but a crevice is
    /// a thin, irregular scribble that fills only a sliver of its own bounding box, where a real window
    /// is close to a filled rectangle. 0.5 rejects anything shaped more like a crack than a pane.</summary>
    [Range(0f, 1f)] public float glbWindowMinRectangularity = 0.5f;
    /// <summary>Max bounding-box aspect ratio (long side / short side) a glass component may have —
    /// the second half of the crevice guard above: a one-texel-wide crack can still pass the
    /// rectangularity test (a 1x40 sliver is 100% of its own bbox) but is never a window's own
    /// proportions. 5 comfortably covers a tall city window (net aspect ~0.93:0.5 ≈ 1.86, see
    /// windowWidthFraction/windowHeightFraction) with slack for the GLB texture's own painterly
    /// irregularity.</summary>
    public float glbWindowMaxAspectRatio = 5f;
    /// <summary>How many DISTINCT lit-window patterns exist per GLB model — the draw-call AND texture-
    /// memory knob, exactly analogous to <see cref="skylineHazeBandCount"/> quantising the skyline's
    /// continuous distance into a fixed number of materials. Callers pass a free-running instance index
    /// as the seed and GlbCityKit buckets it into this many variants, so ~130 instances can never mint
    /// ~130 materials (or ~130 4096-square emission textures, which is the far more expensive half).
    ///
    /// 6 is chosen against the clone-tell this whole phase exists to prevent: with 4 models x 6 patterns
    /// there are 24 distinct facades in the city, so the odds of two ADJACENT buildings sharing both
    /// model and pattern are low enough that the repeat is not legible — while the memory stays bounded
    /// and predictable. Raising it buys variety at one emission texture each; see BuildLitMaterial's
    /// remarks for what one costs.</summary>
    public int glbWindowSeedVariants = 6;

    [Header("GLB building shells (Tripo models skinned over the playable roofs, PHASE 3)")]
    /// <summary>Master gate for the painterly GLB shells over RooftopArena's 31 playable roofs
    /// (SceneStyler.CreateGlbShells). It exists as an A/B switch for the one question a screenshot
    /// cannot settle — whether the painterly walls read as fast as the flat windowed facades did at a
    /// sprint — and it is a HONEST switch, which is why it is worth a field: the shells replace the
    /// roof bodies' and masses' RENDERERS only, so every collider, and every rim trim outlining every
    /// ledge, is identical either way. Flipping this off restores the flat boxes with the simulation
    /// byte-for-byte unchanged; the two builds differ in nothing but what you see.</summary>
    public bool glbShellEnabled = true;

    /// <summary>When true, the playable cluster is styled as an UNDER-CONSTRUCTION site: concrete-shell
    /// facades restyled onto the existing collider-exact roof bodies/masses (no shell models, no
    /// renderer stripping), plus ConstructionShells toppers and ConstructionDressing
    /// props/cranes/worklights. The Tripo GLB shells are skipped for the playable towers in this mode —
    /// the backdrop city keeps its finished Kenney look (deliberate contrast: a construction site inside
    /// a living city). Movement geometry is untouched either way.</summary>
    public bool constructionZone = true;

    /// <summary>When true, playable towers are stacked from the modular building GLBs (Assets/buildings
    /// — bottom/middle/top per type, see ModularBuildings). Takes precedence over the generated
    /// construction facades when constructionZone is also true; flip off to fall back to the
    /// ConstructionShells generated look. Colliders are identical either way.</summary>
    public bool modularBuildings = true;
    /// <summary>How far above a model's own <c>DeckY</c> a triangle must sit before the shell drops it.
    /// In the models' MODEL-LOCAL normalized space (height ~= 1.0), NOT metres — the culled mesh is
    /// cached once per model and shared by every roof that picks it, so it cannot depend on any one
    /// instance's scale. At the arena's 26.5-34m columns one unit is the whole building, so 0.02 is
    /// ~0.6m of real clutter.
    ///
    /// This is what stops players walking through the water towers and stair huts Tripo baked into the
    /// same fused mesh as the building (see GlbCityKit's class doc): the shells carry no colliders, so
    /// anything left standing above the deck is walked straight through. It is NOT free to raise, and
    /// the floor is building4: its deck is not a crisp plane — its top three surface clusters span
    /// 0.3898..0.4078, i.e. 0.018 around a DeckY of 0.4006 — so an epsilon under ~0.008 starts culling
    /// building4's own ROOF and punching holes in the surface players stand on. 0.02 clears that spread
    /// with margin while still dropping every real clutter stack in the set (the shortest is building2's
    /// stair hut at 0.113 above its deck; building3's is 0.176). Expect ~0% dropped on building1 — it
    /// has no rooftop clutter geometry at all, its clutter is painted into the texture.</summary>
    public float glbShellCullEpsilon = 0.02f;
    /// <summary>How far each shell's concrete tint may deviate from pure white (SceneStyler.ShellTint),
    /// as a fraction subtracted from 1.0 — i.e. every tinted RGB component lands in [1 - jitter, 1.0].
    /// Kept subtle and one-sided (never brighter than white, only darker/greyer) because the tint
    /// MULTIPLIES the model's own painted texture (see GlbCityKit.BuildLitMaterial): pushing a component
    /// above 1 cannot brighten past what the texture already bakes in, it just clips. 0.10 is deliberately
    /// restrained — the goal is "two identical models read as two different buildings", not a visibly
    /// colour-graded skyline; see <see cref="wallValueJitter"/> for the same restraint on the procedural
    /// concrete palette this mirrors.</summary>
    [Range(0f, 0.25f)] public float glbTintJitter = 0.10f;
    /// <summary>Night-palette multiplier applied over the whole ShellTint result. The bucketed jitter above
    /// stays near WHITE by design (it varies buildings against each other); this is what pulls every GLB
    /// shell down into the dark slate/blue city so none of them read as bright cream towers — without it,
    /// these shells (not the Kenney placements) are the buildings that read as too light. Windows still
    /// glow — emission is independent of the base multiply.</summary>
    public Color glbShellNightTint = new(0.34f, 0.36f, 0.46f);
    /// <summary>How many DISTINCT tint buckets a shell's seed is hashed into — the same draw-call/material
    /// discipline as <see cref="glbWindowSeedVariants"/> and <see cref="skylineHazeBandCount"/>, and for
    /// the identical reason: GlbCityKit.BuildLitMaterial mints one material PER (model, tint) pair, there
    /// is a hard ceiling of 96 GLB materials in the project, and the skyline already spends ~44 of them.
    /// A per-instance tint (31 roofs x continuous colour) would mint up to 31 more materials for one model
    /// alone; bucketing into a handful of shared tints keeps the count bounded regardless of roof count.
    /// 6 mirrors glbWindowSeedVariants's own bucket count, so a shell's tint and its window pattern quantise
    /// at the same granularity.</summary>
    [Range(1, 12)] public int glbTintVariants = 6;

    [Header("GLB swing crane (crane_swing.glb over each RooftopArena swing link, PHASE 5)")]
    /// <summary>Uniform scale of the crane_swing.glb model SceneStyler.CreateGlbCranes places at each
    /// swing pivot. UNIFORM only — it's a machine, not a building, so no per-axis aspect stretch. The
    /// model ships normalized (height ~1.0), so this is roughly the crane's height in metres. Gated on
    /// <see cref="glbShellEnabled"/> with the shells (flat-box A/B build keeps the procedural crane).
    ///
    /// It also loosely couples to ChainSwingInteractable's procedural collider layout: the model is placed
    /// hook-tip-on-pivot, its jib running out along +side, so at 6 the model's counterweight lands ~4.4m
    /// out (0.729 span * 6), which is where that file's CounterOvershoot puts the anti-camp Counterweight
    /// pad collider (mast at jib reach 3m, pad 1.4m beyond it). Change this and the pad no longer sits
    /// under the model's counterweight — bump CounterOvershoot to match. The colliders are invisible and
    /// exist for camp-prevention + physics parity; the model may visually overhang the jib collider (pure
    /// dressing, no colliders of its own).</summary>
    public float craneModelScale = 6f;
    /// <summary>Final cm-level nudge applied to the GLB crane's world position AFTER the hook-meets-pivot
    /// solve, for hand-aligning the hook tip to the chain anchor if the measured HookLocal needs a tweak
    /// in-scene. Zero by default — the solve lands the hook on the pivot analytically.</summary>
    public Vector3 craneHookNudge = Vector3.zero;

    [Header("GLB climb pipes (modular_pipe.glb tiled over each ClimbPipeVisual wall pipe, PHASE 7)")]
    /// <summary>Uniform fudge on modular_pipe.glb's segment diameter, multiplied over the procedural
    /// pipe's own measured diameter (2 * BuildClimbPipeVisual's radius) before each segment is scaled —
    /// in case the GLB's silhouette needs to read slightly fatter/thinner than the exact (collider-less)
    /// primitive it replaces to still look grabbable. 1 = exact match, no fudge.</summary>
    public float glbPipeDiameterScale = 1f;
    /// <summary>Extra distance (m) each segment is pushed along the wall's outward normal, on top of the
    /// procedural pipe's own centreline (which BuildClimbPipeVisual already offsets proud of the wall by
    /// radius+0.04 — see its faceOffset). Zero by default; a hand-tune lever if the GLB's own mesh pivot
    /// doesn't sit flush with its visible surface the way the primitive cylinder's did.</summary>
    public float glbPipeOutwardNudge = 0f;
    /// <summary>Extra yaw (degrees), applied on top of the wall-facing solve that points the model's
    /// local +Z at the wall. 180 because modular_pipe.glb models its bracket clamp on local -Z (the
    /// opposite of crane_swing.glb's +Z-is-front convention CreateGlbPipes assumes): at 0 the wall-mount
    /// tabs visibly faced the street instead of the facade.</summary>
    public float glbPipeYawOffsetDegrees = 180f;

    [Header("Rim trims (moonlit roof edges — FUNCTIONAL, see remarks)")]
    /// <summary>A pale cool white-blue: moonlight catching the parapet. This trim is NOT decoration — it
    /// outlines every ledge so players can read where a roof ends at running speed — so the hue is chosen
    /// for contrast, not for mood.
    ///
    /// Cool, specifically, because warm would break it. The city's lights are the dominant warm thing in
    /// frame (windows and billboards both warm amber), and a warm-amber line threaded along every ledge
    /// would be one more amber object among hundreds — legible as glow, useless as an EDGE. Cool puts the
    /// trim in the only hue family nothing else in the scene occupies, so a ledge is the one pale-blue
    /// line on a warm-speckled dark facade. It also reads as the moon's light, the only source that could
    /// plausibly be up there.</summary>
    public Color rimColor = new Color32(0xBF, 0xD4, 0xF5, 0xFF);
    /// <summary>Kept low relative to the emissive ladder elsewhere in the file, and that is a deliberate
    /// choice, not an oversight: bloom in this scene is strong, and a bright rim value flares into a
    /// glaring white outline that dominates every shot. 1.0 sits right at bloomThreshold, which still
    /// gives a slight bloom — enough to make a ledge catch your eye peripherally, exactly when you need
    /// it — without the flare a higher value would cause.</summary>
    public float rimEmissiveIntensity = 1.0f;
    public float rimThickness = 0.15f;
    public float rimHeight = 0.12f;

    [Header("Interactables (safety orange)")]
    /// <summary>Safety orange. This is reserved gameplay colour language for player-usable things —
    /// re-hueing it to suit an art pass would trade a learned signal for a mood. It stays legible
    /// against the night palette on saturation alone: it is the only fully-saturated orange in the
    /// scene, where the city's lights are all pale warm amber, and it sits at the top of the emissive
    /// ladder besides (see interactableEmissiveIntensity).</summary>
    public Color interactableColor = new Color32(0xF0, 0x70, 0x20, 0xFF);
    /// <summary>With no bright ambient key light, the emissive ladder is the only visual hierarchy left,
    /// so interactables must sit unmistakably at its top: windows 1.5 &lt; billboards 2.0 &lt;
    /// interactables 2.6. If interactables ever dropped below the set dressing's intensity they would
    /// get buried in a dark scene where nothing else establishes gameplay-vs-dressing hierarchy.</summary>
    public float interactableEmissiveIntensity = 2.6f;

    [Header("Silhouettes (cranes, far skyline)")]
    /// <summary>The far city's base tone, before <see cref="skylineHazeBlend"/> pushes each band toward
    /// <see cref="fogColor"/>. Must sit WELL below fogColor's value, not near it — that gap is what keeps
    /// atmospheric perspective alive: if this sat at the fog's value the near and far bands would both
    /// resolve to fog and the skyline would flatten into one silhouette-free smear. At (30,28,40) the near
    /// band reads as genuinely dark buildings and the far band dissolves up into the brighter dome —
    /// distance makes things LIGHTER at night (haze glows, it doesn't shadow), the opposite of a daytime
    /// scene. Also reused for cranes and the fire-escape props, which read as dark shapes after dark
    /// anyway.</summary>
    public Color silhouetteColor = new Color32(0x1E, 0x1C, 0x28, 0xFF);
    /// <summary>How many DISTINCT haze tints the backdrop skyline is allowed — the draw-call knob, not a
    /// layout one. Buildings' distance-from-centre t is continuous; quantising t into this many bands is
    /// what keeps TagArenaMapGeometry.GetFacadeMaterial's (tint, intensity) cache yielding exactly this
    /// many shared materials instead of one per building. Raising it buys smoother atmospheric
    /// perspective at one extra draw call each.</summary>
    public int skylineHazeBandCount = 4;
    /// <summary>Outer edge of the backdrop ground slab / keep-out sizing (see BackdropBounds): the slab's
    /// square extent is sized from this plus groundEdgeMargin so its edge dies in fog well past anything
    /// placed near it. Per-band colour is pushed toward <see cref="fogColor"/> by
    /// <see cref="skylineHazeBlend"/> across it, for atmospheric perspective. Kept small enough that
    /// buildings near the outer edge (>95% fogged at this radius) stay effectively invisible — pushing it
    /// further out costs their draw calls for no visible gain and dilutes the near->far fill gradient
    /// into a band you can't actually see.</summary>
    public float skylineOuterRadius = 240f;
    [Range(0f, 1f)] public float skylineHazeBlend = 0.75f;
    /// <summary>Skyline blocks carry the SAME window grid as the playable buildings — without it the
    /// windowed play area would meet an unwindowed horizon. Kept dimmer than the buildings' own
    /// <see cref="windowEmissiveIntensity"/> since these sit behind fog and should not out-glow the ones
    /// you can stand on, but the ratio between them must stay high (currently ~87% of the near city's
    /// glow): the far skyline has essentially no albedo left to read at night (silhouetteColor is dark
    /// and heavily fogged), so its windows ARE the far city — dropping the ratio much further would hand
    /// the horizon a dark band the eye reads as nothing at all.</summary>
    public float silhouetteWindowEmissiveIntensity = 1.3f;
    /// <summary>How much of a band's window glow the haze eats at the OUTERMOST band (scaled by the band's
    /// distance t, so the nearest band is unfaded). Deliberately below 1: distant windows must still
    /// read, so the far band keeps some of its glow (currently 50%) rather than going black — same
    /// reasoning as <see cref="silhouetteWindowEmissiveIntensity"/>: at night there is no lit facade left
    /// behind the fog, so windows are all the far city has to read by.</summary>
    [Range(0f, 1f)] public float silhouetteWindowHazeFade = 0.5f;

    [Header("Building masses (cosmetic downward extension of each playable roof)")]
    /// <summary>Y where RooftopArena's roof bodies stop (BuildingSkirt = 3 -> every body bottoms out
    /// at -3, regardless of roof height). The cosmetic mass continues each building's exact footprint
    /// straight down from here to <see cref="buildingBaseY"/> so rooftops read as the TOP of a real
    /// building rather than a floating slab. Coupled to RooftopArena.BuildingSkirt: if that changes,
    /// update this so the seam stays flush.</summary>
    public float buildingBodyBottomY = -3f;
    /// <summary>Street level: the ground slab, the road strips, the cars and every building mass's
    /// bottom all derive from this one knob. Must stay more negative than RoundController.FallResetY
    /// (-15): falling off a roof needs to CROSS that threshold on the way down, or an agent lands on the
    /// street having never tripped the fall check, stranding bots there forever and never losing the
    /// round for the player. Fix any future ground-level change here, not at the threshold — FallResetY
    /// is what SelfPlayTests' fall metric is calibrated against, and moving it changes when bots respawn
    /// headless.</summary>
    public float buildingBaseY = -25f;

    [Header("Facade props (mid-height wall & roof dressing — item 3 of the city-grounding pass)")]
    /// <summary>Props per building (not per unit area): a facade this small (8-20m) reading as 2-3
    /// discrete objects keeps the low-poly/backdrop language cars and clouds already use — a
    /// per-area density would scatter far more props over the biggest roofs (Con_Alley's 8x20) than
    /// the brief's budget can afford. The .4 fraction gives ~40% of qualifying buildings a 3rd prop
    /// (see SceneStyler.PropsForBuilding's seeded coin-flip) instead of every building landing on the
    /// exact same count.</summary>
    public float propDensity = 2.4f;
    /// <summary>Distance from the map centre beyond which a building gets NO props at all — the
    /// budget-honesty knob: at exponential fog density 0.010, fog is already ~78% at 150m and 96.7%
    /// at the skyline's 340m outer ring (nothing there is worth a vertex). 130m sits under that 78%
    /// mark (~73% fogged, still legible) while comfortably covering the ENTIRE playable cluster (max
    /// radius from centre ~66m, RooftopArena.Roofs' own footprints) plus the innermost skyline ring
    /// (72m) with margin — and excludes ring 1 onward (~161m+, >=80% fog), which is most of the ~500
    /// prop-eligible buildings the naive count would otherwise hit.</summary>
    public float propMaxRadius = 130f;
    /// <summary>XZ safety buffer added to every RooftopArena.Roofs footprint for the keep-out check
    /// (SceneStyler.VerifyPropKeepOut) — must clear the largest prop protrusion off a wall
    /// (propFireEscapeSlatDepth, 0.18) with real margin, so a flush-mounted prop is unambiguously
    /// judged "inside the cluster" by the safety check rather than sneaking past it on a technicality.</summary>
    public float propKeepOutMargin = 1.0f;
    /// <summary>Vertical clearance a wall prop must keep on BOTH sides of the mass's own [buildingBaseY,
    /// buildingBodyBottomY] column: prop tops stay this far below buildingBodyBottomY (-3, the roof
    /// lip immediately above) and prop bases this far above buildingBaseY (-25, the street). Reused
    /// for both ends rather than adding a second knob — the mass is 22m tall, so one small margin
    /// keeps props off both seams without needing to mean two different things.</summary>
    public float propWallTopMargin = 0.5f;

    /// <summary>6-8 sided prism per the brief ("plenty" for a squat tank at rooftop-silhouette
    /// distance); 7 avoids the perfectly-hexagonal/square silhouette reading as too regular up close,
    /// at zero draw-call cost (only adds triangles to an already-merged mesh).</summary>
    public int propWaterTowerSides = 7;
    /// <summary>Sized against the ~2m door implied by the building's own window rows (windowSpacingY):
    /// big enough to read as a tank from the street, small enough not to loom over an 8-13m facade.</summary>
    public float propWaterTowerRadius = 0.9f;
    public float propWaterTowerHeight = 1.3f;
    public float propWaterTowerLegHeight = 0.6f;
    public float propWaterTowerLegThickness = 0.12f;

    /// <summary>One box, not two — the brief allows "one or two"; a second box would only add
    /// vertices to a merged mesh nobody stands close enough to tell apart from a single unit at
    /// rooftop-silhouette distance.</summary>
    public Vector3 propACSize = new(1.1f, 0.8f, 0.8f);

    /// <summary>Roughly a real ad panel's proportions (16:9-ish), sized to read as a distinct lit
    /// rectangle against an 8-13m facade without dwarfing it.</summary>
    public float propBillboardWidth = 3.2f;
    public float propBillboardHeight = 1.8f;
    /// <summary>Off the wall just far enough to clear z-fighting with the facade it's mounted flush
    /// against — not a real gap, a render-correctness margin.</summary>
    public float propBillboardProtrusion = 0.05f;
    /// <summary>Reuses the window-light family per the brief: same warm glow as windowLitColor so a
    /// billboard reads as kin to the lit windows rather than a foreign object. Same reasoning as
    /// windowLitColor: an ad panel is a city light, and city lights don't change colour with time of
    /// day.</summary>
    public Color propBillboardColor = new Color32(0xFF, 0xCC, 0x7A, 0xFF);
    /// <summary>Brighter than a window's own glow so a billboard reads as a distinct lit sign, not just
    /// another lit room among many — currently ~1.33x windowEmissiveIntensity. Kept below
    /// interactableEmissiveIntensity: middle rung of the emissive ladder, windows 1.5 &lt; billboards
    /// 2.0 &lt; interactables 2.6, which keeps signs loud without ever letting set dressing out-shout a
    /// gameplay interactable. Pushing this much higher clips to white and stops reading as a sign at
    /// all.</summary>
    public float propBillboardEmissiveIntensity = 2.0f;

    /// <summary>A few slats, not stairs (brief: "do not build stairs") — 4 is enough to read as a
    /// fire-escape run up a facade from any distance a player will ever see it.</summary>
    public int propFireEscapeSlatCount = 4;
    public float propFireEscapeSlatWidth = 1.8f;
    public float propFireEscapeSlatThickness = 0.08f;
    /// <summary>How far each slat sits off the wall — thin on purpose, this is an impressionistic
    /// suggestion of a fire escape, not a modelled structure.</summary>
    public float propFireEscapeSlatDepth = 0.18f;
    /// <summary>~one storey (matches windowSpacingY, 1.5) apart, so the stack reads as climbing past
    /// real floors rather than at an arbitrary rhythm.</summary>
    public float propFireEscapeSlatSpacing = 1.4f;

    [Header("Streets (sidewalk ground slab, RooftopArena only)")]
    public Color sidewalkColor = new Color32(0x4E, 0x50, 0x56, 0xFF);
    /// <summary>The ground slab sits this far below the road strips' top surface — not zero, so the
    /// two coplanar flat surfaces don't z-fight; not large, so the tiny lip stays invisible.</summary>
    public float roadSurfaceLift = 0.02f;

    [Header("Backdrop keep-out (ground slab sizing, RooftopArena only)")]
    /// <summary>XZ margin added to RooftopArena.Roofs' combined bounds to form the backdrop keep-out.
    /// Small on purpose — the backdrop must come right up to the play area's edge, since a wide gap
    /// would read as a bald ring of sidewalk around the city.</summary>
    public float backdropKeepOutMargin = 2f;

    /// <summary>How far the ground slab's edge runs PAST <see cref="skylineOuterRadius"/> — the slab is
    /// sized from the skyline, not from the roofs (it is only CENTRED on them), because its edge has to
    /// die in the fog rather than merely be far away. Deriving it from skylineOuterRadius is the point:
    /// pushing the skyline out can never silently strand the ground short of it.
    ///
    /// From Roof_Tower (the highest playable roof, y=9) the nearest slab edge sits ~460m out, where fog
    /// is ~99.0% — the slab's own colour is gone (~2/255 off pure fogColor) before it ends. This
    /// comfortably clears the outermost skyline blocks too (radius+jitter+width reaches ~385m from the
    /// skyline's centre, ~82m inside the edge), so no silhouette ever hangs off the slab. Below ~60 the
    /// edge drops under 98% fog and starts to show.</summary>
    public float groundEdgeMargin = 140f;

    [Header("Clouds")]
    /// <summary>Moonlit clouds are cool, and much darker than a sunlit sky by comparison: this is an
    /// albedo, and the moon lighting it is dim (see sunIntensity), so a value that would read as "soft
    /// warm cloud" under strong light reads as "floodlit balloon" once it is the brightest thing under a
    /// near-black zenith. Kept dark enough to separate plainly from the zenith it's seen against while
    /// still reading as dim blue masses rather than objects demanding attention. Their bellies get a
    /// faint warm underlight for free from ambientGround (down-facing normals, kept warm as street
    /// bounce) — cool tops, warm undersides, the whole city-at-night cloud read, and it costs nothing to
    /// arrange. Kept dark enough to read correctly against the hand-authored Quaternius cloud meshes,
    /// which catch more sky light than a simple faceted blob would.</summary>
    public Color cloudColor = new Color32(0x3A, 0x40, 0x55, 0xFF);
    /// <summary>Cloud opacity: the shared cloud material renders as URP transparent at this alpha so
    /// the sky gradient reads through.</summary>
    public float cloudAlpha = 0.45f;
    public int cloudCount = 10;
    /// <summary>Cloud altitude band: kept a true sky band far above the decks and perimeter towers so
    /// clouds never crowd a vantage. Free to sit high: clouds are on the minimap-culled Dressing layer
    /// (see SceneStyler.CreateClouds).</summary>
    public float cloudHeightMin = 175f;
    public float cloudHeightMax = 240f;
    /// <summary>Long (drift) axis of a cloud in metres. Quantised into 3 discrete size tiers across
    /// the sky (see SceneStyler.CreateClouds) so the layout reads as varied, not one repeated puff.</summary>
    public float cloudLengthMin = 34f;
    public float cloudLengthMax = 70f;
    /// <summary>Length:width ratio — the flat footprint is this many times longer than it is deep, so
    /// each cloud reads as wider-than-tall rather than a round ball.</summary>
    public float cloudAspectMin = 2.6f;
    public float cloudAspectMax = 3.6f;
    /// <summary>Puffiness: each lobe is a DOME (flat-bottomed at y=0, see AppendIcosphereBlob) whose
    /// height is this multiple of its radius. >1 gives tall, rounded cumulus puffs; the flat base plus
    /// these puffy tops is the asymmetry that reads as "cloud" instead of "cluster of spheres".</summary>
    public float cloudPuffMin = 1.1f;
    public float cloudPuffMax = 1.6f;
    public float cloudDriftSpeedMin = 3f;
    public float cloudDriftSpeedMax = 7f;
    /// <summary>Radius of the drift area centered on the map — a cloud that drifts past this wraps
    /// back around to the opposite edge instead of drifting away forever.</summary>
    public float cloudDriftRadius = 120f;
    /// <summary>Blob count per cloud — each cloud mesh is a cluster of overlapping icosphere blobs
    /// (see SceneStyler.CreateClouds), not a single primitive, so this is the "how lumpy" knob.</summary>
    public int cloudBlobsMin = 5;
    public int cloudBlobsMax = 9;
    /// <summary>Icosphere subdivision level for each blob — the main poly-count/shape-smoothness
    /// knob. 0 is the raw 20-face icosahedron (very faceted, cheap); 1 splits every face into 4
    /// (80 faces); 2 would be 320. Kept low deliberately: low-poly is the aesthetic, not a rounding
    /// error to smooth away.</summary>
    [Range(0, 2)] public int cloudBlobSubdivisions = 1;
    /// <summary>Per-vertex radial jitter, as a fraction of blob radius, so blobs read as irregular
    /// lumps rather than perfect spheres.</summary>
    [Range(0f, 0.4f)] public float cloudVertexJitter = 0.12f;
    /// <summary>Each blob's radius as a fraction of ITS cloud's half-width, so blob size scales with
    /// the cloud it belongs to. Varied radii give the puffy top its staggered, uneven lobes.</summary>
    public float cloudBlobRadiusMin = 0.7f;
    public float cloudBlobRadiusMax = 1.1f;

    [Header("Planes")]
    /// <summary>Small low-poly plane silhouettes flying straight above the cloud band — count kept
    /// low (they're fast, so a handful reads as steady traffic without cluttering the sky).</summary>
    public int planeCount = 3;
    public float planeHeightMin = 120f;
    public float planeHeightMax = 150f;
    /// <summary>Noticeably faster than <see cref="cloudDriftSpeedMin"/>/Max (3-7) so planes read as
    /// distinct high-altitude traffic rather than just another cloud.</summary>
    public float planeSpeedMin = 8f;
    public float planeSpeedMax = 14f;
    /// <summary>Radius of the drift area centered on the map — reuses the clouds' wrap convention
    /// (see <see cref="cloudDriftRadius"/>) so planes loop within the same visible bounds.</summary>
    public float planeDriftRadius = 120f;
    /// <summary>Uniform scale factor applied to the whole plane silhouette (fuselage + wings + tail).</summary>
    public float planeScale = 1f;

    [Header("Ambience")]
    /// <summary>Path to a pre-authored looping city ambience clip (car horns, distant traffic) — a
    /// quiet-but-present street bed. Procedural synthesis is not used for this: prior attempts at
    /// generating it produced poor results, so this stays a pre-authored file. The file may not exist
    /// yet at build time; SceneStyler.CreateAmbience must skip cleanly rather than fail.</summary>
    public string ambienceClipPath = "Assets/Audio/city-ambience.ogg";
    [Range(0f, 1f)] public float ambienceVolume = 0.10f;

    [Header("Post-processing")]
    /// <summary>The filmic tonemap applied to the HDR frame — the base of the whole grade, run before
    /// bloom/contrast are even meaningful. Without one, the emissive ladder (windows 1.5 / billboards 2.0
    /// / interactables 2.6) clips flat past 1.0 and the windows never read as glowing light sources, just
    /// bright stickers. Neutral, NOT ACES, on purpose: ACES desaturates and hue-shifts warm tones, which
    /// would drain the exact warm window/sign colours this night scene is built around (see colorFilter and
    /// the class remarks — cool ambient, warm points of interest). Neutral keeps that palette intact while
    /// still giving a soft filmic rolloff into the highlights.</summary>
    public UnityEngine.Rendering.Universal.TonemappingMode tonemapMode =
        UnityEngine.Rendering.Universal.TonemappingMode.Neutral;
    /// <summary>Bloom is ADDITIVE, and against a night frame it reads much stronger than the same value
    /// would against a bright scene — there is no ambient brightness for it to disappear into. This is
    /// the knob where night either sings or turns to mush: the city's lights are supposed to be points,
    /// so too high a value merges a facade's lit rooms into a single glowing block. Kept high enough
    /// (0.72) that the construction site's worklights read as genuinely glowing, while still short of
    /// the point where windows, billboards and ledge trims stop being individually resolvable.</summary>
    public float bloomIntensity = 0.72f;
    /// <summary>At night nothing NON-emissive comes close to 1.0 (the brightest albedo in the scene is
    /// concreteFloor under a dim moon), so this threshold cleanly separates "is a light" from "is lit by
    /// one" without any tuning. Everything above it is deliberate — the emissive ladder (windows 1.5 /
    /// billboards 2.0 / interactables 2.6) and the rim trims — which is exactly the set of things that
    /// should bloom.</summary>
    public float bloomThreshold = 1.0f;
    /// <summary>A vignette darkens the frame's edges, which is a cheap focus trick against a bright scene
    /// but a real tax against a dark one — it subtracts from frames that have little brightness to give,
    /// and it lands hardest on the street-death camera (already the darkest shot in the game). Kept
    /// low and nonzero because it still pulls the eye to centre-frame; just not pushed higher, since here
    /// it isn't free.</summary>
    public float vignetteIntensity = 0.12f;
    /// <summary>Contrast pivots around mid-grey: it pushes values above the pivot up and values BELOW it
    /// down. This scene lives almost entirely below the pivot (a dark night frame), so a high contrast
    /// value here would crush the whole scene toward black and take the street's readability with it.
    /// Kept low (4) to give some snap without fighting the theme's darkness.</summary>
    public float postContrast = 4f;
    /// <summary>Kept mild deliberately: pulling saturation further for "night looks desaturated" realism
    /// would drain the warm city lights, and those ARE the theme (see the class remarks — cool ambient,
    /// warm points of interest). The cool/warm split is doing the mood work; this does not need to
    /// help.</summary>
    public float postSaturation = -5f;
    /// <summary>A gentle cool tint. This multiplies EVERYTHING, which is why it is only barely tinted
    /// rather than pushed to a strong night-blue: a heavy cool filter would drain the warm windows and
    /// billboards, the exact accents this theme is built around. It only needs to stop actively fighting
    /// the palette (a warm filter over a moonlit scene reads as a colour-correction error) — the moon and
    /// ambients are already doing the real cooling.</summary>
    public Color colorFilter = new Color32(0xE6, 0xEC, 0xFA, 0xFF);
    /// <summary>Kept low — just enough to soften whip-pans and mantle camera snaps,
    /// not a strong cinematic blur that would fight the "feel fast" movement-first goal.</summary>
    [Range(0f, 1f)] public float motionBlurIntensity = 0.12f;
}
