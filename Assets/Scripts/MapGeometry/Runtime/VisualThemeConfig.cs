#nullable enable

using UnityEngine;

namespace Game.MapGeometry;

/// <summary>
/// Single source of truth for the "moonlit night over the construction site" visual pass
/// (docs/superpowers/specs/2026-07-12-visual-pass-design.md). Presentation values only —
/// nothing here may influence simulation. Like MovementConfig, this is instantiated via
/// CreateInstance at build time (the defaults ARE the theme); scenes never persist a
/// reference to a config asset (see PlaygroundBuilder's remarks on the deserialization bug).
///
/// RE-THEMED golden-hour sunset -> NIGHT. The city was already full of interior lights it was
/// contradicting: emissive window grids, emissive billboards, a lit skyline. Interior lights do no
/// work against a bright sky — they only read once the sky is dark, so the sunset was spending the
/// scene's whole light budget arguing with its own set dressing. It is also the fitting story: a
/// raccoon fleeing pest control over rooftops is nocturnal.
///
/// The organising rule for every value below, and the thing to preserve when retuning: the MOON is
/// the only cool key, and the CITY ITSELF supplies every warm accent (windows, billboards, the
/// horizon light dome). Cool ambient + warm points of interest. Anything that was warm because the
/// SUN was warm went cool; anything warm because a CITY LIGHT is warm stayed exactly as it was
/// (windowLitColor and propBillboardColor are untouched — they were always right, they just had
/// nothing to read against).
///
/// The field names in the Sun header still say "sun". They are the MOON now; renaming ripples
/// through SceneStyler.ApplyEnvironment and the GradientSkybox shader's _SunColor/_SunDirection/
/// _SunSize properties for zero benefit.
/// </summary>
[CreateAssetMenu(fileName = "VisualThemeConfig", menuName = "RooftopTag/Visual Theme Config")]
public sealed class VisualThemeConfig : ScriptableObject
{
    [Header("Sky")]
    /// <summary>#3B2E5E -> #080B16. Near-black blue: the top of a night sky, and the darkest value in
    /// the whole theme. It has to bottom out this hard for the rest of the pass to work — every window,
    /// billboard and rim trim below is sold by CONTRAST against this, not by its own brightness (which
    /// is why several emissive intensities could come DOWN in this re-theme rather than up).</summary>
    // Construction-zone re-theme (round 5, matched to the Nano Banana concept): the whole night moved
    // from violet-grey toward saturated INDIGO-BLUE and brightened a step — the concept reads as a
    // blue-hour diorama with hot orange worklights, not near-black. Band monotonicity and the
    // skyHorizon == skyGround == fogColor seam rule are preserved.
    public Color skyZenith = new Color32(0x0C, 0x12, 0x2A, 0xFF);
    /// <summary>#B45252 (the sunset's red band) -> #141A2C, a dark blue sitting between the zenith and
    /// the horizon's light dome. Monotonic by value with its neighbours — horizon (46,44,58) > mid
    /// (20,26,44) > zenith (8,11,22) — so the sky darkens all the way up with no band inversion.</summary>
    public Color skyMid = new Color32(0x1B, 0x24, 0x45, 0xFF);
    /// <summary>The sky AT the horizon, and this is why it is exactly <see cref="fogColor"/> (was
    /// #F0904A, then #D9906A at sunset, now the night fog): Unity's fog does NOT apply to the skybox, so
    /// wherever fully-fogged geometry meets the sky it meets an UNFOGGED colour, and any mismatch reads
    /// as a seam along the horizon. Matching them is the physical truth (distant haze IS what you see at
    /// the horizon) and it is what makes the ground slab's far edge — 99% fogged, so already pure
    /// fogColor — dissolve into the sky instead of ending at a visible line. The night re-theme does not
    /// weaken this: it STRENGTHENS it, because a light dome genuinely is "the haze, seen edge-on", so
    /// horizon == fog is now the literal physical claim rather than a convenient match. The punch at the
    /// horizon still comes from the moon disc the skybox shader ADDS on top, and skyMid still drives the
    /// gradient above. Keep these two and fogColor equal — retuning one alone re-opens the seam.</summary>
    public Color skyHorizon = new Color32(0x2C, 0x37, 0x60, 0xFF);
    /// <summary>Below-horizon sky. Also fogColor (was #FFC873, then #D9906A), same reason as
    /// <see cref="skyHorizon"/> — and it costs nothing: the ground slab now spans past the skyline, so in
    /// RooftopArena this is only visible in the ~4-degree sliver between the slab's far edge and the
    /// horizon. (In MovementPlayground, which builds no slab, it reads as a flat haze floor.)</summary>
    public Color skyGround = new Color32(0x2C, 0x37, 0x60, 0xFF);
    /// <summary>0.35 -> 0.25. Where <see cref="skyMid"/> lands as a fraction of the way up the sky, so
    /// lowering it COMPRESSES the horizon->mid transition down toward the horizon. That is the shape of a
    /// real light dome: a city's glow is a tight band hugging the skyline that falls off fast, not a
    /// gradient washing a third of the way to the zenith. At 0.35 the dome bled high enough to read as
    /// dusk that forgot to finish.</summary>
    [Range(0.05f, 0.9f)] public float skyMidPoint = 0.25f;

