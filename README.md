# Milk Meter

A fork-concept of [ManaMune](https://github.com/Noffletoff/mana-mune): same idea (drive a
Customize+ chest bone scale from something happening in-game), now switchable between three
triggers instead of picking one:

- **Food mode** (default): scale tracks your Well Fed (food) buff timer.
- **Mana mode** (original ManaMune behavior): scale tracks current MP.
- **Job mode**: same mechanic for every tracked job. Provoke for tanks (Warrior additionally
  tracks Equilibrium, either one counts); melee/physical ranged DPS track Second Wind;
  healers/casters track Lucid Dreaming - any one of a job's tracked abilities firing counts. Using
  one **subtracts** a "Base Reduction Per Action" from your current scale, multiplied by that
  specific ability's own fully user-configurable multiplier (-5 to 5; defaults match each ability's
  real cooldown relative to Provoke's 30s baseline - adjust any of them freely in the settings
  window). A negative multiplier flips that ability's effect into an **increase** instead, capped
  at Maximum Scaling (In Combat) ALWAYS - per request, this doesn't switch down to Maximum Scaling
  (Out of Combat) out of combat the way the damage-taken/jump increase variants still do, so a
  negative-multiplier ability can push scale past Baseline even out of combat. Otherwise,
  stacking if you use it repeatedly, so overusing your job action can shrink size all the way down
  to a universal Minimum Scaling (0.75 by default, the same regardless of combat state). Simply
  existing over time continuously **grows** that value back up via Passive Scale Gen (`PassiveScaleGenPerSecond`,
  a direct per-second rate, slider range -0.10 to 0.10 - 0.05 means +0.05 scale/second - replacing
  an earlier "minutes to grow a
  full unit" parameterization; the default preserves the exact same effective rate the old default
  had. NOTE: negative values here currently have no distinct effect - `ApplyGrowth`'s own
  `<= 0f` check treats any negative value the same as exactly 0 (no growth), and since this same
  value also feeds the `/cackle` drain rate (`drainPerSecond = PassiveScaleGenPerSecond *
  CackleDrainRateMultiplier`), a negative value makes that drain go inert too via the same
  `ApplyDrain` check - so the entire negative half of this slider is currently a dead zone,
  identical to 0, unlike `ExtraScaleGenPerSecond` below, which does have a distinct negative
  behavior): toward a higher
  Maximum Scaling (In Combat) while fighting (1.25 by default), or only back up to Maximum Scaling
  (Out of Combat) once out of combat (1.0 by default) - but only from BELOW that ceiling.
  Entering/leaving combat while already above the new ceiling does NOT pull you back down to it -
  size stays exactly where it was until you actually use the ability again. A second, independent
  rate - `ExtraScaleGenPerSecond` ("Extra Scale Gen (Per Second)" in settings, -0.10 to 0.10, off/0
  by default) - standalone and NOT multiplicative of any other factor, unlike Passive Scale Gen
  (which gets scaled by the cackle/shakedrink rate multipliers depending on state) -
  applies on top of Passive Scale Gen, but IGNORES the in/out of combat ceiling switch entirely: a
  POSITIVE value grows toward Maximum Scaling (In Combat) regardless of actual combat state, the
  same as always; a NEGATIVE value instead drains toward Minimum Scaling (Always), same
  combat-independent reasoning in reverse. Gated behind `/cackle`'s own drain NOT being
  active - while that drain is running, neither this nor ordinary Passive Scale Gen generates any
  scaling change at all (Passive Scale Gen is instead entirely repurposed into the drain rate
  itself during that window). Outside of that window, a negative value here still works against
  ordinary passive growth.
  Optionally, dying
  immediately sets scale to Minimum Scaling and **freezes** it there - no Passive Scale Gen at all -
  until you're revived, then it resumes normally (off by default).
  "GCD Affects Scale" is the PRIMARY mechanic for fighting the scale back down as it builds - on
  by default, unlike every other scale-modifying toggle here - subtracting a configurable amount
  per GCD invocation (any spell or weaponskill), clamped between Minimum Scaling and Maximum
  Scaling (In Combat), both in AND out of combat. A negative amount raises scale instead of
  lowering it. Detected via `JobBuffTracker.GetGcdCooldownState`, built on the same
  `ActionManager.Instance()->GetRecastGroupDetail()` mechanism already proven elsewhere in this
  file for per-ability tracking - the recast group number itself (`GcdRecastGroup`, default 57)
  was confirmed empirically via `/milkmeter gcddebug`'s active-group scan
  (`JobBuffTracker.ScanActiveRecastGroups()`) - community documentation had suggested group 58,
  which turned out to be wrong on this client version. Fires on EITHER of two independent signals,
  not just a plain rising edge of the group going on cooldown: also on `Elapsed` decreasing while
  already on cooldown, since pressing the next GCD at the exact instant the previous one ends can
  mean the group's `OnCooldown` state never actually observes a false in between - found this the
  hard way, it was silently missing GCDs pressed immediately as the previous one came off
  cooldown. Also optionally, taking damage
  while in combat (any HP decrease, including DoT ticks; never triggers out of combat) can
  **raise or lower** scale by a
  configurable amount - "Damage Taken Affects Scale" (a positive amount raises scale, a negative
  amount lowers it), on by default (a separate, dedicated "reduces"
  toggle existed here previously but was removed per request, in favor of this single
  bidirectional toggle, with the GCD mechanic above still the primary way to fight the scale down).
  Clamped between Minimum Scaling and whichever Maximum Scaling
  currently applies rather than a fixed value. This trigger is rate-limited by a configurable
  cooldown (0-5 seconds, 0 = no limit), so a fast-ticking DoT can't fire the effect on every
  single tick. Also optionally, "Jumping Affects Scale" (on by default) adds a configurable
  amount per jump (positive raises, negative lowers) - detected as a fresh upward vertical-velocity impulse (`JumpVelocityThreshold`,
  a guessed starting value tunable via `/milkmeter jumpdebug`) while
  `ConditionFlag.Jumping` OR `ConditionFlag.Jumping61` (Dalamud's
  own API lists two separate flags both described as "jumping", so both are checked rather than
  guessing which one the game actually sets), counted once per jump rather than continuously while
  airborne. Always clamped against Maximum Scaling In Combat specifically (not context-dependent the way
  the damage-taken toggle is), and unlike the
  damage-taken toggle, not restricted to combat - rate-limited by its own separate "Jump Trigger
  Cooldown", not shared with the damage-taken cooldown above.

