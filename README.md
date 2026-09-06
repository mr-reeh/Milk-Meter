# Milk Meter

A Dalamud plugin that drives your character's chest size (via [Customize+](https://github.com/Aether-Tools/CustomizePlus))
from something happening in-game, displayed as an animated baby-bottle gauge on your HUD -
plus a second, independent waist/hunger meter tied to your food buff. Pick whichever of
three breast-scaling modes fits how you want it to play:

## The three modes

- **Food mode** — size tracks your Well Fed food buff. Full while it's fresh, tapering down
  as it runs low, back to your normal size once it expires.
- **Mana mode** — size tracks your current MP. Bigger with more mana, smaller as you spend
  it (or the other way around, if you flip it).
- **Job mode** — a little mini-game. Size slowly and passively grows on its own the longer
  you play, and it's up to you to fight it back down: using your job's tracked actions
  (Provoke and Reprisal for tanks, Second Wind for melee/physical ranged, Lucid Dreaming
  for healers/casters, and so on), landing GCDs, jumping, and taking damage in combat can
  all be configured to shrink it back down. Everything is tunable in the settings window -
  how fast it grows, how much each action shrinks (or even grows) it, and how big or small
  it's allowed to get.

Switch between modes anytime with `/milkmeter mode food`, `/milkmeter mode mana`, or
`/milkmeter mode job` - or just open the settings window and pick from there.

## The HUD gauge

A draggable, resizable baby-bottle bar shows your current size at a glance, filling from
empty to full as it changes color across configurable zones.

- **Left-click** pauses/unpauses scaling on the spot - handy for cutscenes, screenshots, or
  just wanting a break. Everything freezes exactly where it was; nothing snaps back to
  normal.
- **Right-click** instantly resets size to normal (1.0), without pausing anything -
  growth/decay just continues from there on the next tick.

It can also be set to fade out when idle and reappear on hover, or to hide entirely out of
combat, so it stays out of the way when you don't need it.

## The waist/hunger meter

A second, entirely independent meter, opened in its own settings window via `/hungermeter`
(or the short form, `/food`) - separate from the breast-scale settings above, with its own
pause toggle.

- Waist size starts at a configurable **Baseline** (default `1.0`).
- Every real-world hour that passes, it decays toward a configurable **Minimum** (default
  `0.8`) - applied continuously, not in once-an-hour jumps.
- Every time you eat a food item (whether that's your first bite or refreshing an
  already-active buff), it jumps up toward a configurable **Maximum** (default `1.2`).
- All five values - Minimum, Baseline, Maximum, Increase Per Food, Reduction Per Hour - are
  sliders in that window.

Your progress persists across game restarts: closing the game for a while and reopening it
applies that elapsed time's decay retroactively, rather than resetting to Baseline.

This used to be a separate plugin (Hunger Meter). It's now merged directly into Milk Meter,
because the two plugins independently pushing to Customize+ at the same time caused them to
intermittently erase each other's scaling - one combined plugin avoids that entirely.

## The server info bar

A single combined readout on the game's server info bar (the row next to the clock, shared
with FPS counters/gil trackers/etc.) shows both meters at a glance: `Food: X% | Milk: Y%`.
Each meter maps its own Minimum/Baseline-or-Out-of-Combat-Maximum/Maximum range onto
0%-200% independently - Milk's can climb past 100% while actually in combat. Toggle the
whole entry off in the main settings window if you'd rather not see it there.

## Getting started

**Requires [Customize+](https://github.com/Aether-Tools/CustomizePlus)** to already be
installed - this plugin doesn't touch your character model directly, it applies a
temporary scale on top of your existing Customize+ profile (nothing else you've scaled is
lost, and nothing is changed permanently).

**Everything is local** - nobody else sees your chest or waist size change. This is purely
a visual/gameplay effect on your own client.

### Installing

1. In-game, open the plugin installer, go to **Settings → Experimental → Custom Plugin
   Repositories**.
2. Add this URL: `https://raw.githubusercontent.com/mr-reeh/milk-meter/main/repo.json`
3. Save, then search for **Milk Meter** in the plugin installer and install it.

### Commands

- `/milkmeter` (or the short form, `/milk`) — opens the breast-scale settings window.
- `/milkmeter mode food` / `mode mana` / `mode job` — switch modes without opening
  settings.
- `/milk moan` — plays a sound and ramps breast scale up to Maximum Scaling (In Combat)
  over 20 seconds.
- `/milk <number>` (e.g. `/milk 1.3`) — sets breast scale directly, clamped between Minimum
  and Maximum Scaling (In Combat).
- `/hungermeter` (or the short form, `/food`) — opens the waist/hunger settings window.

Everything else - fine-tuning growth rates, per-action multipliers, HUD appearance,
colors, sounds, and more - lives in the settings windows themselves.

## 🤖 AI Assistance & Attribution
This project is AI-assisted. 
* **Core Coding & Architecture:** Assisted by [Anthropic's Claude](https://claude.ai) 
