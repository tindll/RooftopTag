# Tripo generation prompts — RooftopTag

Settings: **Smart Mesh**, **triangle** topology. (HD/H3.1 only for a hero prop you get close to.)

Art direction derived from existing assets:
- Cast: raccoon (runner) vs pest control (tagger), trash bins, eat zones → grubby backstreet, low-rise, not skyscrapers.
- Style DNA from `pest_control.fbx` texture: soft hand-painted semi-realistic, muted earthy palette, soft shading, no cel outlines.
- Scene is **moonlit night** (`VisualThemeConfig.cs`) → generate with NEUTRAL lighting, let URP light it.
- Windows glow via an emissive procedural atlas (`#FFCC7A`, 45% lit) → generate windows DARK.
- `#F07020` safety orange is reserved for interactables → **no orange on buildings**.

---

## Style block (append to every prompt)

```
Stylized semi-realistic game asset, softly hand-painted PBR texture, muted desaturated
earthy palette: weathered concrete grey, dull red-brown brick, olive green, khaki, rust,
oxidised copper. Slightly chunky exaggerated proportions, clean readable silhouette,
no tiny fiddly detail. Flat wall planes, crisp straight edges, sharp 90 degree corners.
Evenly lit neutral studio lighting, flat ambient, no baked shadows, no cast shadows,
no strong highlights, no night lighting, no coloured lighting.
```

---

## 1. Brick tenement walk-up (the workhorse)

```
A single free-standing city apartment walk-up building, narrow with a square footprint,
with a completely flat empty roof. Old red-brown brick facade, pale stone lintels above
plain rectangular windows, a black iron zig-zag fire escape with ladders running down one
face, a low brick parapet wall around the roof edge, a small brick chimney stack. Windows
are dark flat glass panels, unlit, no interior visible, no curtains. Weathered, grubby,
soot-stained, but structurally straight and true.
+ STYLE BLOCK
```

## 2. Industrial brick warehouse / factory (construction zone)

```
A single free-standing old industrial brick factory warehouse building, square footprint,
flat roof. Dark red brick with tall arched multi-pane industrial windows, a row of rusted
corrugated metal sawtooth skylights on the roof, riveted steel roof vents, a tall square
brick smokestack on one corner, a rusted steel gantry bracket on one wall. Windows are
dark flat grimy glass, unlit, no interior visible. Rust streaks, peeling paint, weathered
brick.
+ STYLE BLOCK
```

## 3. Concrete commercial mid-rise

```
A single free-standing mid-century concrete commercial city building, square footprint,
completely flat empty roof. Plain grey precast concrete panel facade with a regular grid
of rectangular windows, a slim concrete ledge band between each floor, a low concrete
parapet around the roof, boxy metal HVAC air conditioning units clustered on the roof,
a thin metal antenna mast. Windows are dark flat glass, unlit, no interior visible.
Water-stained concrete, patched, utilitarian.
+ STYLE BLOCK
```

## 4. Under-construction concrete shell (construction zone)

```
A single free-standing half-built concrete building shell under construction, square
footprint, flat open top floor. Bare grey poured concrete floor slabs and square columns,
no exterior walls on the upper floors, exposed rusty rebar rods sticking up from the top
slab, tied metal scaffolding poles along two faces, wooden plank walkways, a green safety
mesh net partly covering one side, stacked concrete blocks. Raw, unfinished, dusty.
+ STYLE BLOCK
```

## 5. Rooftop access hut / bulkhead (prop scale — high value)

```
A small flat-roofed rooftop stairwell access hut, a simple brick and concrete box
structure with a single weathered grey steel door, a small metal roof vent, a rusted
conduit pipe running up one side, a low concrete lip around its flat roof. Standalone
object, isolated, no ground, no surrounding building.
+ STYLE BLOCK
```

## 6. Water tower (candidate for HD)

```
An old cylindrical wooden water tower standing on a tall riveted steel lattice leg frame,
with a conical wooden lid roof, iron hoop bands around the barrel, a thin service ladder
up one leg, a rusted outlet pipe. Weathered silver-grey timber, rusted iron fittings.
Standalone object, isolated, no ground, no surrounding building.
+ STYLE BLOCK
```

---

## Import notes (measured from the project)

| Fact | Value | Source |
|---|---|---|
| Character capsule | 1.8m tall, 0.4m radius | `MovementConfig.cs:226` |
| Standard roof footprint | 8×8m (max 12×12m) | `RooftopArena.cs:64-108` |
| Roof-to-street drop | ~26–34m | `VisualThemeConfig.cs:368` |
| Roof rim trim | 0.12m tall, visual only, no collider | `TagArenaMapGeometry.cs:610` |
| AC unit prop | 1.2 × 0.9 × 0.9m | `RoofPropDresser.cs:92` |

1. **Tripo has no scale.** Import, then scale so a door reads ~2.1m against the 1.8m capsule. Check against the capsule, never by eye.
2. **Never take Tripo's collider.** Mantle (0.5–2.2m band) and vault (≤1.1m) depend on ledges being exactly where the code puts them. Import as visual shell only, keep the procedural colliders.
3. **Buildings are ~8m wide × ~30m tall (1:4).** Tripo will not give you that slenderness — it'll generate normal proportions, and stretching 4x will smear the texture. Prefer generating a **building cap** (top few floors + roof) and letting the existing procedural facade + window atlas tile the tall lower mass. The cap is the part the camera actually sees.
