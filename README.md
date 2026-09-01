# Milk Meter

A Dalamud plugin that drives your character's chest size (via [Customize+](https://github.com/Aether-Tools/CustomizePlus))
from something happening in-game, displayed as an animated baby-bottle gauge on your HUD.
Pick whichever of three modes fits how you want it to play:

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
empty to full as it changes color across configurable zones. Click it to pause/unpause
scaling on the spot (this always resets size to normal) - handy for cutscenes,
screenshots, or just wanting a break. It can also be set to fade out when idle and
reappear on hover, or to hide entirely out of combat, so it stays out of the way when you
don't need it.

## Getting started

**Requires [Customize+](https://github.com/Aether-Tools/CustomizePlus)** to already be
installed - this plugin doesn't touch your character model directly, it applies a
temporary scale on top of your existing Customize+ profile (nothing else you've scaled is
lost, and nothing is changed permanently).

**Everything is local** - nobody else sees your chest size change. This is purely a
visual/gameplay effect on your own client.

### Installing

1. In-game, open the plugin installer, go to **Settings → Experimental → Custom Plugin
   Repositories**.
2. Add this URL: `https://raw.githubusercontent.com/mr-reeh/milk-meter/main/repo.json`
3. Save, then search for **Milk Meter** in the plugin installer and install it.

### Commands

- `/milkmeter` (or the short form, `/milk`) — opens the settings window.
- `/milkmeter mode food` / `mode mana` / `mode job` — switch modes without opening
  settings.

Everything else - fine-tuning growth rates, per-action multipliers, HUD appearance,
colors, sounds, and more - lives in the settings window itself.