    [Header("Sun — this is the MOON now (see the class remarks on why the names stayed)")]
    /// <summary>13 -> 34 degrees, and this is the single most load-bearing number in the re-theme: it is
    /// what let the key light drop 56% without taking the playable surfaces with it. Direct light on an
    /// UP-facing surface scales with sin(elevation), on a wall with cos(elevation):
    ///   roofs: sin(13)=0.22 -> sin(34)=0.56   |   walls: cos(13)=0.97 -> cos(34)=0.83
    /// So raising the moon buys roofs 2.5x more direct light while barely touching walls. Combined with
    /// <see cref="sunIntensity"/> 1.25 -> 0.55 the net is exactly the split the night wants: ROOFS (where
    /// you play, and where the ledge trims live) hold roughly their sunset brightness, WALLS fall to ~37%
    /// so the window grids finally have something dark to pop against.
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
    /// <summary>#FFD98A (warm sun) -> #B9C9EC, cool blue-white. Drives BOTH the directional light's colour
    /// and the skybox's _SunColor (the disc itself), so this one knob is the moon as a light and the moon
    /// as an object. Cool is the whole point of the re-theme's organising rule: this is the ONLY cool key,
    /// and every warm thing left in the scene is therefore unambiguously a city light.</summary>
    public Color sunColor = new Color32(0xB9, 0xC9, 0xEC, 0xFF);
    /// <summary>1.25 -> 0.55. It is a moon. Read this together with <see cref="sunElevationDegrees"/>
    /// (13 -> 34), which is what makes a cut this deep safe rather than reckless — the two were retuned as
    /// one move and neither number means anything alone.
    ///
    /// The knock-on worth knowing before touching anything emissive: dropping the key 56% multiplies the
    /// apparent strength of EVERY emissive in the theme by ~2.3x for free. That is why the window,
    /// billboard and bloom intensities below all went DOWN in a re-theme toward darkness — they were
    /// tuned to shout over a sunset and no longer have to.</summary>
    public float sunIntensity = 0.55f;
    /// <summary>Shader pow() exponent for the moon disc: higher = smaller, sharper disc. 384 -> 1400. A
    /// sunset sun is a big soft glow bleeding into the sky (which is what 384's ~3.4deg half-max disc
    /// drew); a moon is a small hard-edged object. 1400 gives ~1.8deg — still ~3.6x the real moon's
    /// 0.5deg, deliberately: a truly correct disc lands on ~2 pixels and aliases into a sparkle.</summary>
    public float sunDiscSize = 1400f;

    [Header("Ambient (trilight)")]
    /// <summary>#6B5480 -> #333C5C. NOTE FOR ANYONE RETUNING THE STREET: Unity's Trilight ambient is
    /// sampled by a surface's NORMAL, so ambientSky lights every UP-FACING surface — which means THIS is
    /// the street's light, not <see cref="ambientGround"/> (that one only lights undersides). The road at
    /// y=-25 faces up, gets no direct moon at all (see sunElevationDegrees on why 34deg cannot reach a
    /// canyon floor), and is therefore lit by this value and this value alone.
    ///
    /// So it is deliberately only cut to ~66% of the sunset's luminance rather than crushed to night
    /// levels: it is doing double duty as the roofs' fill AND as the sole thing making the street-death
    /// sequence watchable. Night is sold here by HUE (purple sun-bounce -> cool blue) and by the black sky
    /// and popping windows around it — not by driving this toward zero, which would take the street with
    /// it. Do not lower this without raising sidewalkColor to match.</summary>
    public Color ambientSky = new Color32(0x40, 0x4E, 0x82, 0xFF);
    /// <summary>#C97B5A -> #3A3446, ~40% of the old luminance — the largest ambient cut in the re-theme,
    /// and the one that does the most work. This lights VERTICAL surfaces (normals at the horizon), i.e.
    /// the facades, and at sunset it was a strong warm bounce that kept every wall glowing — which is
    /// precisely what left the window grids with nothing to read against. Now it is the city's own dome
    /// (tinted toward fogColor's violet) faintly lifting the walls off black. Facades land at ~40% of
    /// their sunset brightness between this and the key: that gap IS the window pop.</summary>
    public Color ambientEquator = new Color32(0x33, 0x3C, 0x62, 0xFF);
    /// <summary>#4A3844 -> #2A2430 (~67%). DOWN-facing surfaces only — ledge undersides, overhangs, the
    /// cosmetic masses' soffits, cloud bellies. Despite the name this is NOT the street's light (see
    /// <see cref="ambientSky"/>); it is the light the street throws back UP. Kept faintly warm rather than
    /// going cool with everything else, because that is what it physically is: sodium bounce off tarmac.
    /// It buys the clouds' undersides a free warm underlight against the cool moonlit tops.</summary>
    public Color ambientGround = new Color32(0x2A, 0x24, 0x30, 0xFF);

