# Visual Pass — "Golden Hour over the Construction Site"

**Date:** 2026-07-12
**Status:** Approved by user (mood boards + blend mockup: artifact `050ad155`)
**Scope tier:** Stylized greybox+ — geometry stays code-generated boxes; visuals come from palette, lighting, fog, emissives, trims, procedural props, and post-processing. No modeled art, no textures beyond simple procedural ones, no character meshes.

## Art direction (approved)

Permanent golden hour over an industrial rooftop city. Light from the "Golden Hour" board, matter from the "Overcast Industrial" board:

- Dusk gradient sky (zenith `#3B2E5E` → horizon `#F0904A` → `#FFC873`), low warm sun.
- Building bodies: desaturated concrete tinted by the dusk shadow (`#5C545E` family, slight per-building variation).
- Every roof edge and sun-facing face catches a warm lit rim (`#FFB668`).
- Warm haze (`#D9906A`) pools below roof level; falling to street level means dropping into the fog.
- Cranes, antennas, far skyline read as silhouettes against the sky.

**Gameplay color language (strict):**

| Color | Meaning |
| --- | --- |
| Safety orange `#F07020`, emissive | Interactable: ladders, chains, climbable faces, scaffolds |
| Red `#FF3D2E`, emissive + bloom | Tagger (visible in silhouette at range) |
| Pale warm `#FFE9C4` | Runner |
| Pulsing emissive | Conversion grace state |

Orange and red are reserved exclusively for these meanings — no decorative use.

## Constraints (from project architecture)

1. **Scenes are generated from code.** All three scenes (MovementPlayground, TagArena, RooftopArena) are built by `PlaygroundBuilder` menu items calling `TagArenaMapGeometry` / `RooftopArena`. The visual system must live in that pipeline; hand-dressing scenes is forbidden (it would be wiped on rebuild).
2. **Presentation only.** Simulation (movement, tagging, bots, self-play harness) must be provably unaffected. Headless self-play must keep working; visual components must not run in it or must be inert there.
3. **Config convention.** All tuning values in ScriptableObjects. Note: this project instantiates configs via `ScriptableObject.CreateInstance` at build/bootstrap time rather than loading `.asset` files (see PlaygroundBuilder remarks on the deserialization bug). `VisualThemeConfig` defaults ARE the theme.
4. **Bot navigation is sacred.** Parkour-graph anchors (roof centers / `Walk` points) and link lines between them must keep clearance. Props may not block them.
5. **No new asmdef dependencies from simulation → presentation.** Visual code lives in `Game.MapGeometry` beside the geometry it styles (a `SceneStyler` static class + `VisualThemeConfig`). The headless self-play harness never calls `SceneStyler`; only the editor builders and scene bootstraps do. No simulation assembly gains a new reference.

## Components

### 1. `VisualThemeConfig` (ScriptableObject)

Single source of truth for every visual value: sky colors, sun elevation/azimuth/color/intensity, ambient tint, fog color/density/haze-plane heights, concrete palette (base + variation range, per-zone tint), rim-trim color/thickness, interactable orange + emissive intensity, tagger/runner/grace colors + emissive intensities, bloom threshold/intensity, color-grading warmth, vignette strength. Defaults = the approved swatches above.

### 2. `SceneStyler` (static, in `Game.MapGeometry`)

Called by the scene builders after geometry creation. Responsibilities:

- **Skybox:** procedural gradient dusk skybox material (simple custom gradient shader or tweaked built-in procedural skybox), assigned via `RenderSettings.skybox`; ambient from gradient.
- **Sun:** replaces `CreateLight()`'s neutral light — low elevation (~12–15°), warm color, shadows on.
- **Fog:** `RenderSettings.fog` (exponential) in haze color; 2–3 large translucent haze quads layered below roof level. True height fog is out of scope (phase 2).
- **Materials:** semantic material set (URP Lit) replacing flat colors: `ConcreteBody` (with per-building hue jitter, seeded), `RoofSurface`, `RimTrim` (emissive warm), `Interactable` (emissive orange), plus agent materials. Simple procedural noise texture on concrete is optional stretch, flat tinted is acceptable.
- **Rim trims:** thin (~0.15 m) boxes auto-generated along the top perimeter of every roof/platform box — reads as sunset rim light AND outlines every ledge/gap/landing at speed. Generated from the same layout data as the geometry (no scene scraping).

### 3. Prop dressing (deterministic, nav-safe)

- Rooftop props: AC units, vents, pipes, antennas. Placement seeded per roof (hash of roof name) so rebuilds are stable.
- **Clearance rule:** no prop within a configurable radius of any parkour-graph anchor, nor within a corridor around any link line (jump/ramp/ladder paths), nor within margin of roof edges used for jumps. Props DO get colliders (they're vault-sized or wall-like) but only in positions the clearance rule proves safe.
- Silhouette dressing outside playable bounds: 1–2 cranes, far skyline boxes, no colliders.
- Zone flavor: TagArena's wall-run alley + ledge row lean construction-site (scaffold verticals in safety orange on non-runnable faces); RooftopArena leans rooftop clutter.

### 4. Agent visuals

- Extend `TagRulesConfig`/`TagAgent.UpdateColor()` from base-color-only to base + emission (tagger red emissive, runner pale, grace pulse via emission intensity oscillation). `TagAgent` already owns a per-agent material instance — reuse it.
- MovementPlayground's player capsule gets the runner material.

### 5. Post-processing

Global URP Volume created by the builders: bloom (picks up tagger red + interactable orange + rim trims), warm color grading (lift shadows toward purple-grey, warm highlights), subtle vignette. Values from `VisualThemeConfig`.

## Verification (agentic loops, per work package)

1. **Compile/console loop:** zero errors/warnings after every change (existing Unity CLI tooling).
2. **Screenshot loop:** enter play mode in each scene, capture editor screenshots, compare against the approved mockup for palette/mood; human eyeballs at package boundaries.
3. **Simulation regression:** run the movement-metrics play-mode suite + a self-play batch after the prop package and at the end. Gate: stuck-detection counts, parkour-edge usage, and win rates unchanged within noise vs. a pre-change baseline batch. Any regression → the offending prop/trim placement is fixed or reverted.
4. **Headless safety:** self-play harness runs with styling either skipped or inert; no `Shader.Find` failures or material leaks in batch mode.

## Work packages (for executor delegation)

| WP | Content | Risk | Suggested tier |
| --- | --- | --- | --- |
| 1 | `VisualThemeConfig` + material set + sun/ambient + fog + post volume, wired into all three builders | Medium (URP plumbing) | Opus |
| 2 | Rim trims + interactable/agent emissive language | Low (data already exists) | Sonnet |
| 3 | Skybox gradient shader + haze planes | Medium (shader) | Opus |
| 4 | Props + silhouettes with clearance rule + self-play regression gate | High (nav safety) | Opus |
| 5 | Screenshot/tuning loop: adjust theme values against mockup, final regression batch | Low, iterative | Sonnet |

WP1 → WP2/WP3 (parallel) → WP4 → WP5.

## Out of scope

Height-fog shader, texture authoring, character meshes/animation, HUD restyle (beyond role colors already present), main-menu art, LOD/performance work beyond keeping the prototype smooth.
