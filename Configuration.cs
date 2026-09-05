using System.Collections.Generic;
using Dalamud.Configuration;
using Dalamud.Plugin;

namespace MilkMeter;

public enum ScaleMode
{
    Mana,
    Food,
    Job,
    Emote,
}

/// <summary>
/// One configured "using this emote sets the chest to this scale"
/// mapping. ExpectedChatText is what an incoming self-performed emote
/// chat message is compared against (Contains, case-insensitive) to
/// detect that THIS trigger fired - see EmoteScaleTracker for why this
/// is a manually-entered field rather than something auto-resolved from
/// game data, and /milkmeter emotedebug for reading off the
/// exact text to paste in.
/// </summary>
public sealed class EmoteScaleTrigger
{
    /// <summary>Free-text label for your own reference (e.g. "/dazed") - not used for matching.</summary>
    public string Label { get; set; } = "";

    public string ExpectedChatText { get; set; } = "";

    public float TargetScale { get; set; } = 1.0f;

    public bool Enabled { get; set; } = true;
}

public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    public bool Enabled { get; set; } = true;

    /// <summary>
    /// If true, every mechanic that would modify or push the applied
    /// chest scale is skipped entirely - passive growth, ability-use,
    /// damage-taken, jumping, /dazed/guard, death-reset, the
    /// ease-toward-target animation, and the Customize+ push itself.
    /// Unlike Enabled above, this deliberately leaves the HUD gauge and
    /// its particle effects still rendering, just dimmed to 10% opacity
    /// (frozen at whatever scale was applied at the moment of pausing)
    /// and following the same idle-fade/hover-reveal rules as any other
    /// state rather than being forced fully visible - the point is to
    /// freeze scaling in place
    /// while keeping the plugin's visible presence intact, not to hide
    /// it. The threshold effect (vignette/glow/heartbeat sound) is the
    /// one exception - it completely stops the instant this is true,
    /// per request, rather than continuing to evaluate against the
    /// frozen scale value. Toggled by left-clicking the HUD gauge
    /// itself (see HudGaugeWindow.Draw()) or this checkbox in settings.
    /// Off by default.
    /// </summary>
    public bool ScalingPaused { get; set; } = false;

    /// <summary>Which signal currently drives the chest scale.</summary>
    public ScaleMode Mode { get; set; } = ScaleMode.Job;

    /// <summary>
    /// Original ManaMune behavior: if true, chest shrinks as MP is spent
    /// (full at 100% MP). If false, inverted - shrinks as MP is restored.
    /// Only relevant when Mode == ScaleMode.Mana.
    /// </summary>
    public bool ManaInverted { get; set; } = false;

    /// <summary>Chest scale with no food buff active.</summary>
    public float FoodMinScale { get; set; } = FoodScale.DefaultMinScale;

    /// <summary>Chest scale with a freshly-eaten food buff.</summary>
    public float FoodMaxScale { get; set; } = FoodScale.DefaultMaxScale;

    /// <summary>
    /// How many minutes before the buff expires the taper begins. Full
    /// size is held for any remaining time above this window, regardless
    /// of the food's total duration.
    /// </summary>
    public float FoodTaperMinutes { get; set; } = FoodScale.DefaultTaperMinutes;

    /// <summary>
    /// How fast the applied chest scale eases toward the target, in scale
    /// units per second. E.g. 0.10 means moving across a 0.35 range
    /// (0.65-1.00) takes 3.5 seconds. This smooths every transition -
    /// eating food, the buff tapering near expiry, or it ending - instead
    /// of snapping instantly to the new target.
    /// </summary>
    public float ScaleTransitionRate { get; set; } = 0.2f;

    /// <summary>
    /// Universal floor for the current job scale, regardless of combat
    /// state - the lowest using tracked abilities can push it to. The
    /// combat-growth mechanic (Provoke plus each tank job's own extra
    /// tracked abilities, Equilibrium for Warrior, Second Wind, Lucid
    /// Dreaming - see JobBuffTracker.TrackedAbilityNames) is the mirror
    /// image of a typical "use it to grow" design: using any tracked
    /// ability SUBTRACTS JobOveruseBonus from the CURRENT scale (clamped
    /// so it never goes below this floor), and simply existing over time
    /// GROWS that value back up toward whichever ceiling currently
    /// applies - JobUpperLimitScale while in combat, or JobBaselineScale
    /// once out of combat - but only ever from BELOW that ceiling: if
    /// you're already above it (e.g. the ceiling just dropped), it stays
    /// right where it is rather than snapping back down. See
    /// PassiveScaleGenPerSecond for the growth rate, and JobOveruseBonus for
    /// the per-use amount subtracted.
    /// </summary>
    public float JobCombatFloorScale { get; set; } = JobScale.DefaultCombatFloorScale;

    /// <summary>The neutral starting value, and the growth ceiling once out of combat (see JobCombatFloorScale).</summary>
    public float JobBaselineScale { get; set; } = JobScale.DefaultBaselineScale;

    /// <summary>
    /// Base amount SUBTRACTED from the current job scale each time a
    /// tracked ability is used - stacks if you use it repeatedly, which
    /// is what lets overusing your job action push scale all the way
    /// down toward JobCombatFloorScale. The actual amount subtracted per
    /// use is this value times that specific ability's own configurable
    /// multiplier - see the *Multiplier properties below, and
    /// JobScale.GetOveruseMultiplier for how they're looked up by name.
    /// </summary>
    public float JobOveruseBonus { get; set; } = JobScale.DefaultOveruseBonus;

    /// <summary>
    /// Per-ability overuse multipliers - fully user-configurable, not
    /// derived from anything automatically. Defaults happen to match
    /// each ability's real cooldown relative to Provoke's 30s (Provoke=1x
    /// baseline; Equilibrium/Lucid Dreaming's 60s=2x; Second Wind's
    /// 120s=4x), so a fresh install starts balanced, but there's no
    /// enforcement keeping them that way - adjust freely in the settings
    /// window. Can be set negative (down to -5), which flips that
    /// specific ability's effect from a reduction into an INCREASE
    /// instead - see the sign-handling in Plugin.cs's ability-use branch.
    /// Per request, this increase is now capped at JobUpperLimitScale
    /// (Maximum Scaling In Combat) ALWAYS, even out of combat - unlike
    /// the damage-taken/jump increase variants, this one does NOT switch
    /// down to JobBaselineScale out of combat, so a negative-multiplier
    /// ability can push scale past Baseline regardless of combat state.
    /// See JobScale.GetOveruseMultiplier for how these are looked up by
    /// ability name.
    /// </summary>
    public float ProvokeMultiplier { get; set; } = 0.0f;

    public float EquilibriumMultiplier { get; set; } = -1.0f;

    public float LucidDreamingMultiplier { get; set; } = -1.0f;

    public float SecondWindMultiplier { get; set; } = -1.0f;

    /// <summary>Overuse-bonus multiplier for Reprisal (all four tank jobs - see TrackedAbilityNames). 60s recast, same 2x-Provoke baseline as Equilibrium/Lucid Dreaming. See JobScale.GetOveruseMultiplier.</summary>
    public float ReprisalMultiplier { get; set; } = 2.0f;

    /// <summary>
    /// Growth ceiling while in combat - the highest simply existing over
    /// time can grow the current job scale to while fighting. Using a
    /// tracked ability is unaffected by this (it only ever shrinks
    /// toward the floor), so this only matters for how high in-combat
    /// growth can bring size back up to.
    /// </summary>
    public float JobUpperLimitScale { get; set; } = JobScale.DefaultUpperLimitScale;

    /// <summary>
    /// How much the current job scale grows per SECOND, directly - a
    /// value of 1.0 means +1.0 scale per second. Replaced an earlier
    /// "minutes to grow a full 1.0 unit" parameterization per request;
    /// the default here (1/300 ≈ 0.00333/sec) preserves the exact same
    /// effective rate the old default (5 minutes) had, so existing
    /// behavior doesn't silently change for anyone who hasn't touched
    /// this setting. Slider range is -0.10 to 0.10, per request. This
    /// still normally only grows up to JobBaselineScale (Maximum Scaling
    /// Out of Combat) while out of combat, or JobUpperLimitScale (Maximum
    /// Scaling In Combat) while in combat - see ExtraScaleGenPerSecond
    /// below for a second, independent rate that ignores this in/out of
    /// combat ceiling switch entirely. Growth only ever pushes UP toward
    /// whichever ceiling currently applies, and only from below it - it
    /// never pulls scale down on its own; only using a tracked ability
    /// (JobOveruseBonus) does that. A rate of 0 OR BELOW means no
    /// passive growth at all (JobScale.ApplyGrowth's own internal check
    /// is "growthPerSecond &lt;= 0f", not just "== 0f"). IMPORTANT: this
    /// same value also feeds the /dazed drain rate below
    /// (drainPerSecond = PassiveScaleGenPerSecond * DazedDrainRateMultiplier,
    /// see Plugin.cs) - since DazedDrainRateMultiplier is always
    /// positive, a negative PassiveScaleGenPerSecond makes that computed
    /// drainPerSecond negative too, which ALSO trips ApplyDrain's own
    /// "&lt;= 0f means no drain" check. In other words: the ENTIRE negative
    /// half of this slider's range currently behaves identically to
    /// exactly 0 for BOTH normal passive growth AND the /dazed drain -
    /// there's no distinct "negative" effect yet, unlike
    /// ExtraScaleGenPerSecond below, which does have one. This was
    /// flagged to the user rather than silently redesigned, since making
    /// it meaningful would require a real design decision about how it
    /// should interact with the /dazed drain specifically.
    /// </summary>
    public float PassiveScaleGenPerSecond { get; set; } = JobScale.DefaultPassiveScaleGenPerSecond;

    /// <summary>
    /// A second, independent per-second rate, applied on top of
    /// (in addition to) PassiveScaleGenPerSecond above - standalone and
    /// NOT multiplicative of any other factor, unlike
    /// PassiveScaleGenPerSecond (which gets scaled by
    /// DazedDrainRateMultiplier or ShakeDrinkGrowthRateMultiplier
    /// depending on state) - this value is used exactly as configured.
    /// IGNORES the in/out of combat ceiling switch entirely. A
    /// POSITIVE value grows toward JobUpperLimitScale (Maximum Scaling In
    /// Combat) regardless of actual combat state, the same way the
    /// negative-multiplier ability increase above does. A NEGATIVE value
    /// (range -0.10 to 0.10, per request) instead DRAINS toward
    /// JobCombatFloorScale (Minimum Scaling, Always), using the same
    /// combat-state-independent reasoning in the opposite direction. Off
    /// (0.0) by
    /// default, so existing behavior is unaffected unless explicitly
    /// turned up. Per request, this is now gated behind /dazed's drain
    /// NOT being active - while /dazed's drain is running, neither this
    /// nor ordinary Passive Scale Gen generates any scaling change at
    /// all (Passive Scale Gen is instead entirely repurposed into the
    /// drain rate itself during that window, contributing no separate
    /// growth of its own), so /dazed's drain is never fought against by
    /// either. Outside of that window, a negative value here still works
    /// against ordinary passive growth/shakedrink-boosted growth, since
    /// both push scale in opposite directions within the same tick - that
    /// interaction is unchanged.
    /// </summary>
    public float ExtraScaleGenPerSecond { get; set; } = 0f;

    /// <summary>
    /// If true, performing the /shakedrink looping emote (see
    /// EmoteLoopTracker) dramatically speeds up Passive Scale Gen and forces
    /// its ceiling to JobUpperLimitScale (Maximum Scaling In Combat)
    /// regardless of actual combat state - so /shakedrink can push scale
    /// past Baseline even out of combat. The instant the emote stops,
    /// both the rate and the ceiling revert to normal on the very next
    /// frame - whatever value it reached stays there (subject to normal
    /// growth/no-further-growth rules from then on), it doesn't snap
    /// back down on its own. On by default.
    /// </summary>
    public bool ShakeDrinkBoostEnabled { get; set; } = true;

    /// <summary>
    /// How many times faster Passive Scale Gen runs while /shakedrink is
    /// active - multiplies PassiveScaleGenPerSecond by this value for that
    /// duration. 10x by default ("tremendously" faster, per the request
    /// this was built for).
    /// </summary>
    public float ShakeDrinkGrowthRateMultiplier { get; set; } = 5.0f;

    /// <summary>
    /// The ModeParam value that identifies /shakedrink specifically -
    /// see EmoteLoopTracker's class doc comment for the full story on
    /// how this was found (not guessed). Confirmed as 76 via
    /// /milkmeter emotedebug. -1 would mean "match ANY looping
    /// emote" instead, in case a game update ever changes this value and
    /// it needs resetting until re-confirmed.
    /// </summary>
    public int ShakeDrinkEmoteModeParam { get; set; } = 76;

    /// <summary>
    /// Mirror of ShakeDrinkBoostEnabled for /dazed: while active, this
    /// dramatically speeds up draining the job scale DOWN toward
    /// DazedDrainFloorScale instead of growing it - "empty the gauge".
    /// Reverts to normal Passive Scale Gen the instant the emote stops, same
    /// "stays wherever it ended up" behavior as the shakedrink boost. On
    /// by default. (Briefly swapped to /greentea, then reverted after
    /// confirming /greentea isn't actually a looping emote at all - it
    /// plays a single pass and stops, so Character.Mode never reaches
    /// EmoteLoop for it, which this whole detection mechanism requires.)
    /// </summary>
    public bool DazedDrainBoostEnabled { get; set; } = true;

    /// <summary>How many times faster than normal Passive Scale Gen's rate the drain runs while /dazed is active. 10x by default, matching ShakeDrinkGrowthRateMultiplier.</summary>
    public float DazedDrainRateMultiplier { get; set; } = 7.0f;

    /// <summary>
    /// The floor the /dazed drain targets - a dedicated value separate
    /// from JobCombatFloorScale (Minimum Scaling), which stays used
    /// elsewhere (ability-use reduction, damage-taken reduction,
    /// death-reset). 1.20 by default, so /dazed alone can't drain all
    /// the way down to Minimum Scaling.
    /// </summary>
    public float DazedDrainFloorScale { get; set; } = 1.0f;

    /// <summary>The ModeParam value that identifies /dazed specifically. Confirmed as 79 via /milkmeter emotedebug - see ShakeDrinkEmoteModeParam for the full story on how this kind of value gets found rather than guessed.</summary>
    public int DazedEmoteModeParam { get; set; } = 79;

    /// <summary>
    /// Mirror of DazedDrainBoostEnabled for /water ("Breast Feeding
    /// Drain"): while active, this dramatically speeds up draining the
    /// job scale DOWN toward WaterDrainFloorScale instead of growing it
    /// - same "empty the gauge" mechanic as /dazed, just a second,
    /// independently-configured emote/rate/floor rather than sharing
    /// Dazed's. Reverts to normal Passive Scale Gen the instant the
    /// emote stops, same "stays wherever it ended up" behavior as every
    /// other looping-emote boost in this file. On by default.
    /// </summary>
    public bool WaterDrainBoostEnabled { get; set; } = true;

    /// <summary>How many times faster than normal Passive Scale Gen's rate the drain runs while /water is active. 7x by default, matching DazedDrainRateMultiplier.</summary>
    public float WaterDrainRateMultiplier { get; set; } = 7.0f;

    /// <summary>
    /// The floor the /water drain targets - a dedicated value separate
    /// from both JobCombatFloorScale (Minimum Scaling) and
    /// DazedDrainFloorScale, which stay used elsewhere. 0.70 by
    /// default.
    /// </summary>
    public float WaterDrainFloorScale { get; set; } = 0.70f;

    /// <summary>The ModeParam value that identifies /water specifically. Confirmed as 75 via /milkmeter emotedebug - see ShakeDrinkEmoteModeParam for the full story on how this kind of value gets found rather than guessed.</summary>
    public int WaterEmoteModeParam { get; set; } = 75;

    /// <summary>
    /// If true, performing the /attention looping emote wakes the HUD
    /// gauge from its idle fade (see HudGaugeWindow.WakeFromIdle()) -
    /// unlike ShakeDrinkBoostEnabled/DazedDrainBoostEnabled, this does
    /// NOT affect the job scale itself in any way; it's purely a "check
    /// the gauge's current status" gesture, mode-agnostic (works
    /// regardless of the active Scale Source, since the HUD gauge itself
    /// isn't Job-mode-exclusive). On by default.
    /// </summary>
    public bool AttentionWakeEnabled { get; set; } = true;

    /// <summary>The ModeParam value that identifies /attention specifically. Confirmed as 29 via /milkmeter emotedebug - see ShakeDrinkEmoteModeParam for the full story on how this kind of value gets found rather than guessed.</summary>
    public int AttentionEmoteModeParam { get; set; } = 29;

    /// <summary>
    /// If true, forces "/guard motion" once per second (see
    /// GuardTriggerIntervalSeconds in Plugin.cs) for as long as BOTH of
    /// the following hold simultaneously: the applied scale is at or
    /// above GuardThresholdScale, and the player is standing still
    /// (position hasn't meaningfully changed for a short window - see
    /// Plugin.cs). Unlike the Self Sucking Threshold's rising-edge
    /// pattern (fire once, wait for conditions to reset), this keeps
    /// repeating on its own interval for as long as both conditions
    /// hold - an earlier version also required some OTHER looping emote
    /// to already be playing, but that requirement was removed per
    /// request. Per a later request, this is now ALSO suppressed
    /// entirely (skipped for the tick, regardless of the other
    /// conditions) while the player is Charmed
    /// (CharmedEmoteModeParam) or performing Ball Dance
    /// (BallDanceEmoteModeParam), so it doesn't interrupt either of
    /// those. On by default.
    /// </summary>
    public bool GuardAutoTriggerEnabled { get; set; } = true;

    /// <summary>The applied-scale value that must be reached or exceeded for the guard auto-trigger to fire. 1.20 by default.</summary>
    public float GuardThresholdScale { get; set; } = 1.3f;

    /// <summary>The ModeParam value that identifies /guard specifically. Confirmed as 58 via /milkmeter emotedebug - see ShakeDrinkEmoteModeParam for the full story on how this kind of value gets found rather than guessed.</summary>
    public int GuardEmoteModeParam { get; set; } = 58;

    /// <summary>
    /// If true, while /guard is active (whether forced by the
    /// auto-trigger above or performed manually), wakes the HUD gauge
    /// from its idle fade - mirrors AttentionWakeEnabled's exact
    /// pattern for /attention. On by default.
    /// </summary>
    public bool GuardWakeEnabled { get; set; } = true;

    /// <summary>
    /// The ModeParam value that identifies the "Charmed" crowd-control
    /// status - per request, the guard auto-trigger (GuardAutoTriggerEnabled
    /// above) is suppressed entirely while this is active, so it doesn't
    /// interrupt it. 33 by default.
    /// </summary>
    public int CharmedEmoteModeParam { get; set; } = 33;

    /// <summary>
    /// The ModeParam value that identifies the "Ball Dance"
    /// emote/animation - per request, the guard auto-trigger
    /// (GuardAutoTriggerEnabled above) is suppressed entirely while this
    /// is active, so it doesn't interrupt it. 6 by default.
    /// </summary>
    public int BallDanceEmoteModeParam { get; set; } = 6;

    /// <summary>
    /// If true, while /dazed's drain is active AND scale is at/below
    /// DazedDrainFloorScale (the same floor the drain itself is capped
    /// at - this used to check a separate SelfSuckingThreshold value,
    /// removed per request in favor of unifying on the floor, since
    /// scale literally can't drop below it via the drain alone),
    /// repeatedly forces "/attention motion" once per second for as
    /// long as both conditions hold (see GameCommandSender - method
    /// signature confirmed via IntelliSense against a real compiled
    /// FFXIVClientStructs.dll). An earlier version fired only once per
    /// crossing; this now keeps retrying periodically instead, per
    /// request - partly because it's not confirmed whether the game
    /// always honors a direct switch from one already-active looping
    /// emote to another on the first attempt. On by
    /// default.
    /// </summary>
    public bool SelfSuckingThresholdAutoAttentionEnabled { get; set; } = true;

    /// <summary>How many seconds after the Self Sucking auto-attention-swap first reaches DazedDrainFloorScale before the burp sound (BurpSoundPlayer) plays. 0.5 by default.</summary>
    public float SelfSuckingBurpDelaySeconds { get; set; } = 0.1f;

    /// <summary>
    /// The burp only plays if scale was observed strictly above
    /// DazedDrainFloorScale within this many seconds before falling to
    /// at/below it - distinguishes a genuine fresh drop (burp plays)
    /// from starting /dazed while already at/below the floor (burp
    /// should NOT play, since scale never actually "fell" from
    /// anywhere recent). 5.0 by default.
    /// </summary>
    public float SelfSuckingBurpRecentAboveFloorWindowSeconds { get; set; } = 0.5f;

    /// <summary>
    /// If true, dying immediately resets the current job scale to
    /// JobCombatFloorScale (Minimum Scaling) and FREEZES it there - no
    /// passive regen (JobScale.ApplyGrowth) happens at all while frozen,
    /// so scale stays pinned at the floor until the player is revived
    /// (the freeze clears on the falling edge of ConditionFlag.Unconscious,
    /// regardless of whether this toggle is still on at that point).
    /// Checked every frame, not just while Mode == Job, so a death that
    /// happens while you're on Food or Mana mode still resets/freezes it
    /// in the background. Off by default.
    /// </summary>
    public bool ResetJobScaleToCombatFloorOnDeath { get; set; } = true;

    /// <summary>
    /// If true, every time the player's HP decreases while in combat
    /// (any damage taken, checked frame-to-frame - never triggers out of
    /// combat), the current job scale is immediately adjusted by
    /// DamageTakenScaleIncrease (added, so a positive value raises scale
    /// and a negative value lowers it - "Damage Taken Affects Scale" in
    /// the settings window, previously "Increases" back when only the
    /// positive direction was supported), clamped between
    /// JobCombatFloorScale and whichever ceiling passive growth
    /// currently targets (JobUpperLimitScale in combat, JobBaselineScale
    /// out of combat) rather than a fixed value - it behaves like an
    /// accelerated burst of the same regen (or, with a negative amount,
    /// an accelerated drain), not its own separate cap. Checked every
    /// frame regardless of Mode, same as the death-reset toggle. Fires
    /// on every HP tick while in combat, so a fast-ticking DoT or
    /// sustained AoE can trigger it repeatedly in quick succession -
    /// keep DamageTakenScaleIncrease's magnitude small unless that's the
    /// effect you want. On by default. A separate, dedicated "Damage
    /// Taken Reduces Scale" toggle existed here previously but was
    /// removed per request in favor of this single bidirectional
    /// toggle, with GcdReducesScaleEnabled below still the primary way
    /// to fight the scale back down.
    /// </summary>
    public bool IncreaseScaleOnDamageTaken { get; set; } = true;

    /// <summary>Amount added to the current job scale per in-combat HP-decrease event when IncreaseScaleOnDamageTaken is on. Positive raises scale, negative lowers it.</summary>
    public float DamageTakenScaleIncrease { get; set; } = 0.01f;

    /// <summary>
    /// If true, every GCD invocation (a spell or weaponskill - detected
    /// as the rising edge of the shared GCD recast group going on
    /// cooldown, see JobBuffTracker.GetGcdCooldownState) adds
    /// GcdScaleReductionAmount to the current job scale (so a positive
    /// amount raises scale and a negative amount lowers it - "GCD
    /// Affects Scale" in the settings window; the property name and its
    /// negative-by-default value are both carried over from when this
    /// only supported lowering and used subtraction instead, but the
    /// sign convention now matches DamageTakenScaleIncrease/
    /// JumpScaleIncreaseAmount exactly, per request), clamped between
    /// JobCombatFloorScale and JobUpperLimitScale just like ability-use
    /// reductions. Unlike the damage-taken toggle above, this fires both
    /// in AND out of combat - a GCD is a GCD either way, so unlike the
    /// damage-taken toggle's ceiling it doesn't switch to
    /// JobBaselineScale out of combat (same reasoning Jump's own ceiling
    /// uses). This is the PRIMARY mechanic for fighting the scale back
    /// down as it builds, per request - on by default, unlike every
    /// other opt-in scale-modifying toggle in this file.
    /// </summary>
    public bool GcdReducesScaleEnabled { get; set; } = true;

    /// <summary>Amount added to the current job scale per GCD invocation when GcdReducesScaleEnabled is on. Positive raises scale, negative lowers it - negative by default so the PRIMARY mechanic still fights scale down out of the box.</summary>
    public float GcdScaleReductionAmount { get; set; } = -0.02f;

    /// <summary>
    /// The FFXIVClientStructs recast group number shared by every GCD
    /// spell/weaponskill - community documentation suggested 58, but
    /// that turned out to be wrong on this client version; confirmed via
    /// /milkmeter gcddebug's active-group scan (pressing GCD
    /// abilities and observing which group consistently activated) to
    /// actually be 57.
    /// </summary>
    public int GcdRecastGroup { get; set; } = 57;

    /// <summary>
    /// If true, every jump (detected as a fresh upward vertical-velocity
    /// impulse while ConditionFlag.Jumping or Jumping61 is true - not a
    /// plain rising edge of those flags alone, since an earlier version
    /// found that back-to-back jumps performed immediately upon landing
    /// don't reliably toggle the flag back to false in between, causing
    /// the second jump to go undetected) adjusts the current job scale
    /// by JumpScaleIncreaseAmount (added, so a positive value raises
    /// scale and a negative value lowers it - "Jumping Affects Scale" in
    /// the settings window, previously "Increases" back when only the
    /// positive direction was supported), clamped between
    /// JobCombatFloorScale and JobUpperLimitScale (Maximum Scaling In
    /// Combat) always - unlike IncreaseScaleOnDamageTaken, this doesn't
    /// switch to JobBaselineScale out of combat, per request. Checked
    /// every frame regardless of Mode, same as the damage-taken/
    /// death-reset toggles, and unlike the damage-taken toggle, not
    /// restricted to combat - jumping isn't inherently combat-related
    /// the way taking damage is. Rate-limited by its own dedicated
    /// JumpTriggerCooldownSeconds below, separate from
    /// DamageTriggerCooldownSeconds. On by default.
    /// </summary>
    public bool JumpIncreasesScaleEnabled { get; set; } = true;

    /// <summary>Amount added to the current job scale per jump when JumpIncreasesScaleEnabled is on. Positive raises scale, negative lowers it.</summary>
    public float JumpScaleIncreaseAmount { get; set; } = 0.01f;

    /// <summary>
    /// The minimum vertical velocity (in yalms/second) that counts as a
    /// fresh jump impulse for JumpIncreasesScaleEnabled's detection. This
    /// is a GUESSED starting value, not verified against any known FFXIV
    /// jump-physics constant - if jumps are being missed, this may be
    /// set too high; if a single jump is registering as multiple (or
    /// normal falling/landing motion is being miscounted as a jump), it
    /// may be too low. Use /milkmeter jumpdebug to observe
    /// live vertical velocity while jumping and tune this against what
    /// you actually see.
    /// </summary>
    public float JumpVelocityThreshold { get; set; } = 1.0f;

    /// <summary>
    /// Minimum real-world seconds between jump triggers - separate from
    /// DamageTriggerCooldownSeconds below, since jump-spamming shouldn't
    /// share a cooldown with damage ticks. 0 means no limit (fires every
    /// jump).
    /// </summary>
    public float JumpTriggerCooldownSeconds { get; set; } = 0f;

    /// <summary>
    /// Minimum real-world seconds between damage-taken triggers
    /// (IncreaseScaleOnDamageTaken) - so a fast-ticking DoT can't fire
    /// the effect on every single tick. 0 means no limit (fires every
    /// time HP decreases).
    /// </summary>
    public float DamageTriggerCooldownSeconds { get; set; } = 0f;

    /// <summary>
    /// If true, fires a brief burst of milk droplet particles around the
    /// bottle - a tracked ability's own reduction (a STRICTLY positive
    /// overuse multiplier only; a multiplier sitting exactly at 0.00 is
    /// a no-op and stays silent, same as a negative one), a
    /// GCD-triggered reduction when GcdReducesScaleEnabled is active, or
    /// a negative-amount Damage Taken/Jumping Affects Scale event - see
    /// HudGaugeWindow's local MilkParticleBurst. Does NOT fire for
    /// passive growth, the raising direction of the
    /// GCD/Damage-Taken/Jumping "Affects Scale" toggles, a
    /// zero-or-negative ability multiplier, or the death freeze/reset.
    /// On by default.
    /// </summary>
    public bool ShowMilkBurstEffect { get; set; } = true;

    /// <summary>Whether the always-on-screen "boob gauge" HUD bar is shown at all.</summary>
    public bool ShowHudGauge { get; set; } = true;

    /// <summary>
    /// If true, the HUD gauge fades out after HudFadeIdleSeconds of no
    /// genuine "activity" (see HudGaugeWindow.WakeFromIdle() - passive
    /// growth alone deliberately does NOT count, only deliberate-action
    /// events do), fading back in the instant one occurs. Combined with
    /// HudHideOutOfCombat by taking whichever wants the gauge MORE
    /// hidden - either condition alone can hide it, both need to want it
    /// visible for it to show. On by default.
    /// </summary>
    public bool HudFadeOnIdleEnabled { get; set; } = true;

    /// <summary>How many seconds of no genuine activity (see HudFadeOnIdleEnabled) before the HUD gauge starts fading out. 5 seconds by default.</summary>
    public float HudFadeIdleSeconds { get; set; } = 2.5f;

    /// <summary>How many seconds the actual fade transition takes, in either direction. 0 means an instant snap rather than an eased fade. 1 second by default.</summary>
    public float HudFadeDurationSeconds { get; set; } = 0.2f;

    /// <summary>
    /// If true, the applied scale reaching or exceeding
    /// HudShowAboveScaleThreshold continuously wakes the HUD gauge from
    /// its idle fade - same WakeFromIdle() mechanism as an ability use
    /// or the /attention emote, just driven by the scale value itself
    /// instead of a discrete event. Checked every frame in Plugin.cs
    /// alongside the /attention and /guard wake-checks, so as long as
    /// scale stays at or above the threshold the gauge stays visible
    /// (or fades back in if it had already faded); once scale drops
    /// back below, the normal idle timer resumes counting down from
    /// there like any other wake. Off by default - the whole idle-fade
    /// feature exists to reduce clutter, so this only kicks in once
    /// explicitly turned on.
    /// </summary>
    public bool HudShowAboveScaleEnabled { get; set; } = false;

    /// <summary>The applied-scale value at or above which HudShowAboveScaleEnabled keeps the HUD gauge awake. Independent of every other scale threshold in this file (Guard, threshold-effect ramp, etc.) - purely for HUD visibility. 1.20 by default.</summary>
    public float HudShowAboveScaleThreshold { get; set; } = 1.20f;

    /// <summary>
    /// If true, the HUD gauge is hidden entirely while out of combat,
    /// regardless of HudFadeOnIdleEnabled's own timer - a separate,
    /// independently toggleable condition. Off by default.
    /// </summary>
    public bool HudHideOutOfCombat { get; set; } = false;

    /// <summary>
    /// If true, a full-screen pink hazy vignette (see
    /// ThresholdEffectOverlay) plus two optional, independently
    /// configurable looping sounds - a heartbeat
    /// (ThresholdEffectHeartbeatSoundEnabled) and a separate sound
    /// (ThresholdEffectMoanSoundEnabled) - play as the applied scale
    /// rises through the ThresholdEffectRampStartScale ->
    /// ThresholdEffectRampEndScale
    /// range - a continuous intensity ramp (0% at the start, 100% at the
    /// end), not a single on/off threshold. This is a purely
    /// cosmetic effect - drawn on top of the game frame, doesn't touch
    /// input, movement, or anything mechanical. Off by default.
    /// </summary>
    public bool ThresholdEffectEnabled { get; set; } = true;

    /// <summary>The applied-scale value at which the threshold effect's intensity ramp begins (0% intensity). 1.15 by default.</summary>
    public float ThresholdEffectRampStartScale { get; set; } = 1.15f;

    /// <summary>The applied-scale value at which the threshold effect's intensity ramp reaches its maximum (100% intensity, still scaled by ThresholdEffectIntensity). 1.30 by default.</summary>
    public float ThresholdEffectRampEndScale { get; set; } = 1.30f;

    /// <summary>How many seconds the vignette's fade in/out transition takes. 0 means an instant snap. 1.5 seconds by default.</summary>
    public float ThresholdEffectFadeSeconds { get; set; } = 5.0f;

    /// <summary>Maximum opacity the vignette reaches at full fade-in, at the very edge of the screen. 0.55 by default.</summary>
    public float ThresholdEffectIntensity { get; set; } = 0.4f;

    /// <summary>How far the vignette extends inward from each screen edge, as a fraction of screen height. 0.30 (30%) by default.</summary>
    public float ThresholdEffectVignetteSize { get; set; } = 0.5f;

    /// <summary>The vignette's tint color, as separate 0-1 R/G/B components matching this file's existing style. Defaults to a dark pink/magenta.</summary>
    public float ThresholdEffectColorR { get; set; } = 0.42492014f;

    public float ThresholdEffectColorG { get; set; } = 0.0f;

    public float ThresholdEffectColorB { get; set; } = 0.1699681f;

    /// <summary>
    /// If true, a looping heartbeat sound (embedded in the plugin,
    /// played via System.Media.SoundPlayer - see
    /// HeartbeatSoundPlayer) plays once the applied scale reaches
    /// ThresholdEffectHeartbeatSoundThreshold
    /// - a plain on/off trigger, independent of the vignette's own
    /// ramp (ThresholdEffectRampStartScale/ThresholdEffectRampEndScale)
    /// and independent of the separate moan sound below, which has its
    /// own threshold. Both used to be a single combined .wav file
    /// (heartbeat.wav) with both sounds mixed together, split per
    /// request into two separately embedded files, each with its own
    /// player class and threshold, so either can be tuned or disabled
    /// independently. Played via System.Media.SoundPlayer - which,
    /// per a real, reported bug, can't reliably play multiple sounds
    /// simultaneously even from separate instances (it wraps the
    /// Windows PlaySound API, which doesn't support that). A
    /// NAudio-backed mixer briefly replaced SoundPlayer to work around
    /// this, but that was dropped per request and NAudio has since
    /// been removed entirely, so heartbeat/moan overlap is no longer
    /// guaranteed to work cleanly. On by
    /// default (only takes effect if ThresholdEffectEnabled is also on).
    /// </summary>
    public bool ThresholdEffectHeartbeatSoundEnabled { get; set; } = true;

    /// <summary>The applied-scale value at which the heartbeat sound starts playing - independent of the vignette's own ramp start/end, and independent of the moan sound's own threshold below. 1.25 by default.</summary>
    public float ThresholdEffectHeartbeatSoundThreshold { get; set; } = 1.2f;

    /// <summary>
    /// If true, a second, independent looping sound (embedded in the
    /// plugin, played via System.Media.SoundPlayer - see
    /// MoanSoundPlayer) plays once the applied scale reaches
    /// ThresholdEffectMoanSoundThreshold - same mechanism as the
    /// heartbeat sound above, just a separate file/threshold/player, per
    /// request to split what used to be one combined .wav (heartbeat.wav
    /// mixed both sounds together) into two independently configurable
    /// ones, able to genuinely play at the same time as each other. On
    /// by default (only takes effect if ThresholdEffectEnabled
    /// is also on).
    /// </summary>
    public bool ThresholdEffectMoanSoundEnabled { get; set; } = false;

    /// <summary>The applied-scale value at which the moan sound starts playing - independent of the vignette's own ramp start/end, and independent of the heartbeat sound's own threshold above. 1.25 by default.</summary>
    public float ThresholdEffectMoanSoundThreshold { get; set; } = 1.3f;

    /// <summary>
    /// If true, a pulsing radiating glow centered on the bottle (see
    /// HudGaugeWindow) is drawn whenever the threshold effect's ramp
    /// fraction is nonzero - a rhythmic soft brightness pulse (not a
    /// literal on/off strobe, which could be visually harsh or a
    /// photosensitivity concern) using the same tint color as the
    /// vignette for thematic consistency. Drawn via
    /// ImGui.GetForegroundDrawList() (an earlier version used the
    /// bottle window's own draw list, which clipped the glow to the
    /// small window's rectangular bounds - visible as solid squares
    /// rather than a radiating glow once the glow was made large enough
    /// to exceed those bounds) - note this means the glow composites
    /// on top of the bottle rather than strictly behind it, since
    /// foreground draw list content always renders above regular window
    /// content regardless of call order; it stays semi-transparent
    /// enough that the bottle should still read through clearly.
    /// Scales with the same ramp
    /// fraction as everything else in the threshold effect - barely
    /// visible near the ramp's start, more prominent near its end. On
    /// by default (only takes effect if ThresholdEffectEnabled is also
    /// on).
    /// </summary>
    public bool ThresholdEffectGlowEnabled { get; set; } = true;

    /// <summary>Maximum opacity the glow reaches at full ramp fraction and the brightest point of its pulse. 0.6 by default.</summary>
    public float ThresholdEffectGlowIntensity { get; set; } = 0.05f;

    /// <summary>How many pulse cycles per second the glow completes. 1.2 by default - a calm, heartbeat-like rhythm rather than a rapid flicker.</summary>
    public float ThresholdEffectGlowPulseSpeed { get; set; } = 0.7f;

    /// <summary>How far the glow's outermost ring extends, as a multiple of the bottle's own width. 4.0 by default - substantially larger than an earlier version's fixed 1.6, per request for a much bigger, more noticeable glow.</summary>
    public float ThresholdEffectGlowSize { get; set; } = 10.0f;

    /// <summary>
    /// While true, the HUD gauge is fixed in place and click-through
    /// (won't intercept mouse input). Uncheck to drag it somewhere else
    /// in the settings window, then re-check to lock the new position in.
    /// </summary>
    public bool HudLocked { get; set; } = true;

    /// <summary>Screen-space pixel position of the HUD gauge's top-left corner.</summary>
    public float HudPositionX { get; set; } = 2403.0f;

    public float HudPositionY { get; set; } = 1173.0f;

    /// <summary>
    /// Uniform scale multiplier for the whole bottle-shaped HUD gauge
    /// (nipple, cap, and body all scale together from a fixed set of
    /// base dimensions) - replaces the old independent HudWidth/HudHeight,
    /// since a bottle needs specific proportions to actually read as a
    /// bottle rather than an arbitrary rectangle. 1.0 is the base size.
    /// </summary>
    public float HudScale { get; set; } = 1.15f;

    /// <summary>
    /// Text overlaid on the center of the bottle body (not stacked above
    /// it, no space reserved for it). Blank by default.
    /// </summary>
    public string HudLabelText { get; set; } = "";

    /// <summary>
    /// Font size multiplier for HudLabelText, independent of HudScale -
    /// applied via ImGui.SetWindowFontScale, which affects both the text
    /// measurement (for centering) and the actual rendered glyph size.
    /// </summary>
    public float HudLabelFontScale { get; set; } = 1.0f;

    /// <summary>
    /// <summary>
    /// The bottle's fill color for the lower zone (Minimum Scaling to
    /// Maximum Scaling Out of Combat) - the "milk" - as separate 0-1
    /// R/G/B components rather than a single struct, matching this
    /// file's existing style of plain float properties. Defaults to a
    /// creamy white rather than pure white, to actually read as milk.
    /// In Job mode this only fills the bottle's bottom zone; in Food/Mana
    /// mode (which have no analogous "baseline within range" split) it's
    /// the only fill color used across the whole body.
    /// </summary>
    public float HudFillColorR { get; set; } = 1.0f;

    public float HudFillColorG { get; set; } = 1.0f;

    public float HudFillColorB { get; set; } = 1.0f;

    /// <summary>
    /// Second fill color, used only in Job mode. Maps Gauge 2's own
    /// start/end (HudGauge2StartScale/HudGauge2EndScale) across the FULL
    /// bottle height (same coordinate space as HudFillColorR/G/B, not a
    /// separate stacked portion of it) and is drawn on top of gauge 1 -
    /// so once scaling reaches gauge 2's own start, this progressively
    /// covers over the first color instead of appending alongside it,
    /// like a Kingdom Hearts boss health bar. Defaults to a warm golden
    /// tone, distinct from the creamy white first color. Unused in
    /// Food/Mana mode.
    /// </summary>
    public float HudFillColor2R { get; set; } = 1.0f;

    public float HudFillColor2G { get; set; } = 0.58146966f;

    public float HudFillColor2B { get; set; } = 0.9665711f;

    /// <summary>
    /// Third fill color, used only in Job mode. Same idea as
    /// HudFillColor2R/G/B, but for gauge 3 (HudGauge3StartScale/
    /// HudGauge3EndScale) - drawn AFTER gauge 2, so it covers over BOTH
    /// gauge 1 and gauge 2 wherever it's filled. Defaults to a deep red,
    /// distinct from both other gauges' colors.
    /// </summary>
    public float HudFillColor3R { get; set; } = 1.0f;

    public float HudFillColor3G { get; set; } = 0.6230032f;

    public float HudFillColor3B { get; set; } = 0.69527096f;

    /// <summary>
    /// Each HUD gauge's start/end are fully independent of every other
    /// gauge (and of JobCombatFloorScale/JobBaselineScale/
    /// JobUpperLimitScale) - purely visual thresholds for this gauge
    /// display, independent of the actual growth/ceiling mechanics.
    /// All three gauges span the FULL bottle height using their own
    /// range (see HudGaugeWindow.DrawJobThreeZoneFill), so these can be
    /// set to overlap, leave a gap, or run in either
    /// direction - whatever's meaningful for the display. Gauge 1/2
    /// defaults (0.75/1.00/1.00/1.25) match this display's original
    /// two-gauge defaults exactly; gauge 3 defaults to 1.25/1.50 as a
    /// reasonable continuation of that same progression.
    /// </summary>
    public float HudGauge1StartScale { get; set; } = 0.7f;

    public float HudGauge1EndScale { get; set; } = 1.01f;

    public float HudGauge2StartScale { get; set; } = 1.01f;

    public float HudGauge2EndScale { get; set; } = 1.3f;

    public float HudGauge3StartScale { get; set; } = 0.1f;

    public float HudGauge3EndScale { get; set; } = 0.1f;

    /// <summary>
    /// Extra status names to treat as a food buff, in case the client's
    /// language doesn't use "Well Fed" or a food adds a differently-named
    /// status. Empty by default.
    /// </summary>
    public List<string> ExtraWellFedStatusNames { get; set; } = [];

    /// <summary>Configured emote-to-scale mappings for ScaleMode.Emote. Empty by default.</summary>
    public List<EmoteScaleTrigger> EmoteTriggers { get; set; } = [];

    private IDalamudPluginInterface? pluginInterface;

    public void Initialize(IDalamudPluginInterface pi) => pluginInterface = pi;

    public void Save() => pluginInterface?.SavePluginConfig(this);
}
