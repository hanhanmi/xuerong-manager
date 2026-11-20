# Bot AI Feature Reference — v1.4.2

Bundled bot stack: Smarter-Bot 1.13.29. The upstream changelog notes the package was file-verified but not play-tested, so treat the in-game feel as something to confirm yourself.

---

## 1. CT site holding

- Bots take up assigned positions without being pulled away by the old pathfinder. Combat, trade kills and retakes can still interrupt holding.
- Each round randomly picks between a full-site hold, partial patrol inside the site, or rotating corners. A and B are planned separately.
- Patrol and rotation keep at least one bot watching an angle; a lone holder relies on short local peeks.
- Movement inside the site checks obstacles and drops.
- From 1.13.29: peeking is side-to-side first — both sides and short lateral steps, with diagonal only as a low-probability fallback. Front-back peeks were removed.
- From 1.13.29: pull-back is judged by actual distance and progress. If a bot cannot move for a while it tries to unstuck and swap angle within its area. If a route is blocked but the target area was reached, control returns to the holding logic and the bot keeps peeking.

## 2. T post-plant defence

- Bots split by actual C4 position: someone watches the bomb, others cover entrances. Standing stance with short peeks and pull-backs.
- When defuse sounds are heard nearby, bots prefer an angle with sight of the C4.
- Bots far from the site try to regroup, pausing during firefights and retrying when stuck.
- One or two survivors hold together; with more bots there is a chance one or two check nearby entrances. Peripheral checks are cancelled when the hold is short-handed or defusing starts.
- If the bomb is about to explode and nobody is defusing, control returns to the vanilla retreat logic.
- From 1.13.29: post-plant peeking also uses side-to-side as the default.

## 3. Sound awareness (new in 1.13.29)

- Bots hear footsteps, gunfire and landing sounds, and report approximate directions to teammates.
- Memory decays over time, up to about four seconds, and is used for pre-aiming while holding or guarding the bomb.

## 4. Gunfights and movement

- Brief continuation of the original direction when an enemy first appears, then lateral movement — reduces the stop caused by instant slowdown. From a standstill, seeing an enemy starts movement first.
- Rifle wide peeks and crouch peeks. Pistols and SMGs may be used for diagonal running fire depending on distance and the situation.
- Reloading may back off into cover or attempt evasion.
- Crouch peek: build speed standing up first, then crouch once there is real lateral movement; keep moving in the same direction, switch sides while crouched only when there is time and the other route is safe.
- After a kill, the previous strafe / crouch / fire restrictions are cleared so the next target can be engaged immediately.

## 5. Support, retake and dropped bomb

- Bots cover for a human teammate who dies, and trade kills.
- From 1.13.29: distinguishes actually arriving from still en route. If nobody is suitable at the moment, the decision is re-evaluated shortly. Still accounts for another site being held, confirmed push direction and C4 intel.
- After plant, defuse duty and support are assigned by actual C4 position, distance, defuse kit and time left. Support goes to surrounding areas rather than crowding the bomb.
- Dropped-bomb recovery, fast plant, split push for the bomb, and mid-round decisions by economy.

## 6. Flash handling

- Real flash duration from the game interrupts firing temporarily; short side flashes get a reaction too.
- Before turning away from a flash, bots remember their facing and turn back at a unified speed for all three difficulty tiers. Multiple flashes are handled separately; a short flash does not cut short a longer one still in effect.
- From 1.13.29: flash avoidance prefers standing and keeps moving — no forced crouch or hard stop.

## 7. Weapon and grenade decisions

- Weapon and grenade picks by round, team economy and armour.
- Downgrade when the budget is tight, give weapons to human players, and swap weapons at round start.
- Bundled updates: BotBuy 1.1.7, BotAimImprover 2.1.9, NadeSystem 1.2.4.

## 8. Aim feel (VPK)

The main and Medium profiles retune seven parameters — six view-angle values and reaction time. The view-angle values are lowered across more than 1300 profiles for smoother tracking and less mechanical snapping:

- `LookAngleStiffnessNormal` / `Attacking` — lowered
- `LookAngleDampingNormal` / `Attacking` — lowered
- `LookAngleMaxAccelNormal` / `Attacking` — lowered
- `ReactionTime` — now 0.05 / 0.06 / 0.15 s instead of near-instant

These were originally a separate community-tuned profile; 1.13.29 folded them in and lowered the values further.

## 9. Carried over from earlier releases

- Three difficulty tiers: easy / normal / hard.
- First-shot reaction delay by distance, turn amount, pre-aim, smoke and recent damage; pre-aiming the same enemy is handled separately from spotting a new one.
- Smoke: wait, route around, hide, or fire through; occasional blind bursts toward damage direction or last known contact. Teammates receive only rough positions.
- T side: default map control, split, group up, slow play, stack into a site, fake, contact play; roles for flank and rear guard.
- CT side: rotate, support, fill empty sites, group or spread; economy-based gamble stacks.
- Stuck detection based on intended versus actual movement, then route recalculation; a crouch-jump is used only when the path ahead and the landing are clear.

---

## Console commands

```
css_smartbot_status        version, A/B peek and pull-back counts, unstuck, sound and support stats
                           T side adds a T-postplant line: regrouping, site hold, peripheral checks, peek distance
css_smartbot_debug         toggle tactical logging
css_botstate_flashdebug 1  enable flash logs (0 to disable)
css_bot_fov status         field-of-view state
```

---

## Packaged state

| Item | Value |
| --- | --- |
| Upstream version | Smarter-Bot 1.13.29 |
| BotState.dll | 294400 bytes, `f27016e3` |
| BotAimImprover / BotBuy / NadeSystem | 1.13.22, unchanged in v3 |
| botprofile.vpk | 1711 profiles per file; main and Medium use 1.13.29 aim tuning, High and Low keep 1.13.22 |
| Bot names per VPK | 971 Chinese, 28 KOOK |
| bot_info.json | 4861 entries, 4786 Chinese |
| RoundDamageRecap | local maintained build, not overwritten by upstream |
| Not included | Panel.exe, source and backup trees, upstream notes |