    [Header("Fog & street haze")]
    /// <summary>#D9906A (sunset haze) -> #2E2C3A. NOT neutral black, and that is the entire point: a real
    /// city at night sits under a LIGHT DOME, and a dark blue-grey with a faint warm lift reads as that
    /// dome where pure black reads as a missing skybox. Mixed as ~10% of a warm sodium glow into a deep
    /// night blue, which lands R and G level with each other and B a little over both (46,44,58) — cool
    /// overall, but lifted just off the blue axis so it reads as air with a city under it.
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
    /// 0.010-exponential -> 0.006-expsq once the ground plane landed. Fog now has to satisfy two
    /// demands at once that plain exponential cannot: hide the world's edge 460m out, AND leave the
    /// ~34m of air between a roof and the street clear enough to read concrete as CONCRETE. Exponential
    /// is near-linear up close, so buying 99% at the edge cost 29% wash at 34m — the whole city came
    /// out salmon (verified: CityGroundingAudit shots, every surface past ~10m converged to fogColor).
    /// Squared is flat near and steep far, which is exactly the shape wanted:
    ///   34m: 29% -> 4%   |   340m (skyline): 96.7% -> 98.4%   |   460m (ground edge): 99.0% -> 99.95%
    /// Better at BOTH ends. Raising this past ~0.007 starts tinting the play area again; below ~0.005
    /// the ground's far edge climbs back out of the fog and the world-edge returns.</summary>
    // Back to 0.006 (from the 0.007 fog-as-culling pass): the horizon is now a solid Kenney building
    // WALL (~160-190m out) instead of a sparse fogged skyline, and at 0.007 the wall itself drowned. At
    // 0.006 the wall rows read as dark silhouettes closing off the map while depth haze remains.
    public float fogDensity = 0.006f;
    public int hazePlaneCount = 3;
    /// <summary>Y of the highest haze plane — must sit strictly BELOW the lowest walkable roof
    /// surface. The construction zone's lowest floor (Con_Yard) is y=1.5; a haze plane at exactly
    /// 1.5 was perfectly coplanar with that roof's top face, z-fighting as visible shadow-like
    /// flicker whenever the camera moved (the map expansion invalidated the original "roofs start
    /// at y=3" assumption this value was picked under).</summary>
    public float hazeTopY = 1.0f;
    /// <summary>2 -> 8 with the continuous ground plane: at 2 the three planes sat at y 1/-1/-3, a 4m
    /// lid hugging the roofline with 22m of empty air below it and the street at -25 — they read as one
    /// plane (no parallax) and did nothing for the street they exist to drown. At 8 they sit at 1/-7/-15
    /// and actually span the roof-to-street gap, so moving the camera separates them into layers.
    /// Roof-to-roof readability is unchanged either way: you stand above all three.</summary>
    public float hazeSpacing = 8.0f;
    /// <summary>0.16 -> 0.07 -> 0.03. The first cut was for solid ground replacing void; this one is
    /// because these planes were quietly doing the real fog's job. The stack multiplies (planes are
    /// alpha a, 1.5a, 2a), so 0.07 was still a flat 28% of fogColor laid over everything below the
    /// roofline REGARDLESS of distance — depth haze that ignores depth. Stacked on exponential fog's
    /// 29% it put the street ~49% erased and turned the concrete salmon.
    ///
    /// With fogDensity now expsq (see its remarks) the real fog handles distance properly, so these
    /// go back to being what they are for: a few soft layers that give the canyon parallax as the
    /// camera moves. 0.03 = a 12% stack — felt, not seen. Push it back up only if the street reads
    /// too crisp from a rooftop; it is the wrong knob for "the horizon doesn't fade".</summary>
    public float hazeBaseAlpha = 0.03f;
    public float hazePlaneSize = 400f;

