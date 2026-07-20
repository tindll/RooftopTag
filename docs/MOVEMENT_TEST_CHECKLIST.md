# Movement Feel Checklist

How to use: load an empty playground scene — no enemies, no objectives, no
tag rules, just geometry to move through. Run through each section below as
its own pass, doing the described action several times in a row. Check the
box if it feels right; if not, write a one-line note next to it (what you
did, what it felt like) instead of unchecking silently. This is a feel test,
not a bug hunt — "technically works" that still feels bad is a FAIL.

## 1. Responsiveness

- [ ] Tap a direction key from standstill — character starts moving within a frame or two, no wind-up.
- [ ] Press Shift to sprint mid-run — speed increases immediately, not on a delay.
- [ ] Tap Space — jump fires on the same frame as landing, not a beat later.
- [ ] Change direction while running — character reorients without a canned turn animation locking input.
- [ ] Mash Ctrl repeatedly while running — every press slides or crouches, none get eaten.
- [ ] Press E at a ledge/wall/rope — the context action triggers without a noticeable pause to "decide."
- [ ] Release all keys mid-sprint — character decelerates naturally, doesn't snap to idle.

## 2. Flow & transitions

- [ ] Run into a slide (Ctrl) then hop out of it (slide-hop) — one continuous motion, no stutter-step.
- [ ] Vault a low obstacle while sprinting — you land already running, no re-acceleration from zero.
- [ ] Grab a chain-swing, release at the top, land, and keep running — no dead stop on landing.
- [ ] Wall-hook off a wall mid-air, then jump-launch — launch direction/speed feels like a continuation, not a reset.
- [ ] Climb a ladder/pipe to the top and immediately run off the top — no frozen "climb-exit" beat.
- [ ] Chain vault -> slide -> jump back-to-back — no forced idle frame between any two actions.
- [ ] Mantle onto a ledge and immediately sprint — no awkward stand-up-then-wait delay.

## 3. Momentum

- [ ] Sprint in a straight line for several seconds — speed visibly builds, doesn't just snap to max.
- [ ] Slide down a slope — speed increases the longer the slope, doesn't cap out like flat-ground slide.
- [ ] Slide on flat ground — speed bleeds off gradually, doesn't stop dead.
- [ ] Turn sharply while sprinting — you keep most of your speed and carry it into the new direction.
- [ ] Jump while sprinting then hold a direction — trajectory reflects run speed, doesn't float like a standing jump.
- [ ] Commit-dive/lunge from a run — the dive carries forward momentum from the run, not launched from a standstill feel.
- [ ] Bunny-hop by jumping right as you land — speed stacks slightly instead of resetting every hop.
- [ ] Come to a stop and re-accelerate — no "sticky" first-step drag before you're back up to speed.

## 4. Forgiveness

- [ ] Walk off a ledge and press jump a few frames late — coyote time still lets you jump.
- [ ] Press jump slightly before landing — jump buffers and fires right on landing, not ignored.
- [ ] Approach a ledge at a slight angle or off-center — vault/mantle still triggers, doesn't require pixel-perfect alignment.
- [ ] Attempt a slide-hop with imperfect timing (a few frames early/late) — it still cancels into the hop.
- [ ] Reach for a wall-hook slightly off-angle mid-air — grab still connects.
- [ ] Go for a double-jump (as a runner) with a slightly late second press — it still registers.
- [ ] Grab a chain-swing while approaching from an imperfect angle — grab still connects, doesn't whiff.

## 5. Readability

- [ ] Run at a knee-high ledge repeatedly — it's always a vault, never randomly a mantle or climb.
- [ ] Run at a chest-high ledge repeatedly — it's always a mantle/climb, never a vault.
- [ ] Approach the same slope at the same speed multiple times — behavior (run-up vs slide vs block) is consistent every time.
- [ ] Run off the same edge repeatedly — same result every time (drop, vault, etc.), no random variation.
- [ ] Explicit E-press vault vs automatic running vault — both produce the same recognizable animation/outcome.
- [ ] Approach a wall at the same speed/angle repeatedly — wall-hook grab triggers (or doesn't) consistently.

## 6. Physical feedback

- [ ] Sprint to top speed — FOV visibly widens as speed increases, narrows back down when slowing.
- [ ] Fall from a large height and land — camera shake kicks in, scaled to fall height (small drop = little/no shake).
- [ ] Drop from a small height and land — no exaggerated shake for a trivial landing.
- [ ] Slide (Ctrl) — camera dips/FOV kicks noticeably at the start of the slide.
- [ ] Hard-land from a big fall — a roll or heavier landing animation plays, doesn't just stick the landing silently.
- [ ] Chain several feedback triggers back to back (land, slide, land) — effects don't stack into a jittery mess.

## 7. Skill expression

- [ ] Run a route cleanly (vault/slide/swing chained with good timing) vs sloppily (stopping between each) — the clean run is noticeably faster.
- [ ] Chain vault -> slide -> swing without stopping — speed carries through all three, doesn't reset at each transition.
- [ ] Time a slide-hop well vs poorly — good timing gives a clear speed/height bonus over a mistimed one.
- [ ] Mistime a jump or grab — you can still recover (double-jump, wall-hook, scramble) instead of being punished with a hard stop.
- [ ] Use bunny-hopping deliberately on a straightaway — an attentive player is visibly faster than one who just holds sprint.
- [ ] Take a "sloppy" alternate path around an obstacle instead of vaulting it — it costs visible time/speed vs the skilled route.

## Feel regression watch

Any of these showing up is a sign tuning has regressed, even if nothing is
technically "broken":

- [ ] Vault/mantle/climb feels like hitting molasses — a beat of dead time before or after the action.
- [ ] Sliding on flat ground triggers off of a plain walk, not a deliberate Ctrl press.
- [ ] Turning around while sprinting briefly stops or stutters you instead of just redirecting momentum.
- [ ] Camera jitters or judders during normal turning/orbiting, not just during shake events.
- [ ] Can't maintain speed running up a ramp that should be climbable at a run.