Switch between them at runtime with `/milkmeter mode food`, `mode mana`, or
`mode job` — no reload needed. Run `/milkmeter config`, `/milk`, or click the gear icon in the plugin
installer) to open a settings window with live sliders and status monitoring; `status`
prints the same info to `/xllog` instead.

The plugin scales your chest **relative to your own permanent Customize+ profile**, not to
an absolute value — it reads your current chest scale once (on load, or via
`/milkmeter rebaseline`) and treats the driving number (food/mana/job) as a multiplier on top
of that. If your normal profile already has a bigger chest than the game's raw default,
this plugin's "1.00" means "your normal size," not "the game's default size."

## Scaling curve

| State | Scale (defaults, configurable in the settings window) |
|---|---|
| No food buff | 0.65 (minimum) |
| Buff just consumed, ≥ taper window remaining | 1.00 (maximum) |
| Inside the taper window | linear taper, max → min |
| Buff expired (or manually clicked off) | 0.65 |

Min scale, max scale, and the taper window length (default 30 minutes before expiry) are
all adjustable live in the settings window — changes apply immediately, no reload needed.
The taper only cares about *remaining* time, so it works correctly regardless of the food
item's total duration (30/45/90 minutes, whatever it grants): full size holds for however
long is left above the taper window, then it tapers over that window down to minimum.

Removing the buff early — whether it naturally expires or you right-click it off — looks
identical to the plugin (the status is just no longer present either way), so both are
already handled by the same code path. The plugin also detects that transition and snaps
to the new scale immediately, bypassing the normal update throttle, so there's no
noticeable delay either way.

## How it works