    [Header("Concrete palette")]
    /// <summary>Muted, near-neutral greys (a hair cool). These were a purple-tinted grey, which fought
    /// the golden-hour sun/sky for control of the scene's colour and read as stylised rather than
    /// "real city". Desaturating them hands the colouring back to the light: the same wall now goes
    /// warm in the sun and cool in shadow on its own. Values (92/110) are unchanged from the
    /// purple palette — only the hue/saturation went; the wall/floor read order is preserved.
    ///
    /// NIGHT: values 92/110 -> 110/131, a flat x1.19 on both. Hue and saturation are
    /// untouched and MUST stay that way — the desaturation logic above is not a sunset artefact, it is
    /// the reason the re-theme was cheap at all. These greys never knew what colour the light was, so
    /// the same walls that went warm under the sun now go cool under the moon for free; the only thing
    /// that needed fixing was that the moon brings less light, not that it brings a different colour.
    /// The x1.19 is that compensation and nothing more. Read order (wall &lt; floor) is
    /// preserved exactly by scaling both together.
    ///
    /// Ramps used to live here too (concreteRamp) — retired by the plank-deck pass below: ramps are
    /// wood now, and a field named "concrete" feeding a wood surface would just lie. See the
    /// "Ramp planks" header for its replacement (rampWoodColorLight/Dark).</summary>
    public Color concreteWall = new Color32(0x6E, 0x6E, 0x71, 0xFF);
    public Color concreteFloor = new Color32(0x83, 0x83, 0x85, 0xFF);
    /// <summary>Per-building brightness variation (seeded, deterministic) so facades don't read as clones.
    /// Raised 0.05 -> 0.08 with the facade pass: neighbouring buildings now share a window grid as well
    /// as a tint, so they need more value separation than before to keep reading as separate buildings.</summary>
    [Range(0f, 0.15f)] public float wallValueJitter = 0.08f;

    [Header("Ramp planks (RooftopArena.BuildRamp's wood deck)")]
    /// <summary>Boards laid across the ramp's 3m width. 4 -> ~0.75m boards, a believable plank width;
    /// this is also the generated wood atlas's band count (see TagArenaMapGeometry.BuildRampPlankAtlas)
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
    /// number of cells (see TagArenaMapGeometry.CreateBuildingBox), so these are the target spacing, not
    /// the exact one — which is what keeps a window from being clipped in half at a corner or roof lip.
    /// 2.0 -> 1.0 on the feel-check: windows read as too few and too big (an 8m face now gets 8 columns
    /// rather than 4).</summary>
    public float windowSpacingX = 1.0f;
    /// <summary>Vertical cell size — read this as the building's floor-to-floor height. 2.4 -> 1.5 with
    /// windowSpacingX, same reason (the 21m Tower column goes ~9 rows -> 14).</summary>
    public float windowSpacingY = 1.5f;
    /// <summary>Window size as a fraction of its cell; the remainder is the wall border around it.
    /// Both are kept well under 1 so every cell keeps a border (a window that filled its cell would
    /// merge with its neighbours into continuous glass bands). Net window is ~0.5m wide x ~0.93m tall:
    /// taller than wide, which is what reads as a city window rather than a porthole.</summary>
    [Range(0.1f, 0.9f)] public float windowWidthFraction = 0.5f;
    [Range(0.1f, 0.9f)] public float windowHeightFraction = 0.62f;
    /// <summary>Share of windows with the interior light on. 0.22 -> 0.45 for the night. The old note
    /// still holds and is why this is 0.45 and not 0.9: "a mostly-dark facade with scattered lit rooms
    /// reads as a lived-in city at dusk; a high value reads as an office block on fire" — that failure
    /// mode does not go away after dark, it gets WORSE, because at night a fully-lit facade is the only
    /// bright object in frame. What changes is where the line sits: at 2200h a real city has plenty on
    /// but nowhere near a majority, so 0.45 is "the city is awake" while still leaving most of every
    /// facade dark for the lit rooms to punch out of.</summary>
    [Range(0f, 1f)] public float windowLitChance = 0.45f;
    /// <summary>Warm interior glow — the only thing on a facade that emits. UNCHANGED by the night
    /// re-theme, deliberately: this was never a sunset colour, it is an interior light, and interior
    /// lights are the same colour at midnight as they were at dusk. It is now the scene's principal warm
    /// accent rather than something competing with a warm sky (see the class remarks' organising rule).</summary>
    public Color windowLitColor = new Color32(0xFF, 0xCC, 0x7A, 0xFF);
    /// <summary>Unlit glass: a dark cool multiplier over the wall tint (the albedo atlas MULTIPLIES
    /// concreteWall), so windows stay windows on every per-building tint. #3A3F4A -> #14161C for the
    /// night: an unlit room at 2200h has nothing behind the glass, and because this multiplies, the value
    /// here is a FRACTION of the wall (20/255 = 8%) — so dark windows now land near-black against the
    /// x1.19-lifted concrete regardless of per-building tint. The 8% floor rather than 0 keeps the glass
    /// catching a hint of the moon instead of reading as a hole punched in the building.</summary>
    public Color windowDarkColor = new Color32(0x14, 0x16, 0x1C, 0xFF);
    /// <summary>2.6 -> 1.5, and the direction surprises people, so: this is a re-theme toward DARKNESS in
    /// which the windows got dimmer. 2.6 was the number needed to shout over a golden-hour sky. With the
    /// key light down 56% (see sunIntensity) and bloom in play, 2.6 against a night facade does not read
    /// as "brighter" — it clips to a white blob, loses the warm hue that is the entire point, and bleeds
    /// into its neighbours until a facade is one glowing smear. 1.5 keeps each lit room a distinct warm
    /// rectangle that still crosses bloomThreshold (1.0) and blooms deliberately rather than helplessly.
    /// This is also the bottom rung of the emissive ladder that carries the scene's hierarchy now that the
    /// key cannot: windows 1.5 &lt; billboards 2.0 &lt; interactables 2.6.</summary>
    public float windowEmissiveIntensity = 1.5f;
    /// <summary>The generated atlas is this many cells square, and — because facade UVs are laid out in
    /// cell units and wrapped — this is also the pattern's repeat period in windows. Too small and the
    /// repeat is legible across a wide facade. 16 -> 32: at windowSpacingX=1 an 8m face spends 8 cells,
    /// so 16 repeated every two faces.</summary>
    public int windowAtlasCells = 32;
    /// <summary>Texels per cell. atlas size = windowAtlasCells * windowCellPixels, square. 16 -> 32 on
    /// the feel-check ("low quality / a bit blurry"): a 16px cell stretched over a 1-2m face is roughly
    /// 8-16 texels per metre, which magnifies visibly up close. Note the atlas is 4x the memory per
    /// doubling — see MakeAtlas.</summary>
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

