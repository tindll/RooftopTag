# RooftopTag

Third-person tag across the rooftops of a night-time construction-site city. You play a raccoon
runner being hunted by net-swinging pest-control taggers: sprint, climb, dive-roll, ride crane
swings and kick over trash cans to break line of sight, while bot runners and taggers play the
same game around you. Get caught and a kill-cam replays the moment; outlast the round to win.
The arena, the backdrop city and all dressing are 100% procedurally generated — the scene file
is a build artifact, not hand-authored content.

## Opening the project

- Unity **6000.5.3f1** (URP). Open the repo root as the project.
- The playable scene is `Assets/Scenes/RooftopArena.unity`. Never edit it by hand — regenerate it
  with the menu item **RooftopTag → Build Rooftop Arena** (editor must be idle, not in Play mode).

## Building a player

**RooftopTag → Build → Windows x64 (Development)** or **(Release)**. Output lands in `Build/`.

## Running the tests

PlayMode tests live in `Assets/Tests/PlayMode` (movement metrics, tag rules, parkour graph,
prop clearance, kill-cam, bot self-play). Run them from Unity's Test Runner, or headless:

```bash
Tools/selfplay.sh   # bot-only self-play batch; prints METRIC lines, writes Tools/selfplay-results.xml
```

## Asset credits

Third-party art and audio keep their licenses next to the assets:
`Assets/Art/Kenney/*/License.txt` (Kenney city/vehicle/prop kits, CC0),
`Assets/Art/Quaternius/Clouds/License.txt`, `Assets/Art/Construction/*/License.txt`,
and `Assets/Audio/ATTRIBUTION.md` for the ambience track. Character and building models are
project-generated. See `docs/` for design notes and specs.