1. Each `Framework.Update` tick, `Plugin.ComputeCurrentScale()` checks `Configuration.Mode`
   and reads from one of two sources:
   - **Food**: `FoodBuffTracker` scans the local player's status list for a status named
     "Well Fed" and returns its remaining seconds; `FoodScale.Compute()` turns that into
     a scale using the configured min/max/taper values.
   - **Mana**: `ManaTracker` reads `CurrentMp / MaxMp`; `ManaScale.Compute()` maps that
     fraction linearly onto 0.60–1.00 (or inverted, if `ManaInverted` is set — matches the
     original's "full at 100%, smaller as you spend — or the other way round").
2. `CustomizePlusIpc` pushes whichever scale wins onto Customize+ as a temporary bone-scale
   override on the chest bones (`j_mune_l` / `j_mune_r`), layered on top of whatever
   profile you already have — same "nothing else you scaled is lost" behavior as the
   original. The scale is applied relative to a captured baseline (see above), not as an
   absolute value.

Switching modes doesn't require the buff/MP state to change first — it forces an
immediate re-push so you see the new mode's scale right away. Same for any settings
window change.

Updates are throttled (max ~4/sec, and only when the scale changes by more than 0.2%)
so it doesn't spam Customize+ every frame — except right when the food buff appears or
disappears, which always pushes immediately regardless of the throttle.

## Files

- `Plugin.cs` — Dalamud entry point, framework tick loop, `/milkmeter` command
  (toggle on/off, `config` to open the settings window, `mode food`/`mode mana`/`mode job` to switch
  source, `status` to print current mode/scale to `/xllog`, `rebaseline` to re-capture
  your chest scale as the new baseline, `dumpprofile` to print your active Customize+
  profile's raw JSON for debugging), plus `/milk` - alone it opens the settings window; `/milk
  minimum` or `/milk maximum` manually ease the job scale toward Minimum Scaling or Maximum
  Scaling (Out of Combat) respectively (reads whichever value those sliders are currently set to,
  and works regardless of the active Scale Source).
- `SettingsWindow.cs` — the in-game ImGui settings/monitoring window.
- `HudGaugeWindow.cs` — the always-on-screen "boob gauge" HUD, drawn as a baby bottle (solid pink
  cap matching the body's width exactly - an earlier version had a subtle divider line and shine
  streaks, removed per request; a
  two-circle orange nipple - small waist bump with a glossy highlight, wider
  flared base - an earlier version also had a feeding-hole dot, also removed per request - clear
  glass body with measurement tick marks) that
  fills vertically from the bottom up as the current applied chest scale rises, with an optional
  bold white outlined label (`HudLabelText`, blank by default) overlaid on the center of the
  bottle body - its font size independently adjustable via `HudLabelFontScale`. In Job mode
  specifically, three gauges are overlaid directly on top of each other, each independently spanning
  the FULL bottle height (like a Kingdom Hearts boss health bar, not bars sharing a boundary
  partway up): `HudFillColorR/G/B` (creamy white by default) maps `HudGauge1StartScale` to
  `HudGauge1EndScale` and stays fully filled once reached; `HudFillColor2R/G/B`
  (warm gold by default) maps `HudGauge2StartScale` to `HudGauge2EndScale`, drawn on top of gauge 1
  and progressively covering it as scaling reaches its own start; `HudFillColor3R/G/B` (deep red by
  default) maps `HudGauge3StartScale` to `HudGauge3EndScale`, drawn on top of BOTH other gauges -
  but UNLIKE gauges 1 and 2, gauge 3 is INVERTED and fills from the TOP of the body downward: empty
  at or above its own End, fully covering both other gauges at or below its own Start, growing as
  scaling DROPS rather than rises. All six boundaries are
  purely visual HUD thresholds, fully independent of each other and deliberately NOT tied to
  Minimum/Maximum
  Scaling (the floor/Out/In of Combat), which still govern the actual growth mechanics elsewhere -
  gauge 1/2 defaults (0.75/1.00/1.00/1.25) match this display's original two-gauge defaults
  exactly, gauge 3 defaults to 1.25/1.50 as a continuation; they can be overlapped, given a gap, or
  run in either direction.
  Food/Mana modes
  always use just the first color across the whole body. Locked in place by
  default; toggle `Configuration.HudLocked` off from the settings window to drag it, then back on
  to lock the new position in - while locked, the bottle is no longer fully click-through the way
  an earlier version was: left-clicking it toggles `Configuration.ScalingPaused`, freezing every
  mechanic that would modify or push chest scale (passive growth, ability-use, damage-taken,
  jumping, `/cackle`/`/hum`, death-reset, the ease-toward-target animation, and the Customize+ push
  itself) while leaving the gauge and its particle effects still rendering (frozen at whatever
  scale was applied at the moment of pausing) - unlike `Configuration.Enabled`, which hides the
  gauge entirely. The threshold effect (vignette, glow, heartbeat sound) is the one exception - it
  completely stops the instant it's paused, per request, rather than continuing to evaluate against
  the frozen scale value; it resumes normally, no special-casing needed, the moment it's unpaused.
  A big, thick red X is drawn diagonally across just the body (previously the whole bottle
  including nipple and cap, which made it look stretched out - shrunk down per request to match the
  body's own, more proportional shape) whenever
  paused, replacing an earlier plain "PAUSED" text label per request - the gauge is also forced to
  stay fully visible the entire time it's paused, bypassing its own idle-fade entirely, so the
  indicator stays discoverable without opening settings even if nothing else is happening. This is
  a real trade-off: the window now intercepts clicks
  meant for the game if anything else happens to sit underneath it, which the old click-through
  behavior never did.
  The whole bottle scales uniformly via `HudScale` rather than
  independent width/height, since a bottle needs specific proportions to read as a bottle. Also
  fires its own local particle burst (`MilkParticleBurst`) around the bottle whenever the job
  scale is genuinely reduced - this used to have a bigger, screen-wide sibling (`MilkBurstOverlay`),
  which was removed entirely per request, leaving this as the only milk-particle effect (drawn at
  full alpha, unaffected by the fade described next, and originates from the bottle's top tip with
  speed/gravity/droplet size all scaled by `HudScale`). The bottle itself
  fades out after `HudFadeIdleSeconds` with no genuine activity
  (`HudFadeOnIdleEnabled`, on by default) - deliberately NOT "the applied scale's raw value changed",
  since ordinary passive growth alone would then count as activity and could keep the gauge visible
  indefinitely. Instead the idle timer only resets via an explicit `WakeFromIdle()` call, which
  `Plugin.cs` makes at deliberate-action points (any particle-burst-triggering event, the
  increase-variant events that don't fire a burst, active Self Sucking-boosted growth, and the
  dedicated `/attention` emote), fading back in over `HudFadeDurationSeconds`; a separate,
  independently toggleable `HudHideOutOfCombat` can hide it entirely while out of combat regardless
  of the idle timer - either condition alone can hide the gauge, both need to want it visible for it
  to show. Also draws a soft radiating glow centered on the bottle, pulsing rhythmically via a
  sine wave (not a literal on/off strobe, which could be visually harsh or a photosensitivity
  concern) whenever the threshold effect's ramp fraction is nonzero (`ThresholdEffectGlowEnabled`,
  on by default, `ThresholdEffectGlowIntensity`/`ThresholdEffectGlowPulseSpeed`/
  `ThresholdEffectGlowSize` configurable) -
  reuses the vignette's own tint color for thematic consistency, and independently recomputes the
  same ramp-fraction math `ThresholdEffectOverlay` uses internally rather than requiring a direct
  reference between the two classes. Draws via `ImGui.GetForegroundDrawList()`, not the bottle
  window's own draw list - an earlier version used the window's own list, which clipped the glow to
  the small window's rectangular bounds once it was large enough to exceed them, visible as solid
  squares rather than a radiating glow. Note this means the glow composites on top of the bottle
  rather than strictly behind it, since foreground draw list content always renders above regular
  window content regardless of call order - it stays semi-transparent enough that the bottle
  should still read through clearly.
- `MilkParticleBurst.cs` — reusable particle-burst physics/rendering used by `HudGaugeWindow`'s
  local burst. Always draws via `ImGui.GetForegroundDrawList()`, specifically so particles fly
  freely rather than being clipped to the small bottle window's bounds - and is drawn after the
  bottle window each frame, so the particle layer sits on top of the bottle and both gauges.
  Originates from the very top tip of the bottle, and its speed/gravity/droplet size all scale
  with `HudScale` so the whole burst grows or shrinks along with the bottle.
- `ThresholdEffectOverlay.cs` — a full-screen pink hazy vignette (built from
  `AddRectFilledMultiColor`, a 4-corner gradient fill not used elsewhere in this project - the four
  edges each fade smoothly toward center, and each corner gets its own radial-style fade rather
  than a flat block, avoiding a visible seam) whose intensity ramps continuously as the applied
  scale rises from `ThresholdEffectRampStartScale` (1.15 by default, 0% intensity) to
  `ThresholdEffectRampEndScale` (1.30 by default, 100% intensity) -
  `ThresholdEffectEnabled` is off by default. `ThresholdEffectFadeSeconds` adds a further layer of
  temporal easing on top of the ramp's own inherent smoothness, so rapid flicker right at either
  boundary doesn't show. Color, intensity, and vignette size are all configurable. This is purely
  cosmetic - drawn on top of the already-rendered game frame the same way the particle burst is,
  never touching input, movement, or anything mechanical. A genuine current limitation: this
  can NOT actually blur the screen - a true blur needs shader/post-processing access to the
  rendering pipeline itself, which nothing in this project has any established path to; the
  vignette is the closest achievable approximation with the tools already in use here. The looping
  heartbeat and moan sounds are each a separate, independent on/off trigger -
  `ThresholdEffectHeartbeatSoundThreshold` and `ThresholdEffectMoanSoundThreshold` (1.25 by default,
  each) are their own configurable values, unrelated to the vignette's own ramp start/end and
  unrelated to each other - and, per request, able to genuinely play at the same time (see
  `AudioMixer.cs` below for why that required a real architecture change). These two used to be a
  single combined heartbeat.wav file with both
  sounds mixed into the same waveform; split into two separate embedded files, player classes, and
  thresholds per request, so either can be tuned or disabled independently.
- `AudioMixer.cs` — a single shared NAudio output device + mixer (`WaveOutEvent` +
  `MixingSampleProvider`) that `HeartbeatSoundPlayer`, `MoanSoundPlayer`, and `BurpSoundPlayer` all
  route through, added specifically to fix a real, reported bug: the heartbeat and moan sounds were
  audibly cutting each other off instead of overlapping. Root cause, confirmed via research rather
  than assumed - the project's original sound playback used `System.Media.SoundPlayer`, which wraps
  the Windows `PlaySound` API; that API does NOT support playing multiple sounds simultaneously,
  even from separate `SoundPlayer` instances - starting a new one interrupts whatever was already
  playing through it. `MixingSampleProvider` is NAudio's own documented fix for exactly this
  scenario: open the output device once, and properly mix any number of sound sources into that
  single stream, rather than fighting over one OS-level channel. `mixer.ReadFully = true` keeps the
  device continuously running (playing silence when idle) rather than starting/stopping it per
  sound. Pinned to NAudio 3.0.1 specifically, not an older 2.x release, after finding directly in
  NAudio's own current release notes that earlier versions of the 3.x line had a packaging bug
  where a plain `netX.0-windows` TFM (exactly what this project targets) could silently resolve to
  the cross-platform build, missing `WaveOut`/WASAPI entirely - fixed in 3.0.1. A NAudio dependency
  was tried once before in this project for a different, unrelated reason (continuous volume
  ramping) and removed when that need went away; this is a fresh addition for a new, genuine
  requirement, not a resurrection of that old code.
- `LoopingSampleProvider.cs` — NAudio has no built-in "loop this sound forever" sample provider, so
  this implements the standard, documented pattern for one: per NAudio's own `Read()` contract, a
  read returning fewer samples than requested means the underlying stream has ended, so on that
  condition this seeks it back to position 0 and keeps filling the buffer from the start, rather
  than returning a short read (which `MixingSampleProvider` would otherwise treat as "finished" and
  silently drop from the mix). Used by both `HeartbeatSoundPlayer` and `MoanSoundPlayer`, not
  `BurpSoundPlayer` (a one-shot sound that's supposed to actually finish).
- `HeartbeatSoundPlayer.cs` — loads and loops a heartbeat sound (embedded directly into the
  assembly, see the `EmbeddedResource` entry in the `.csproj`), mixed into the shared `AudioMixer`
  via a `LoopingSampleProvider` wrapper. Originally played via `System.Media.SoundPlayer` directly,
  a standard Windows-only .NET BCL class - moved onto the shared mixer per request once a second,
  independently-triggerable sound (moan) needed to play at the same time as this one, which
  `SoundPlayer` genuinely couldn't do (see `AudioMixer.cs` above). A separate moan sound used to be
  mixed directly into this same file - split out per request into `MoanSoundPlayer.cs` below, its
  own file/thresholds.
- `MoanSoundPlayer.cs` — exactly mirrors `HeartbeatSoundPlayer.cs`'s structure for a second,
  independent looping sound - embeds and loops `moan.wav` through the same shared `AudioMixer`, own
  resource lookup, own `StartLooping()`/`Stop()`/`Dispose()`. Split out from
  `HeartbeatSoundPlayer.cs` per request (the two sounds used to be mixed into a single
  heartbeat.wav waveform) rather than generalizing into one shared class, matching this project's
  own established precedent (`BurpSoundPlayer` vs `HeartbeatSoundPlayer`) of one small class per
  embedded sound.
- `BurpSoundPlayer.cs` — same embedding approach as `HeartbeatSoundPlayer`, but for a
  one-shot burp sound instead of a loop, also now mixed into the shared `AudioMixer` rather than
  played via `SoundPlayer` directly - each `Play()` call creates a fresh, independent playback
  instance from cached audio bytes (rather than re-reading the embedded resource each time, since
  streams can only be read once sequentially), wrapped in a small private
  `AutoDisposeOnFinishSampleProvider` that disposes its own backing stream the instant playback
  actually finishes, since `MixingSampleProvider` automatically drops a finished input from the mix
  but has no way to know it also owns a disposable stream that needs cleaning up. Repeated or
  overlapping `Play()` calls each play independently in full now, rather than restarting or cutting
  each other off. Triggered from `Plugin.cs`, scheduled to play `SelfSuckingBurpDelaySeconds` (0.5 by default) after
  the Self Sucking Threshold fires "/attention motion" rather than instantly - the delay is checked
  every tick regardless of Mode or whether the original trigger conditions still hold by then, since
  the burp is meant to accompany the animation change that already happened, not the live threshold
  state at playback time.
- `FoodScale.cs` / `FoodBuffTracker.cs` — the food-based source.
- `ManaScale.cs` — the mana-based source (`ManaTracker` + `ManaScale`, mirrors the
  original ManaMune).
- `JobScale.cs` — the job-relevant source (`JobBuffTracker` + `JobScale`). Every tracked job
  (`TrackedAbilityNames` - Provoke/Equilibrium/Second Wind/Lucid Dreaming) uses the same mechanic:
  each ability's cooldown is read via FFXIVClientStructs' `ActionManager` (`GetCooldownState()` -
  requires the `FFXIVClientStructs` reference and `AllowUnsafeBlocks` in the `.csproj`) purely as
  a "was it just used" pulse, which Plugin.cs uses to **subtract** `JobOveruseBonus` (scaled
  per-ability by a fully user-configurable multiplier - `ProvokeMultiplier`/`EquilibriumMultiplier`/
  `LucidDreamingMultiplier`/`SecondWindMultiplier` in `Configuration.cs`, looked up via
  `JobScale.GetOveruseMultiplier()`)
  from a persistent scale value it owns (floored at `JobCombatFloorScale`) and continuously grows
  back up via `JobScale.ApplyGrowth()` toward whichever ceiling currently applies (`JobUpperLimitScale`
  in combat, `JobBaselineScale` out of combat) - growth only ever pushes up from below the ceiling,
  never restores downward. Extend `TrackedAbilityNames` to support more jobs later.
  `JobBuffTracker.GetGcdCooldownState()` is a second, separate query this same class answers -
  reading `GetRecastGroupDetail()` directly for a given recast group number rather than resolving
  one from a tracked action name, since the GCD is a single group shared by every spell/weaponskill.
  This powers `GcdReducesScaleEnabled`, the PRIMARY mechanic for fighting the scale back down (see
  Plugin.cs's own dual-signal check - a rising edge of OnCooldown OR Elapsed decreasing while
  already on cooldown, the latter added after discovering GCDs pressed at the exact instant the
  previous one ends could otherwise go undetected) - `GcdRecastGroup` (default 57) was confirmed empirically via
  `ScanActiveRecastGroups()` and `/milkmeter gcddebug`'s active-group scan; community
  documentation had suggested group 58, which was wrong on this client version.
- `EmoteLoopTracker.cs` — detects whether the player is performing a looping emote via
  FFXIVClientStructs' `Character.Mode`/`ModeParam` (verified real fields, not guessed). Supports
  three effects, each narrowed to a specific emote via a ModeParam value confirmed through
  `/milkmeter emotedebug` (no verified public mapping from the raw index to a named emote
  exists, so these aren't guessed constants) - `/shakedrink` (`ShakeDrinkEmoteModeParam` = 76)
  dramatically speeds up Passive Scale Gen and forces its ceiling to Maximum Scaling (In Combat)
  regardless of actual combat state (`ShakeDrinkBoostEnabled`/`ShakeDrinkGrowthRateMultiplier`);
  `/cackle` - labeled "Self Sucking Drain (/cackle)" in settings (`CackleEmoteModeParam` = 98) does
  the mirror opposite, dramatically speeding up a drain
  toward `CackleDrainFloorScale` (1.20 by default - a separate value from Minimum Scaling, so
  /cackle alone can't drain all the way down) instead (`CackleDrainBoostEnabled`/
  `CackleDrainRateMultiplier`, via the new
  `JobScale.ApplyDrain()` - a mirror image of `ApplyGrowth()`). While the drain is actively
  running, the bottle-local burst repeats once per second, in addition to its usual
  ability-use/damage-taken triggers. Both revert to
  normal the instant they stop; whatever value was reached simply stays there. `/attention`
  (`AttentionEmoteModeParam` = 29) is different in kind from the other two - it doesn't touch the
  job scale at all, it purely wakes the HUD gauge from its idle fade
  (`AttentionWakeEnabled`), a dedicated way to "check the gauge's status" without needing to use an
  ability or take damage first. In settings these three appear as "Breast Massage (/shakedrink)",
  "Self Sucking Drain (/cackle)", and "Check Breasts (/attention)". Separately, "Self Sucking
  Threshold (/cackle to /attention)" in settings - while `/cackle` is active AND scale is at or below
  `CackleDrainFloorScale` (the same floor the drain itself is capped at - this used to check a
  separate `SelfSuckingThreshold` value, removed entirely and unified onto the floor per request,
  since scale can't drop below it via the drain alone anyway) - repeatedly forces "/attention
  motion" once per second (`SelfSuckingThresholdAutoAttentionEnabled`) for as long as both hold,
  not just once; this is partly a defensive measure, since it isn't confirmed whether the game
  always honors a direct switch from one already-active looping emote to another on the very first
  attempt. The burp sound (`SelfSuckingBurpDelaySeconds`, 0.5 by default) fires only once, on a
  genuine falling edge - scale observed strictly above the floor within
  `SelfSuckingBurpRecentAboveFloorWindowSeconds` (5.0 by default) beforehand, AND at/below it now -
  not on every repeated swap attempt, and not just because /cackle happened to start while scale was
  already sitting at/below the floor from an earlier session (a plain single-tick falling-edge
  check couldn't tell that apart from a genuine fresh drop, since the tracked "was it at/below last
  check" flag resets every time /cackle stops being active). This check is
  deliberately mode-agnostic and
  freeze-agnostic (unlike the drain mechanic itself), so its state can't go stale
  across a mode switch or death and silently block a legitimate re-trigger. `/hum`
  (`HumEmoteModeParam` = 46) is a fourth tracked emote, forced once per second
  (`HumAutoTriggerEnabled`, `HumTriggerIntervalSeconds` in `Plugin.cs`) for as long as scale is
  at or above `HumThresholdScale` (1.20 by default) AND the player is standing still - both at
  once, repeating on its own interval rather than firing just once (an earlier version also
  required some OTHER looping emote to already be playing and only fired once, but both of those
  were changed per request). It also wakes the HUD gauge from its idle fade while active
  (`HumWakeEnabled`), mirroring `/attention`'s own wake behavior exactly. Per a later request, the
  trigger is now ALSO suppressed entirely (skipped for the tick regardless of the other two
  conditions) while the player is Charmed (`CharmedEmoteModeParam` = 33) or performing Ball Dance
  (`BallDanceEmoteModeParam` = 6), so it doesn't interrupt either of those - both are checked the
  same way as the four tracked emotes above, via `EmoteLoopTracker.IsCharmedActive()`/
  `IsBallDanceActive()`. "Standing still" is a new kind of
  check for this project - `Plugin.cs` tracks `ObjectTable.LocalPlayer.Position` frame-to-frame,
  requiring it to stay effectively unchanged for at least half a second before counting as still.
  `/milkmeter humdebug` breaks down the trigger's conditions separately
  (scale threshold, standing-still, Charmed/Ball Dance suppression), plus when it'll next fire, for
  diagnosing which one isn't being met if the trigger doesn't seem to be firing. All values remain
  user-configurable in the settings window in case a game update changes them, or to swap the
  tracked emote again later.
- `GameCommandSender.cs` — programmatically executes a raw game slash
  command via `RaptureShellModule.ExecuteCommandInner(Utf8String* command, UIModule* uiModule)`, as
  if typed into the chatbox and entered. Web research alone couldn't pin this signature down (see
  the file's own history for that story), so it was confirmed directly from a real compiled
  `FFXIVClientStructs.dll` via Visual Studio's IntelliSense tooltip. Called from `Plugin.cs`'s Self
  Sucking Threshold trigger, still wrapped in try/catch as cheap insurance against any remaining
  runtime surprise.
- `CustomizePlusIpc.cs` — all Customize+ calls, isolated here. Verified against a real
  exported Customize+ template file (bone entries need `Translation`/`Rotation`/`Scaling`
  blocks, and the scale field is `Scaling`, not `Scale`).
- `Configuration.cs` — on/off toggle, active `Mode`, `ManaInverted` flag, food/job scaling
  range/taper settings; persisted between sessions.
- `repo.json` — Dalamud custom-repo manifest, same shape as the original.

## Publishing as an installable plugin repository

Once you're happy with a build, you can distribute it as a real Dalamud custom repo
instead of loading it locally as a dev plugin:

1. **Create a GitHub repo** at `github.com/<your-username>/MilkMeter` (public).
2. **Update `repo.json`** (and `MilkMeter.json`) if `<your-username>` isn't
   `MrReeh` - the `RepoUrl` and three `DownloadLink*` fields all need to point at your
   actual repo.
3. **Push this project** to that repo, including `.github/workflows/release.yml` and
   `.gitignore`.
4. **Bump the version** in `MilkMeter.csproj` (`<Version>`) and in `repo.json`'s
   `AssemblyVersion` - keep these two in sync every release.
5. **Tag and push**: `git tag v0.2.0.0 && git push origin v0.2.0.0` (match the tag to the
   version you just set). This triggers `.github/workflows/release.yml`, which builds in
   Release configuration, zips the `.dll` + `.deps.json` + manifest into `latest.zip`, and
   attaches it to a new GitHub Release at that tag.
6. **Check the Actions tab** on your repo to confirm the workflow succeeded and a Release
   with `latest.zip` attached now exists.
7. **In-game**, open the plugin installer → Settings → Experimental → Custom Plugin
   Repositories, and add: `https://raw.githubusercontent.com/<your-username>/MilkMeter/main/repo.json`
8. Save, then search for "Milk Meter" in the plugin installer and install it
   normally - no more manual `dotnet build` + copy-to-devPlugins needed for future updates,
   just repeat steps 4-6 per release.

You can keep using the local `devPlugins` dev-loading setup alongside this for fast
iteration while developing - the two aren't mutually exclusive, just don't have both
loaded at once (unload the dev-loaded copy before installing the repo version, or vice
versa, since they'd otherwise fight over the same `InternalName`).