    /// <summary>Round 5: the playable cluster is an UNDER-CONSTRUCTION site (Nano Banana concept):
    /// concrete-shell facades restyled onto the existing collider-exact roof bodies/masses (no shell
    /// models, no renderer stripping), plus ConstructionShells toppers and ConstructionDressing
    /// props/cranes/worklights. When true, the Tripo GLB shells are skipped for the playable towers —
    /// the backdrop city keeps its finished Kenney look (deliberate contrast: a construction site
    /// inside a living city). Movement geometry untouched either way.</summary>
    public bool constructionZone = true;

    /// <summary>Round 7: playable towers are stacked from the user's modular building GLBs
    /// (Assets/buildings — bottom/middle/top per type, see ModularBuildings). Takes precedence over
    /// the generated construction facades when constructionZone is also true; flip off to fall back
    /// to the ConstructionShells generated look. Colliders identical either way.</summary>
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
    /// shell down into the dark slate/blue city so none of them read as bright cream towers (user feedback,
    /// twice: the "light buildings" were these shells, not the Kenney placements). Windows still glow —
    /// emission is independent of the base multiply.</summary>
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
    /// <summary>#FFB668 (warm, "sun-lit roof edge") -> #BFD4F5, a pale cool white-blue: moonlight
    /// catching the parapet. This trim is NOT decoration — it outlines every ledge so players can read
    /// where a roof ends at running speed — so the hue was chosen for contrast, not for mood.
    ///
    /// Cool, specifically, because warm was the one option that would have broken it. Night makes the
    /// city's lights the dominant warm thing in frame (windows at #FFCC7A, billboards the same), and a
    /// warm-amber line threaded along every ledge would have been one more amber object among hundreds —
    /// legible as glow, useless as an EDGE. Cool puts the trim in the only hue family nothing else in the
    /// scene occupies, so a ledge is now the one pale-blue line on a warm-speckled dark facade. It also
    /// makes it the moon's, which is the only light that could plausibly be up there.
    ///
    /// It reads better than it did at sunset, which was the requirement: at 1.6 against a golden sky the
    /// old trim was an orange line on orange-lit concrete, fighting its own key light for contrast.</summary>
    public Color rimColor = new Color32(0xBF, 0xD4, 0xF5, 0xFF);
    /// <summary>HELD at 1.6 through the night re-theme — this is a decision, not an oversight. Cutting the
    /// key 56% (see sunIntensity) already multiplies this trim's contrast against its facade by ~2.3x for
    /// free, so 1.6 buys MORE ledge readability at night than 1.6 bought at sunset while still being the
    /// same restrained line rather than a glowing tube. Dimming it to "match the darkness" would spend the
    /// one thing that got better for free and take the ledge cue with it. It stays over bloomThreshold
    /// (1.0) on purpose: the slight bloom is what makes a ledge catch your eye peripherally, which is
    /// exactly when you need it.</summary>
    // 1.0, was 1.6: under the construction re-theme's stronger bloom the rims flared into glaring
    // white outlines that dominated every shot; 1.0 keeps the functional ledge glow (readability at
    // speed) without the flare. (Round 5.)
    public float rimEmissiveIntensity = 1.0f;
    public float rimThickness = 0.15f;
    public float rimHeight = 0.12f;

    [Header("Interactables (safety orange)")]
    /// <summary>Safety orange, UNCHANGED. This is reserved gameplay colour language for player-usable
    /// things and re-hueing it to suit an art pass would be trading a learned signal for a mood. It
    /// survives the night palette on saturation: it is the only fully-saturated orange in the scene,
    /// where the city's lights are all pale warm amber (#FFCC7A), and it now sits at the top of the
    /// emissive ladder besides (see interactableEmissiveIntensity).</summary>
    public Color interactableColor = new Color32(0xF0, 0x70, 0x20, 0xFF);
    /// <summary>2.2 -> 2.6. Raised, in a re-theme where windows came DOWN (2.6 -> 1.5), and for exactly
    /// that reason: with the key light gone the emissive ladder is the only hierarchy left, and
    /// interactables must sit unmistakably at its top. At sunset this was 2.2 against windows at 2.6 —
    /// interactables were actually DIMMER than the set dressing and got away with it because the sun was
    /// doing the hierarchy. Against a night city that would have buried them. The ladder is now explicit
    /// and ordered by gameplay importance: windows 1.5 &lt; billboards 2.0 &lt; interactables 2.6.</summary>
    public float interactableEmissiveIntensity = 2.6f;

    [Header("Silhouettes (cranes, far skyline)")]
    /// <summary>#4A3844 -> #1E1C28. The far city's base tone, before <see cref="skylineHazeBlend"/> pushes
    /// each band toward <see cref="fogColor"/>. Pushed WELL below the new fog (#2E2C3A) rather than near
    /// it, and that gap is the whole trick: it is what keeps atmospheric perspective alive at night. If
    /// this sat at the fog's value the near and far bands would both resolve to fog and the skyline would
    /// flatten into one silhouette-free smear. At 30/28/40 the near band reads as genuinely dark buildings
    /// and the far band dissolves up into the brighter dome — distance makes things LIGHTER at night, the
    /// opposite of the sunset's direction, and correct: haze glows, it doesn't shadow.
    /// Also reused for cranes and the fire-escape props, which want to be dark shapes after dark anyway.</summary>
    public Color silhouetteColor = new Color32(0x1E, 0x1C, 0x28, 0xFF);
    /// <summary>How many DISTINCT haze tints the backdrop skyline is allowed — the draw-call knob, not a
    /// layout one. Buildings' distance-from-centre t is continuous; quantising t into this many bands is
    /// what keeps TagArenaMapGeometry.GetFacadeMaterial's (tint, intensity) cache yielding exactly this
    /// many shared materials instead of one per building. 4 was the old ring count and reproduces the old
    /// gradient exactly; raising it buys smoother atmospheric perspective at one draw call each.</summary>
    public int skylineHazeBandCount = 4;
    /// <summary>Outer edge of the backdrop ground slab / keep-out sizing (see BackdropBounds): the slab's
    /// square extent is sized from this plus groundEdgeMargin so its edge dies in fog well past anything
    /// placed near it. Per-band colour is pushed toward <see cref="fogColor"/> by
    /// <see cref="skylineHazeBlend"/> across it, for atmospheric perspective.</summary>
    // Pulled 340 -> 240 with the heavier fog (fogDensity 0.007): the 240-340 ring sat at 95-99% fog, so
    // dropping it is nearly invisible but culls its buildings' draw calls, and it compresses the
    // near->far fill gradient into the band you can actually see, for a denser-reading near city.
    public float skylineOuterRadius = 240f;
    [Range(0f, 1f)] public float skylineHazeBlend = 0.75f;
    /// <summary>Skyline blocks carry the SAME window grid as the playable buildings — without it the
    /// windowed play area met an unwindowed horizon, which is the break the feel-check reported. Still
    /// dimmer than the buildings' <see cref="windowEmissiveIntensity"/> for the original reason (these sit
    /// behind fog and should not out-glow the ones you can stand on), but the RATIO deliberately climbed
    /// with the night: 1.8 -> 1.3 while the buildings went 2.6 -> 1.5, i.e. 69% -> 87% of the near city's
    /// glow. After dark the far skyline has essentially no albedo left to read (silhouetteColor is #1E1C28
    /// and 75% fogged) — its windows ARE the far city. Holding the old 69% would have handed the horizon a
    /// dark band the eye reads as nothing at all.</summary>
    public float silhouetteWindowEmissiveIntensity = 1.3f;
    /// <summary>How much of a band's window glow the haze eats at the OUTERMOST band (scaled by the band's
    /// distance t, so the nearest band is unfaded). Deliberately below 1: distant windows must still read,
    /// so the far band keeps some of its glow rather than going black. 0.6 -> 0.5 for the night (far band
    /// now keeps 50%, was 40%) — same reasoning as silhouetteWindowEmissiveIntensity: at dusk the fog
    /// eating distant glow still left a lit facade behind it, at night it would leave nothing.</summary>
    [Range(0f, 1f)] public float silhouetteWindowHazeFade = 0.5f;

    [Header("Building masses (cosmetic downward extension of each playable roof)")]
    /// <summary>Y where RooftopArena's roof bodies stop (BuildingSkirt = 3 -> every body bottoms out
    /// at -3, regardless of roof height). The cosmetic mass continues each building's exact footprint
    /// straight down from here to <see cref="buildingBaseY"/> so rooftops read as the TOP of a real
    /// building rather than a floating slab. Coupled to RooftopArena.BuildingSkirt: if that changes,
    /// update this so the seam stays flush.</summary>
    public float buildingBodyBottomY = -3f;
    /// <summary>Street level: the ground slab, the road strips, the cars and every building mass's
    /// bottom all derive from this one knob. -12 -> -25 when the street got its collider: falling off
    /// a roof must CROSS RoundController.FallResetY (-15) on the way down, and at -12 the street sat
    /// below that line — an agent would land on it having never tripped the fall check, stranding
    /// bots on the street forever and never losing the round for the player. The fix is the street,
    /// not the threshold: -15 is what SelfPlayTests' fall metric is calibrated against, and moving it
    /// would change when bots respawn headless. Buildings gain ~22m of facade below the roof line,
    /// which the city wanted anyway.</summary>
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
    /// <summary>Reuses the window-light family per the brief: same warm glow as windowLitColor
    /// (0xFFCC7A) so a billboard reads as kin to the lit windows rather than a foreign object. UNCHANGED
    /// by the night re-theme, same reasoning as windowLitColor: an ad panel is a city light, and city
    /// lights do not change colour when the sun goes down. It is simply doing its job now.</summary>
    public Color propBillboardColor = new Color32(0xFF, 0xCC, 0x7A, 0xFF);
    /// <summary>3.0 -> 2.0. Still brighter than a window's own glow for the original reason (a billboard
    /// reads as a distinct lit sign, not just another lit room among many) — and the SEPARATION actually
    /// grew doing it: 3.0/2.6 = 1.15x at sunset, 2.0/1.5 = 1.33x now. The absolute number fell because the
    /// key light fell 56% with it; 3.0 against a night facade clips to white and stops being a sign at
    /// all. Middle rung of the emissive ladder: windows 1.5 &lt; billboards 2.0 &lt; interactables 2.6,
    /// which keeps signs loud without ever letting set dressing out-shout a gameplay interactable.</summary>
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
    /// Was streetGroundMargin=30 (margin past the ROOFS, a ~100m footprint -> the slab ended at ~90,
    /// only 59% fogged: a plainly visible edge, with the skyline floating past it out to 340).
    ///
    /// 140 -> a 480 half-extent. From Roof_Tower (the highest playable roof, y=9) the nearest edge is
    /// 460 out, where exp fog at density 0.010 is 1-exp(-4.60) = 99.0% — the slab's own colour is gone
    /// (~2/255 off pure fogColor) before it ends. Comfortably clears the outermost skyline blocks too
    /// (radius+jitter+width reaches ~385 from the skyline's centre, ~82m inside the edge), so no
    /// silhouette ever hangs off the slab. Below ~60 the edge drops under 98% and starts to show.</summary>
    public float groundEdgeMargin = 140f;

    [Header("Clouds")]
    /// <summary>#FFE4C0 (warm, sunlit) -> #6E7590. Moonlit clouds are cool, and much darker: this is an
    /// albedo, and the moon lighting it is 0.55 where the sun was 1.25, so a value that read as "soft warm
    /// cloud" reads as "floodlit balloon" once it is the brightest thing under a near-black zenith. At
    /// 110/117/144 under the moon they land around (55,58,72) — plainly separated from the #080B16 zenith
    /// they are seen against, but as dim blue masses rather than objects demanding attention.
    /// Their bellies get a faint warm underlight for free from ambientGround (down-facing normals, kept
    /// warm as street bounce) — cool tops, warm undersides, which is the whole city-at-night cloud read
    /// and cost nothing to arrange.</summary>
    // Darkened for the Quaternius model clouds (round 3): the hand-authored meshes catch far more sky
    // light than the old faceted blobs did, so the same tint rendered pale gray against the night.
    public Color cloudColor = new Color32(0x3A, 0x40, 0x55, 0xFF);
    /// <summary>Cloud opacity (round 4, re-applied after a concurrent-session revert): the shared cloud
    /// material renders as URP transparent at this alpha so the sky gradient reads through.</summary>
    public float cloudAlpha = 0.45f;
    public int cloudCount = 10;
    /// <summary>Cloud altitude band. Round 4 ("much much higher", re-applied): a true sky band far
    /// above the decks and perimeter towers so clouds never crowd a vantage. Free to sit high: clouds
    /// are on the minimap-culled Dressing layer (see SceneStyler.CreateClouds).</summary>
    public float cloudHeightMin = 175f;
    public float cloudHeightMax = 240f;
    /// <summary>Long (drift) axis of a cloud in metres. Quantised into 3 discrete size tiers across
    /// the sky (see SceneStyler.CreateClouds) so the layout reads as varied, not one repeated puff.
    /// Round 4: "slightly smaller" — 34-70, was 44-96 (re-applied).</summary>
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
    /// quiet-but-present street bed, per feel-test. Procedural synthesis is banned for this project
    /// (two prior attempts failed badly — see TUNING_LOG "Wind audio removed entirely"). The file may
    /// not exist yet at build time; SceneStyler.CreateAmbience must skip cleanly rather than fail.</summary>
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
    /// <summary>0.65 -> 0.45. Bloom is ADDITIVE, so the same 0.65 that read as a tasteful sheen when added
    /// to a bright sunset reads roughly twice as strong added to a night frame — nothing has changed about
    /// the bloom except that there is now no ambient brightness for it to disappear into. This is the knob
    /// where night either sings or turns to mush: the city's lights are supposed to be points, and at 0.65
    /// a facade at windowLitChance 0.45 merges its lit rooms into a single glowing block. 0.45 keeps
    /// windows, billboards and ledge trims individually resolvable while still bleeding enough to sell
    /// them as light sources rather than bright stickers.</summary>
    public float bloomIntensity = 0.72f; // construction re-theme: the concept's worklights GLOW
    /// <summary>Unchanged at 1.0, and it got easier: at night nothing NON-emissive comes close to 1.0
    /// (the brightest albedo in the scene is concreteFloor under a 0.55 moon), so this threshold now
    /// cleanly separates "is a light" from "is lit by one" without any tuning. Everything above it is
    /// deliberate — the emissive ladder (windows 1.5 / billboards 2.0 / interactables 2.6) and the ledge
    /// trims at 1.6 — which is exactly the set of things that should bloom.</summary>
    public float bloomThreshold = 1.0f;
    /// <summary>0.18 -> 0.12. A vignette darkens the frame's edges, which is a cheap focus trick against a
    /// bright scene and a tax against a dark one — at night it is subtracting from frames that have little
    /// to give, and it lands hardest on the street-death camera (already the darkest shot in the game).
    /// Kept nonzero because it still pulls the eye to centre-frame; just no longer free.</summary>
    public float vignetteIntensity = 0.12f;
    /// <summary>8 -> 4. Contrast pivots around mid-grey: it pushes values above the pivot up and values
    /// BELOW it down. A sunset frame lived mostly above the pivot, so +8 was almost pure punch. A night
    /// frame lives almost entirely below it, so the identical +8 becomes a crush — it would dig the whole
    /// scene toward black and take the street with it, undoing the sidewalk's x1.35 lift in the grade after
    /// the lighting had already paid for it. 4 keeps some snap without fighting the theme.</summary>
    public float postContrast = 4f;
    /// <summary>Unchanged at -5. Left alone deliberately: pulling saturation further for "night looks
    /// desaturated" realism would drain the warm city lights, and those ARE the theme (see the class
    /// remarks — cool ambient, warm points of interest). The cool/warm split is doing the mood work; this
    /// does not need to help.</summary>
    public float postSaturation = -5f;
    /// <summary>#FFF2E4 (warm) -> #E6ECFA, a gentle cool. This multiplies EVERYTHING, which is why it is
    /// only barely tinted rather than pushed to a strong night-blue: a heavy cool filter would drain the
    /// warm windows and billboards, i.e. the exact accents the whole re-theme exists to let breathe. It
    /// only needs to stop actively fighting the palette (a warm filter over a moonlit scene reads as
    /// colour-correction error), and the moon and ambients are already doing the real cooling.</summary>
    public Color colorFilter = new Color32(0xE6, 0xEC, 0xFA, 0xFF);
    /// <summary>Kept low — just enough to soften whip-pans and mantle camera snaps,
    /// not a strong cinematic blur that would fight the "feel fast" movement-first goal.</summary>
    [Range(0f, 1f)] public float motionBlurIntensity = 0.12f;
}
