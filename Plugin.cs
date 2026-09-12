using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Command;
using Dalamud.Game.Gui.Dtr;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using System.Collections.Generic;
using System.Linq;

namespace MilkMeter;

/// <summary>
/// Entry point. Registered with Dalamud via DalamudPluginInterface.
/// Same shape as the original ManaMune (mana-based) plugin, but the
/// "how big is your chest right now" question can be answered by a food
/// buff timer, MP, or (see JobScale.cs) a job-relevant mechanic that
/// differs by role, instead of only reading MP.
///
/// MERGED FEATURE: waist/hunger scaling, previously its own standalone
/// plugin (Hunger Meter, by the same author). The two plugins running
/// side by side turned out to conflict with each other over Customize+ -
/// each independently pushed its own temporary profile (this one for
/// the chest bones, Hunger Meter for the waist bone), and Customize+'s
/// temporary-profile API appears to fully REPLACE the resolved bone set
/// while active rather than merge per-bone across different pushers -
/// so whichever plugin pushed most recently would silently blank out
/// the other's edit. Merging Hunger Meter's logic directly into this
/// plugin (WaistScale.cs, HungerSettingsWindow.cs, the waist-related
/// Configuration properties, and CustomizePlusIpc.SetScales pushing
/// both bones together in one call) fixes this at the root: one
/// process, one combined push, no second independent pusher to race
/// against. The waist/hunger feature keeps its own dedicated settings
/// window (opened via /hungermeter or /food, not /milkmeter or /milk)
/// and its own independent pause toggle (Configuration.WaistScalingPaused,
/// separate from Configuration.ScalingPaused which only affects
/// breast scaling) - per request, the two meters are otherwise
/// unrelated feature sets that happen to share a process (and, now, a
/// single combined DTR bar entry - see UpdateDtrBarEntry).
/// </summary>
public sealed class Plugin : IDalamudPlugin
{
    public string Name => "Milk Meter";

    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static ICondition Condition { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static IDtrBar DtrBar { get; private set; } = null!;

    private const string CommandName = "/milkmeter";
    private const string ShortCommandName = "/milk";
    private const string HungerCommandName = "/hungermeter";
    private const string FoodCommandName = "/food";
    private const string AssCommandName = "/ass";
    private const string TitsCommandName = "/tits";

    public Configuration Configuration { get; }
    private readonly CustomizePlusIpc customizePlus;
    private readonly FoodBuffTracker foodTracker;
    private readonly ManaTracker manaTracker;
    private readonly JobBuffTracker jobTracker;
    private readonly EmoteLoopTracker emoteLoopTracker;
    private readonly SettingsWindow settingsWindow;
    private readonly HungerSettingsWindow hungerSettingsWindow;
    private readonly HudGaugeWindow hudGauge;
    private readonly IDtrBarEntry dtrBarEntry;
    private int lastDtrBarPercent = -1;
    private int lastDtrBarWaistPercent = -1;
    private readonly HeartbeatSoundPlayer heartbeatSoundPlayer;
    private readonly MoanSoundPlayer moanSoundPlayer;
    private readonly BurpSoundPlayer burpSoundPlayer;
    private readonly ThresholdEffectOverlay thresholdEffectOverlay;

    // Separate from lastPushedScale (chest) - both are checked together
    // in the combined push at the end of OnFrameworkUpdate now that
    // CustomizePlusIpc.SetScales pushes both bones in one call. -1f is
    // the same "never pushed yet" sentinel lastPushedScale already uses.
    private float lastPushedWaistScale = -1f;

    // The waist scale actually pushed to Customize+, eased toward
    // Configuration.CurrentWaistScale (the target) every frame at
    // Configuration.WaistScaleTransitionRate - exact mirror of
    // currentAppliedScale's own relationship to the breast target, added
    // per request so waist changes animate rather than snapping. -1f is
    // the same "not initialized yet" sentinel currentAppliedScale uses;
    // first real tick starts it from neutral (1.0) so logging in
    // mid-buff eases in instead of popping.
    private float currentAppliedWaistScale = -1f;

    // Last Well Fed remaining-seconds reading, stashed each frame by
    // the waist block so UpdateDtrBarEntry() can compute the Food half's
    // percentage from it in remaining-time mode without re-polling the
    // status list. Null means no buff active (0%).
    private float? lastKnownWellFedRemainingSeconds;

    // Set by /food (or /ass) with a number while remaining-time mode is
    // on - overrides the buff-derived waist scale until the buff itself
    // next changes (appears, expires, or is refreshed by eating again),
    // at which point it clears and normal tracking resumes. Null means
    // no override active. Deliberately NOT persisted to config: it's a
    // deliberately short-lived, in-session thing, and persisting it
    // would resurrect exactly the cross-character-sharing problem
    // remaining-time mode otherwise avoids entirely.
    private float? waistManualOverrideScale;

    // Only push an update to Customize+ when the applied scale actually
    // changes by a meaningful amount, and at most several times a second
    // - IPC calls are not free, and Customize+ has to reapply the whole
    // skeleton edit every time we call it. Both are tuned tight enough
    // that the easing animation (see currentAppliedScale below) still
    // looks smooth rather than steppy.
    private const float MinScaleDelta = 0.0005f;
    private const double MinSecondsBetweenPushes = 0.1;

    private float lastPushedScale = -1f;
    private double lastPushTime;
    private bool lastFoodBuffActive;

    // Tracks each tracked ability's recast cooldown on/off-cooldown edge
    // (keyed by ability name, since a job can have more than one - e.g.
    // Warrior has both Provoke and Equilibrium), to detect the instant
    // any of them is used - that's the only thing that adds the overuse
    // bonus. bool? (not bool) so "never observed yet" is distinguishable
    // from "observed as not on cooldown" - without this, an ability
    // that's ALREADY on cooldown the very first time we check it (e.g.
    // used moments before this session started tracking, or before
    // switching to Job mode) would default to "wasn't on cooldown" and
    // falsely register as "just used", adding a bonus you never actually
    // triggered. The fix: skip the just-used check entirely on an
    // ability's first-ever observation, just record its baseline state.
    private readonly Dictionary<string, bool?> lastOnCooldownByAbility = new();

    // Elapsed-decrease tracker, one per tracked ability - a SECOND,
    // independent signal for "this ability was just used", alongside
    // lastOnCooldownByAbility's rising-edge check above. Needed for the
    // exact same reason the GCD detection needed it: pressing an
    // ability again at the precise instant its own cooldown ends can
    // mean OnCooldown never actually observes a false in between (this
    // polling never catches the brief moment it's genuinely off
    // cooldown), so the rising-edge check alone can silently miss a
    // press. RecastDetail.Elapsed resets to (near) 0 on every genuine
    // new use regardless, so a decrease in Elapsed while still on
    // cooldown is just as valid a "just used" signal as the rising edge
    // - either one counts, per-ability, same as the GCD fix.
    private readonly Dictionary<string, float?> lastElapsedByAbility = new();

    // Tracks the "unconscious" (dead) condition flag's edge, to detect
    // both the instant the player dies and the instant they're revived -
    // see Configuration.ResetJobScaleToCombatFloorOnDeath.
    private bool lastUnconscious;

    // Persistent "current job scale" value - floored at
    // JobCombatFloorScale (via ability use or damage-taken reduction);
    // growth only ever pushes it up toward whichever ceiling applies
    // (JobUpperLimitScale in combat, JobBaselineScale out of combat),
    // and never restores it downward on its own - entering/leaving
    // combat while already above the new ceiling leaves it right where
    // it is. Only meaningful while Mode == Job and the current job is
    // tracked; starts at 1.0.
    private float jobCurrentScale = 1.0f;

    // Tracks the player's HP frame-to-frame, to detect the instant it
    // decreases - IncreaseScaleOnDamageTaken keys off this decrease
    // event. Defaults to 0, which never causes a false
    // trigger on first observation: a real HP value is never less than
    // 0, so the first real reading always just records a baseline.
    private uint lastKnownHp;

    // Rate-limit timestamp for the damage trigger, governed by
    // Configuration.DamageTriggerCooldownSeconds. -1 sentinel means
    // "never fired yet", which always passes the cooldown check
    // regardless of its configured length.
    private double lastDamageTriggerTime = -1d;

    // Rate-limit timestamp for the jump trigger, governed by its own
    // dedicated Configuration.JumpTriggerCooldownSeconds - separate
    // from lastDamageTriggerTime above, since jump-spamming shouldn't
    // share a cooldown with damage ticks. -1 sentinel means "never
    // fired yet", which always passes the cooldown check regardless of
    // its configured length.
    private double lastJumpTriggerTime = -1d;

    // Rising-edge tracker for GCD detection - mirrors
    // lastOnCooldownByAbility's exact pattern (see JobBuffTracker's own
    // class doc comment), but for the single shared GCD recast group
    // rather than a per-job tracked ability name. Null means "no
    // observation yet" - the very first check just records a baseline
    // rather than counting as a rising edge, same reasoning as
    // lastOnCooldownByAbility.
    private bool? wasGcdOnCooldown;

    // Elapsed-decrease tracker - a SECOND, independent signal for "a new
    // GCD was just used", alongside wasGcdOnCooldown's rising-edge check
    // above. Needed because pressing the next GCD at the exact instant
    // the previous one comes off cooldown can mean OnCooldown never
    // actually observes a false in between - from this polling's
    // perspective it looks like one continuous on-cooldown period, so
    // the rising-edge check alone misses it. RecastDetail.Elapsed
    // resets to (near) 0 on every genuine new use regardless, so a
    // decrease in Elapsed while still on cooldown is just as valid a
    // "just used" signal as the rising edge is - either one counts.
    private float? lastGcdElapsed;

    // Repeats the milk burst (both screen-wide and bottle-local) once
    // per second for as long as /dazed's drain is actively running -
    // -1 sentinel means "never fired yet", so the very first tick of an
    // active drain fires immediately rather than waiting out the first
    // interval.
    private const double DazedBurstIntervalSeconds = 1.0;
    private double lastDazedBurstTime = -1d;

    // Mirror of DazedBurstIntervalSeconds/lastDazedBurstTime for
    // /water's own drain (Breast Feeding Drain) - a fully independent
    // repeating-burst timer, not shared with Dazed's, since the two
    // drains are mutually exclusive anyway (only one looping emote can
    // be active at once) but are otherwise entirely separate features.
    private const double WaterBurstIntervalSeconds = 1.0;
    private double lastWaterBurstTime = -1d;

    // "/milk moan" ramp state - see OnShortCommand and its handling
    // inside the Job-mode growth/drain block. Progress is tracked as an
    // accumulated elapsed-seconds float advanced by deltaSeconds each
    // tick this runs (NOT a wall-clock timestamp diff, unlike the
    // various burst-interval throttles elsewhere in this file) -
    // specifically so the ramp genuinely pauses along with everything
    // else while Configuration.ScalingPaused is true, rather than
    // jumping forward to "catch up" the instant it's unpaused.
    private bool moanRampActive;
    private float moanRampStartScale;
    private float moanRampElapsedSeconds;
    private const float MoanRampDurationSeconds = 20f;

    // Falling-edge tracker for the Self Sucking auto-attention-swap's
    // burp sound - true once scale has been observed at/below
    // DazedDrainFloorScale, reset to false the moment it's back above
    // (or /dazed stops being active). The burp fires only on the
    // transition into "at/below the floor," not on every tick it stays
    // there.
    private bool dazedAtOrBelowFloorLastCheck;

    // Timestamp of the most recent tick scale was observed strictly
    // above DazedDrainFloorScale - tracked unconditionally every tick,
    // regardless of Mode or whether /dazed is active, since scale can
    // be pushed above the floor by anything (passive growth, ability
    // use, etc.), not just /dazed stopping. Used by the burp trigger
    // below to distinguish "scale just NOW fell from above the floor"
    // (burp should play) from "scale was ALREADY at/below the floor
    // when /dazed started" (burp should NOT play) - a plain single-tick
    // falling-edge check alone couldn't tell these apart, since
    // dazedAtOrBelowFloorLastCheck resets to false every time /dazed
    // stops being active, so starting /dazed while already at/below the
    // floor looked identical to a genuine fresh drop.
    private double lastAboveDazedDrainFloorTime = -1d;

    // Vertical-velocity-based jump detection - replaced an earlier
    // rising-edge check on ConditionFlag.Jumping/Jumping61 alone, which
    // missed jumps performed immediately upon landing: if the flag never
    // actually toggles back to false between two chained jumps (which it
    // apparently doesn't, at least not reliably at this polling
    // resolution), a simple "was false, now true" check sees one
    // continuous airborne period instead of two separate jumps. Tracking
    // Y-velocity directly catches this instead - a genuinely fresh
    // upward impulse is detected the instant it starts, regardless of
    // whether the boolean flag ever dipped in between. jumpCountedForCurrentArc
    // resets the moment vertical velocity stops being positive (falling,
    // or the peak of the arc), so the NEXT upward impulse - even one
    // immediately following, with isJumping never having gone false -
    // still counts as its own jump. lastPlayerY is this block's own
    // dedicated position-tracking field, deliberately separate from the
    // Guard trigger's lastPlayerPosition further below, since that one
    // isn't guaranteed to be updated yet by the point this block runs
    // each tick. The velocity threshold itself is
    // Configuration.JumpVelocityThreshold - a GUESSED starting value,
    // not verified against any known FFXIV jump-physics constant, so
    // it's user-configurable and checkable via /milkmeter
    // jumpdebug.
    private float? lastPlayerY;
    private bool jumpCountedForCurrentArc;

    // Periodic re-send timer for the /attention swap itself (once per
    // second, matching DazedAttentionSwapIntervalSeconds below) - -1
    // sentinel means "never sent yet", so the first tick the floor is
    // reached fires immediately rather than waiting out the first
    // interval. Repeats for as long as the conditions hold, per
    // request, rather than firing just once - see the trigger block's
    // own comment for why.
    private const double DazedAttentionSwapIntervalSeconds = 1.0;
    private double lastDazedAttentionSwapTime = -1d;

    // Scheduled burp playback time (see SelfSuckingBurpDelaySeconds) -
    // null means nothing is pending. Checked every tick regardless of
    // whether the Self Sucking Threshold's own conditions still hold by
    // the time the delay elapses, since the burp is meant to accompany
    // the animation change that already happened, not the live
    // threshold state at playback time.
    private double? pendingBurpPlayTime;

    // Periodic re-trigger timer for the guard auto-trigger (once per
    // second, matching DazedBurstIntervalSeconds's own established
    // pattern below) - -1 sentinel means "never fired yet", so the very
    // first tick where conditions are met fires immediately rather than
    // waiting out the first interval. No longer a rising-edge flag (an
    // earlier version fired once and waited for conditions to reset) -
    // this now keeps re-forcing the emote once per second for as long
    // as both conditions hold, per request.
    private const double GuardTriggerIntervalSeconds = 1.0;
    private double lastGuardTriggerTime = -1d;

    // Frame-to-frame position tracking for the guard auto-trigger's
    // "standing still" condition - a new kind of check for this
    // project, but built on ObjectTable.LocalPlayer.Position, an
    // extremely standard, fundamental Dalamud game-object property
    // (similar confidence tier to CurrentHp, already used above for
    // damage-taken detection). "Standing still" means the position
    // hasn't moved more than PositionStillnessEpsilon for at least
    // PositionStillnessRequiredSeconds - a short grace window rather
    // than a single-frame check, so a genuinely brief pause mid-turn
    // doesn't register as "still" prematurely.
    private const float PositionStillnessEpsilon = 0.01f;
    private const float PositionStillnessRequiredSeconds = 0.5f;
    private System.Numerics.Vector3? lastPlayerPosition;
    private double lastPlayerMovementTime = -1d;

    // While true, passive growth (see JobScale.ApplyGrowth) is skipped
    // entirely - the job scale stays frozen exactly at whatever death
    // set it to. Set when ResetJobScaleToCombatFloorOnDeath fires, and
    // cleared the instant the player is no longer unconscious (revived),
    // regardless of whether that toggle is still on at that point - so a
    // freeze never gets stuck active even if the setting changes mid-death.
    private bool jobScaleFrozenUntilRevive;

    // Mirror of jobScaleFrozenUntilRevive above, for the waist meter -
    // independently tracked since WaistScalingPaused is already
    // independent of ScalingPaused, and the two meters' own death-reset
    // toggles are independently checkable too. Set/cleared in the SAME
    // death-check block as jobScaleFrozenUntilRevive (both key off the
    // same ConditionFlag.Unconscious edge, no need for a second
    // lastUnconscious tracker).
    private bool waistScaleFrozenUntilRevive;

    // Reintroduced per request - a flat, event-triggered bump on top of
    // the continuous growth/decay above, layered back in as an
    // ADDITIONAL mechanic rather than replacing the continuous one.
    // Unlike the original (pre-continuous-rewrite) version, which added
    // WaistIncreasePerFood directly and instantly, this now eases in
    // gradually - see pendingWaistFoodBumpAmount and
    // WaistFoodBumpEaseRatePerSecond below, and the waist block in
    // OnFrameworkUpdate for where it's actually applied.
    private bool lastFoodBuffActiveForBump;
    private float? lastRemainingSecondsForBump;

    // Accumulates whenever a food-consumed edge fires (see
    // OnFrameworkUpdate's waist block) and drains back down to 0 over
    // subsequent frames as it's gradually applied onto
    // Configuration.CurrentWaistScale, at most
    // WaistFoodBumpEaseRatePerSecond scale units per second - so eating
    // multiple times in quick succession (before a previous bump has
    // fully eased in) just adds to the queue rather than restarting or
    // being ignored, and the visible result is always a smooth ramp,
    // never an instant jump, regardless of how large
    // WaistFoodEatenBumpAmount is configured.
    private float pendingWaistFoodBumpAmount;

    // Not user-configurable, per request (only the bump AMOUNT itself
    // is meant to be a slider) - a fixed rate at which
    // pendingWaistFoodBumpAmount above gets applied. At this rate, the
    // default WaistFoodEatenBumpAmount (0.1) takes 5 seconds to fully
    // ease in; a larger configured bump takes proportionally longer,
    // not the same fixed duration regardless of size - mirrors how
    // Configuration.ScaleTransitionRate (a per-second max-step, not a
    // fixed total duration) already works for breast scale's own
    // easing, rather than the moan ramp's fixed-20-second-duration
    // approach, which is a poorer fit here since this needs to handle
    // an unbounded, possibly-repeatedly-added queue rather than a
    // single one-shot ramp to a specific target.
    private const float WaistFoodBumpEaseRatePerSecond = 0.02f;

    // The scale actually pushed to Customize+, eased toward
    // ComputeCurrentScale()'s target every frame rather than snapping to
    // it - this is what makes eating food, Provoke going on cooldown,
    // etc. all look gradual instead of instant. -1 is a sentinel meaning
    // "not yet initialized"; it starts easing in from neutral (1.0 =
    // baseline, unmodified) the first time it's set, so even logging in
    // mid-buff/mid-cooldown eases in rather than popping.
    private float currentAppliedScale = -1f;

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Configuration.Initialize(PluginInterface);

        // Seed the waist accumulator on first-ever run (or from a
        // pre-merge config that's never seen this field) from the
        // configured baseline - see Configuration.CurrentWaistScale's
        // own doc comment for why NaN (not a hardcoded 1.0f) is the
        // sentinel checked here. Mirrors the standalone Hunger Meter
        // plugin's own identical seeding step before it was merged in.
        if (float.IsNaN(Configuration.CurrentWaistScale))
        {
            Configuration.CurrentWaistScale = Configuration.WaistBaselineScale;
            Configuration.Save();
        }

        customizePlus = new CustomizePlusIpc(PluginInterface, Log);
        foodTracker = new FoodBuffTracker(ObjectTable, Configuration);
        manaTracker = new ManaTracker(ObjectTable);
        jobTracker = new JobBuffTracker(ObjectTable, DataManager, Log);
        emoteLoopTracker = new EmoteLoopTracker(ObjectTable);
        settingsWindow = new SettingsWindow(
            Configuration,
            ComputeCurrentScale,
            GetAppliedScale,
            () => foodTracker.GetFoodBuffState(),
            () => manaTracker.GetManaFraction(),
            jobTracker.GetTrackedAbilityDisplayName,
            () => (Condition[ConditionFlag.InCombat], jobCurrentScale));
        hungerSettingsWindow = new HungerSettingsWindow(
            Configuration,
            () => Configuration.CurrentWaistScale,
            () => lastPushedWaistScale,
            () => foodTracker.GetFoodBuffState(),
            ResetWaistToBaseline);
        hudGauge = new HudGaugeWindow(Configuration, GetAppliedScale, () => Condition[ConditionFlag.InCombat], ResetScaleToBaseline);
        heartbeatSoundPlayer = new HeartbeatSoundPlayer(Log);
        moanSoundPlayer = new MoanSoundPlayer(Log);
        burpSoundPlayer = new BurpSoundPlayer(Log);
        thresholdEffectOverlay = new ThresholdEffectOverlay(Configuration, heartbeatSoundPlayer, moanSoundPlayer);
        dtrBarEntry = DtrBar.Get("Milk Meter");
        dtrBarEntry.Shown = Configuration.ShowDtrBarEntry;

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "'/milkmeter' toggles on/off. 'status' prints current scale. "
                + "'config' opens the settings window. "
                + "'mode food', 'mode mana', or 'mode job' switches the scaling source. "
                + "'rebaseline' re-reads your current chest scale as the new baseline. "
                + "'dumpprofile' prints your active Customize+ profile's raw JSON to /xllog. "
                + "'jobdebug' prints diagnostic info for Job Buff mode's ability/cooldown lookup. "
                + "'emotedebug' prints your current Character.Mode/ModeParam - use while performing "
                + "/shakedrink, /dazed, /water, /attention, or /guard to find the ShakeDrinkEmoteModeParam/"
                + "DazedEmoteModeParam/WaterEmoteModeParam/AttentionEmoteModeParam/GuardEmoteModeParam values for precise "
                + "emote matching. 'guarddebug' breaks down the guard auto-trigger's two conditions "
                + "(scale threshold, standing-still) separately, plus when it'll next fire. "
                + "'gcddebug' shows the configured GCD recast group's live cooldown state - use it "
                + "while pressing different GCD spells/weaponskills to confirm or correct "
                + "GcdRecastGroup if GCD-based reduction doesn't seem to be firing. 'jumpdebug' shows "
                + "live Y-position/threshold info for tuning JumpVelocityThreshold if jumps are being "
                + "missed or over-triggered. 'dtrdebug' prints the server info bar entry's current "
                + "computed percentage, Shown state, and inputs, for diagnosing why it might not be "
                + "displaying. See also '/hungermeter' (or its short form '/food') for the separate "
                + "waist/hunger meter's own settings window.",
        });

        CommandManager.AddHandler(ShortCommandName, new CommandInfo(OnShortCommand)
        {
            HelpMessage = "'/milk' alone opens the settings window. 'minimum' eases the job scale toward "
                + "Minimum Scaling over time; 'maximum' eases it toward Maximum Scaling (Out of Combat) "
                + "over time - both read whatever those sliders are currently set to, and work regardless "
                + "of the currently active Scale Source. 'moan' plays the moan sound and ramps job scale "
                + "up to Maximum Scaling (In Combat) over 20 seconds. A plain number (e.g. '150') sets "
                + "breast scale to that PERCENTAGE, matching what the server info bar reads - 0% is "
                + "Minimum Scaling, 100% is Maximum Scaling (Out of Combat), 200% is Maximum Scaling "
                + "(In Combat). '/tits' is an alias for this same command.",
        });

        CommandManager.AddHandler(HungerCommandName, new CommandInfo(OnHungerCommand)
        {
            HelpMessage = "'/hungermeter' toggles the Food/Hunger settings window. 'status' prints the "
                + "current waist scale. 'reset' resets waist scale to Baseline. A plain number (e.g. "
                + "'300') sets waist scale to that PERCENTAGE, matching what the server info bar reads - "
                + "0% is Minimum Waist Scaling, 300% is Maximum. '/food' and '/ass' are aliases for this "
                + "same command. This is a SEPARATE meter "
                + "from breast scaling above - see /milkmeter for that one.",
        });

        CommandManager.AddHandler(FoodCommandName, new CommandInfo(OnHungerCommand)
        {
            HelpMessage = "Alias for /hungermeter.",
        });

        CommandManager.AddHandler(AssCommandName, new CommandInfo(OnHungerCommand)
        {
            HelpMessage = "Alias for /hungermeter - e.g. '/ass 300' sets waist to 300%.",
        });

        CommandManager.AddHandler(TitsCommandName, new CommandInfo(OnShortCommand)
        {
            HelpMessage = "Alias for /milk - e.g. '/tits 200' sets breast to 200%.",
        });

        Framework.Update += OnFrameworkUpdate;
        PluginInterface.UiBuilder.Draw += settingsWindow.Draw;
        PluginInterface.UiBuilder.Draw += hungerSettingsWindow.Draw;
        PluginInterface.UiBuilder.Draw += hudGauge.Draw;
        PluginInterface.UiBuilder.Draw += DrawThresholdEffect;
        PluginInterface.UiBuilder.OpenConfigUi += OnOpenConfigUi;
    }

    private void OnHungerCommand(string command, string args)
    {
        args = args.Trim();

        if (args.Equals("status", System.StringComparison.OrdinalIgnoreCase))
        {
            Log.Information($"[MilkMeter] Current waist scale: {Configuration.CurrentWaistScale:F3} " +
                $"(applied: {lastPushedWaistScale:F3})");
            return;
        }

        if (args.Equals("reset", System.StringComparison.OrdinalIgnoreCase))
        {
            ResetWaistToBaseline();
            return;
        }

        if (args.Equals("statuslist", System.StringComparison.OrdinalIgnoreCase))
        {
            // Dumps every currently active status name/remaining-time
            // pair to /xllog - this is exactly how "Rationing" (applied
            // by the Squadron Rationing Manual) was confirmed as the
            // real in-game status name, correcting an earlier guess of
            // "Meat and Mead" based on wiki descriptions of the item's
            // effect rather than the actual StatusList output - same
            // verification instinct as /milkmeter emotedebug for
            // ModeParam values, but generically useful for confirming
            // ANY status name this project might need to match against
            // in the future too.
            var player = ObjectTable.LocalPlayer;
            if (player is null)
            {
                Log.Information("[MilkMeter] No local player found.");
                return;
            }

            var lines = new System.Collections.Generic.List<string>();
            foreach (var status in player.StatusList)
            {
                var row = status.GameData.ValueNullable;
                var name = row?.Name.ExtractText();
                if (string.IsNullOrEmpty(name))
                    continue;
                lines.Add($"\"{name}\" - {status.RemainingTime:F0}s remaining");
            }

            Log.Information(lines.Count > 0
                ? $"[MilkMeter] Active statuses:\n{string.Join("\n", lines)}"
                : "[MilkMeter] No active statuses found.");
            return;
        }

        // Percent-based, per request - the number is a 0-300 DTR
        // percentage, not a raw scale value, so it matches exactly what
        // the server info bar reads: 0% is Minimum Waist Scaling, 300%
        // is Maximum, linear between (see WaistScale.PercentToScale).
        // Out-of-range input saturates at the ends rather than being
        // rejected.
        if (float.TryParse(args, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var requestedWaistPercent))
        {
            var newWaistScale = WaistScale.PercentToScale(
                requestedWaistPercent, Configuration.WaistMinScale, Configuration.WaistMaxScale);
            Configuration.CurrentWaistScale = newWaistScale;
            Configuration.LastUpdateUnixSeconds = NowUnixSeconds();
            Configuration.Save();

            // In remaining-time mode, also latch this as an override so
            // the next tick's recompute doesn't immediately discard it -
            // it holds until the buff itself next changes. Harmless to
            // set in accumulator mode too (nothing reads it there), but
            // scoped to the mode that actually needs it so the field
            // doesn't linger meaninglessly.
            if (Configuration.WaistUseRemainingTimeMode)
                waistManualOverrideScale = newWaistScale;

            var clampedPercent = System.Math.Clamp(requestedWaistPercent, 0f, 300f);
            Log.Information($"[MilkMeter] Setting waist to {clampedPercent:F0}% (scale {Configuration.CurrentWaistScale:F3}, " +
                $"requested {requestedWaistPercent:F0}%, clamped to 0-300%)." +
                (Configuration.WaistUseRemainingTimeMode
                    ? " Holding this value until Well Fed next changes (appears, expires, or is refreshed by eating)."
                    : string.Empty));
            return;
        }

        hungerSettingsWindow.IsOpen = !hungerSettingsWindow.IsOpen;
    }

    private void ResetWaistToBaseline()
    {
        Configuration.CurrentWaistScale = Configuration.WaistBaselineScale;
        Configuration.LastUpdateUnixSeconds = NowUnixSeconds();
        Configuration.Save();

        // Clear any manual /food override too - "reset" is an explicit
        // "put things back to normal" action, so it should hand control
        // back to buff tracking rather than leaving a stale override
        // pinning the value. Note this means, in remaining-time mode,
        // the Baseline value set above only lasts until the very next
        // tick recomputes from the buff - which is the correct behavior
        // for a reset in that mode, not a bug.
        waistManualOverrideScale = null;
    }

    private static double NowUnixSeconds() => System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;


    private void OnShortCommand(string command, string args)
    {
        args = args.Trim();

        if (args.Equals("minimum", System.StringComparison.OrdinalIgnoreCase))
        {
            // Just set the raw target - the existing outer easing system
            // (Configuration.ScaleTransitionRate) already moves the
            // visually-applied scale toward whatever jobCurrentScale is
            // every frame, so "over time" falls out naturally without
            // needing any new transition logic here. Works regardless of
            // the currently active Scale Source, same as the death/
            // damage checks - it just won't be visually apparent
            // unless Job mode is what's currently selected.
            jobCurrentScale = Configuration.JobCombatFloorScale;
            moanRampActive = false; // an explicit set cancels any in-progress moan ramp
            Log.Information($"[MilkMeter] Easing job scale toward Minimum Scaling ({Configuration.JobCombatFloorScale:F2}).");
            return;
        }

        if (args.Equals("maximum", System.StringComparison.OrdinalIgnoreCase))
        {
            jobCurrentScale = Configuration.JobBaselineScale;
            moanRampActive = false;
            Log.Information($"[MilkMeter] Easing job scale toward Maximum Scaling - Out of Combat ({Configuration.JobBaselineScale:F2}).");
            return;
        }

        if (args.Equals("moan", System.StringComparison.OrdinalIgnoreCase))
        {
            // Plays moan.wav once (a one-shot Play(), independent of
            // the threshold effect's own looping use of the same
            // player - see MoanSoundPlayer.Play()'s doc comment for the
            // one edge case that can overlap with), then starts a
            // 20-second ramp from wherever job scale currently sits up
            // to Maximum Scaling (In Combat) - see the moanRampActive
            // handling inside the Job-mode growth/drain block in
            // OnFrameworkUpdate for the actual per-frame lerp. Re-running
            // this command while already ramping restarts a fresh
            // 20-second ramp from the CURRENT scale rather than
            // stacking or being ignored.
            moanSoundPlayer.Play();
            moanRampActive = true;
            moanRampStartScale = jobCurrentScale;
            moanRampElapsedSeconds = 0f;
            Log.Information($"[MilkMeter] Playing moan sound and ramping job scale toward Maximum Scaling - In Combat ({Configuration.JobUpperLimitScale:F2}) over {MoanRampDurationSeconds:F0} seconds.");
            return;
        }

        // Percent-based, per request - the number is a 0-200 DTR
        // percentage, not a raw scale value, so it matches exactly what
        // the server info bar's Milk half reads: 0% is Minimum Scaling,
        // 100% is Maximum Scaling (Out of Combat), 200% is Maximum
        // Scaling (In Combat). ScalePercent.PercentToScale is the exact
        // inverse of the same two-segment mapping the bar itself uses,
        // so the two can't drift apart. Out-of-range input saturates at
        // the ends rather than being rejected. Cancels any in-progress
        // moan ramp, same as minimum/maximum above.
        if (float.TryParse(args, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var requestedPercent))
        {
            moanRampActive = false;
            jobCurrentScale = ScalePercent.PercentToScale(
                requestedPercent,
                Configuration.JobCombatFloorScale,
                Configuration.JobBaselineScale,
                Configuration.JobUpperLimitScale);

            var clampedMilkPercent = System.Math.Clamp(requestedPercent, 0f, 200f);
            Log.Information($"[MilkMeter] Setting breast to {clampedMilkPercent:F0}% (scale {jobCurrentScale:F3}, " +
                $"requested {requestedPercent:F0}%, clamped to 0-200%).");
            return;
        }

        settingsWindow.IsOpen = true;
    }

    private void OnOpenConfigUi() => settingsWindow.IsOpen = true;

    // Named method (not a lambda) specifically so the same delegate
    // reference can be used for both += and -= on UiBuilder.Draw - two
    // separately-written lambdas with identical bodies are NOT the same
    // delegate instance, so -= with a fresh lambda would silently fail
    // to unsubscribe the original one.
    private void DrawThresholdEffect() => thresholdEffectOverlay.Draw(GetAppliedScale(), Condition[ConditionFlag.InCombat]);

    private void OnCommand(string command, string args)
    {
        args = args.Trim();

        if (args.Equals("status", System.StringComparison.OrdinalIgnoreCase))
        {
            Log.Information($"[MilkMeter] Mode={Configuration.Mode}, " +
                $"target={ComputeCurrentScale():F3}, applied={GetAppliedScale():F3}");
            return;
        }

        if (args.Equals("dtrdebug", System.StringComparison.OrdinalIgnoreCase))
        {
            var milkPercent = ScalePercent.ComputeTwoSegmentPercent(
                GetAppliedScale(),
                Configuration.JobCombatFloorScale,
                Configuration.JobBaselineScale,
                Configuration.JobUpperLimitScale);
            var roundedMilkPercent = (int)System.Math.Round(milkPercent);

            var foodPercent = Configuration.WaistUseRemainingTimeMode
                ? WaistScale.ComputeRemainingTimePercent(lastKnownWellFedRemainingSeconds)
                : ScalePercent.ComputeTwoSegmentPercent(
                    Configuration.CurrentWaistScale,
                    Configuration.WaistMinScale,
                    Configuration.WaistBaselineScale,
                    Configuration.WaistMaxScale);
            var roundedFoodPercent = (int)System.Math.Round(foodPercent);

            Log.Information("[MilkMeter] DTR bar debug info (single combined entry, \"| Food: X% | Milk: Y% |\"):\n" +
                $"ShowDtrBarEntry: {Configuration.ShowDtrBarEntry}\n" +
                $"dtrBarEntry.Shown (actual current value read back from Dalamud): {dtrBarEntry.Shown}\n" +
                $"--- Milk (breast) half ---\n" +
                $"GetAppliedScale(): {GetAppliedScale():F3}\n" +
                $"JobCombatFloorScale (0%): {Configuration.JobCombatFloorScale:F2}, " +
                $"JobBaselineScale (100%): {Configuration.JobBaselineScale:F2}, " +
                $"JobUpperLimitScale (200%): {Configuration.JobUpperLimitScale:F2}\n" +
                $"Computed percent this instant: {milkPercent:F1} (rounded to {roundedMilkPercent}), " +
                $"lastDtrBarPercent (last one actually written): {lastDtrBarPercent}\n" +
                $"--- Food (waist) half ---\n" +
                $"Configuration.CurrentWaistScale: {Configuration.CurrentWaistScale:F3}\n" +
                $"WaistMinScale (0%): {Configuration.WaistMinScale:F2}, " +
                $"WaistBaselineScale (100%): {Configuration.WaistBaselineScale:F2}, " +
                $"WaistMaxScale (200%): {Configuration.WaistMaxScale:F2}\n" +
                $"Computed percent this instant: {foodPercent:F1} (rounded to {roundedFoodPercent}), " +
                $"lastDtrBarWaistPercent (last one actually written): {lastDtrBarWaistPercent}\n" +
                "If ShowDtrBarEntry/Shown are both true here but the bar still shows nothing in-game, " +
                "that points to a Dalamud-side display/registration issue (try a full game restart, not " +
                "just a plugin reload) rather than a computation problem on this end.");
            return;
        }

        if (args.Equals("config", System.StringComparison.OrdinalIgnoreCase))
        {
            settingsWindow.IsOpen = !settingsWindow.IsOpen;
            return;
        }

        if (args.Equals("rebaseline", System.StringComparison.OrdinalIgnoreCase))
        {
            if (ObjectTable.LocalPlayer is null)
            {
                Log.Information("[MilkMeter] No local player yet - try again once you're logged in.");
                return;
            }

            customizePlus.SetCharacterObjectIndex(ObjectTable.LocalPlayer.ObjectIndex);
            customizePlus.CaptureBaseline();
            lastPushedScale = -1f; // force an immediate re-push against the new baseline
            return;
        }

        if (args.Equals("dumpprofile", System.StringComparison.OrdinalIgnoreCase))
        {
            if (ObjectTable.LocalPlayer is not null)
                customizePlus.SetCharacterObjectIndex(ObjectTable.LocalPlayer.ObjectIndex);

            Log.Information($"[MilkMeter] Active profile JSON:\n{customizePlus.DumpActiveProfileJson()}");
            return;
        }

        if (args.Equals("jobdebug", System.StringComparison.OrdinalIgnoreCase))
        {
            Log.Information($"[MilkMeter] Job debug info:\n{jobTracker.GetDebugInfo()}\n" +
                $"In combat: {Condition[ConditionFlag.InCombat]}, current job scale: {jobCurrentScale:F3}");
            return;
        }

        if (args.Equals("emotedebug", System.StringComparison.OrdinalIgnoreCase))
        {
            Log.Information($"[MilkMeter] Emote debug info:\n{emoteLoopTracker.GetDebugInfo(Configuration)}");
            return;
        }

        if (args.Equals("guarddebug", System.StringComparison.OrdinalIgnoreCase))
        {
            var scaleAtOrAbove = jobCurrentScale >= Configuration.GuardThresholdScale;
            var secondsSinceMovement = lastPlayerMovementTime >= 0d ? ImGuiNowSeconds() - lastPlayerMovementTime : (double?)null;
            var standingStill = lastPlayerMovementTime >= 0d && secondsSinceMovement >= PositionStillnessRequiredSeconds;
            var charmedActive = emoteLoopTracker.IsCharmedActive(Configuration);
            var ballDanceActive = emoteLoopTracker.IsBallDanceActive(Configuration);
            var guardSuppressed = charmedActive || ballDanceActive;
            var inCombatNow = Condition[ConditionFlag.InCombat];
            var combatSuppressed = Configuration.GuardAutoTriggerOutOfCombatOnly && inCombatNow;
            var wouldTrigger = Configuration.GuardAutoTriggerEnabled && scaleAtOrAbove && standingStill && !guardSuppressed && !combatSuppressed;
            var secondsUntilNextFire = lastGuardTriggerTime < 0d
                ? 0d
                : System.Math.Max(0d, GuardTriggerIntervalSeconds - (ImGuiNowSeconds() - lastGuardTriggerTime));

            Log.Information("[MilkMeter] Guard auto-trigger debug info:\n" +
                $"GuardAutoTriggerEnabled: {Configuration.GuardAutoTriggerEnabled}\n" +
                $"Current job scale: {jobCurrentScale:F3}, GuardThresholdScale: {Configuration.GuardThresholdScale:F3}, at or above threshold: {scaleAtOrAbove}\n" +
                $"Seconds since last movement: {(secondsSinceMovement.HasValue ? secondsSinceMovement.Value.ToString("F2") : "never moved yet")}, " +
                $"required: {PositionStillnessRequiredSeconds:F1}, standing still: {standingStill}\n" +
                $"Charmed active: {charmedActive}, Ball Dance active: {ballDanceActive} (either one suppresses the trigger entirely)\n" +
                $"GuardAutoTriggerOutOfCombatOnly: {Configuration.GuardAutoTriggerOutOfCombatOnly}, currently in combat: {inCombatNow}, suppressed by this: {combatSuppressed}\n" +
                $"All conditions met (fires once per second while true, not a one-time trigger): {wouldTrigger}\n" +
                $"Seconds until next fire (if conditions stay met): {secondsUntilNextFire:F1}");
            return;
        }

        if (args.Equals("gcddebug", System.StringComparison.OrdinalIgnoreCase))
        {
            var (onCooldown, elapsed, total) = jobTracker.GetGcdCooldownState(Configuration.GcdRecastGroup);
            var activeGroups = jobTracker.ScanActiveRecastGroups();

            var activeGroupsText = activeGroups.Count == 0
                ? "(none currently active - press a GCD spell/weaponskill right before running this command)"
                : string.Join("\n", activeGroups.Select(g =>
                    $"  Group {g.Group}: Elapsed {g.Elapsed:F2}, Total {g.Total:F2}" +
                    (g.Total is >= 1.4f and <= 3.2f ? " <- GCD-length candidate" : "")));

            Log.Information("[MilkMeter] GCD debug info:\n" +
                $"Configured GcdRecastGroup: {Configuration.GcdRecastGroup}" +
                (Configuration.GcdRecastGroup == 57
                    ? " (confirmed correct - community documentation had suggested 58, but 57 is the actual value on this client)\n"
                    : " (57 is the confirmed-correct value on this client - community documentation had suggested 58, which was wrong; this is set to something else)\n") +
                $"OnCooldown: {onCooldown}, Elapsed: {(elapsed.HasValue ? elapsed.Value.ToString("F2") : "n/a")}, " +
                $"Total: {(total.HasValue ? total.Value.ToString("F2") : "n/a")}\n" +
                "To verify this is the right group: press several DIFFERENT GCD spells/weaponskills " +
                "(not oGCDs/abilities) in a row while re-running this command right after each one - " +
                "OnCooldown should flip to True and Elapsed should reset close to 0 every single time, " +
                "regardless of which specific GCD you pressed. If it doesn't change at all, or only " +
                "changes for one specific ability rather than every GCD, the group number is wrong.\n" +
                $"GcdReducesScaleEnabled: {Configuration.GcdReducesScaleEnabled}, " +
                $"GcdScaleReductionAmount: {Configuration.GcdScaleReductionAmount:F3}\n" +
                "Note: the actual reduction trigger in Plugin.cs no longer relies purely on this " +
                "OnCooldown rising edge - it also fires on Elapsed decreasing while already on " +
                "cooldown, to catch GCDs pressed at the exact instant the previous one comes off " +
                "cooldown (where OnCooldown may never observe a false in between).\n" +
                "All currently active recast groups (press a GCD right before running this to find " +
                "the real one - look for a Total around 1.5-3.0 seconds, adjusted by your skill/spell " +
                "speed, that goes active for EVERY different GCD you press, not just one specific " +
                "ability):\n" +
                activeGroupsText);
            return;
        }

        if (args.Equals("jumpdebug", System.StringComparison.OrdinalIgnoreCase))
        {
            var isJumping = Condition[ConditionFlag.Jumping] || Condition[ConditionFlag.Jumping61];
            var currentY = ObjectTable.LocalPlayer?.Position.Y;

            Log.Information("[MilkMeter] Jump debug info:\n" +
                $"isJumping (ConditionFlag.Jumping or Jumping61): {isJumping}\n" +
                $"Current Y position: {(currentY.HasValue ? currentY.Value.ToString("F3") : "n/a")}, " +
                $"lastPlayerY: {(lastPlayerY.HasValue ? lastPlayerY.Value.ToString("F3") : "never set yet")}\n" +
                $"JumpVelocityThreshold: {Configuration.JumpVelocityThreshold:F2} yalms/second\n" +
                $"jumpCountedForCurrentArc (true while still rising from an already-counted jump - " +
                $"prevents re-triggering every tick of the same upward arc): {jumpCountedForCurrentArc}\n" +
                "Run this command repeatedly while actually jumping (ideally spamming it, or once " +
                "right as you press the jump key) to see live Y-position changes - the velocity itself " +
                "isn't shown here since it depends on deltaSeconds between ticks, but a genuine jump " +
                "should show Y climbing steadily for several calls in a row right after isJumping " +
                "becomes true. If jumps aren't registering, try lowering JumpVelocityThreshold in " +
                "settings; if a single jump registers as several, or normal landing motion counts as " +
                "one, try raising it.");
            return;
        }

        if (args.StartsWith("mode", System.StringComparison.OrdinalIgnoreCase))
        {
            var modeArg = args.Length > 4 ? args[4..].Trim() : string.Empty;

            if (modeArg.Equals("food", System.StringComparison.OrdinalIgnoreCase))
                Configuration.Mode = ScaleMode.Food;
            else if (modeArg.Equals("mana", System.StringComparison.OrdinalIgnoreCase))
                Configuration.Mode = ScaleMode.Mana;
            else if (modeArg.Equals("job", System.StringComparison.OrdinalIgnoreCase))
                Configuration.Mode = ScaleMode.Job;
            else
            {
                Log.Information("[MilkMeter] Usage: /milkmeter mode food|mana|job");
                return;
            }

            Configuration.Save();
            lastPushedScale = -1f; // force an immediate re-push under the new mode
            Log.Information($"[MilkMeter] Mode set to {Configuration.Mode}");
            return;
        }

        Configuration.Enabled = !Configuration.Enabled;
        Configuration.Save();

        if (!Configuration.Enabled)
        {
            // Give the profile back its un-modified state instead of
            // freezing it at whatever scale it last had, and reset the
            // animation so re-enabling eases in fresh from neutral rather
            // than jumping back to wherever it left off. Affects BOTH
            // chest and waist now that RevertScales() removes the whole
            // combined temporary profile in one call - lastPushedWaistScale
            // is reset alongside lastPushedScale for the same reason.
            customizePlus.RevertScales();
            lastPushedScale = -1f;
            lastPushedWaistScale = -1f;
            currentAppliedScale = -1f;
            currentAppliedWaistScale = -1f;
            jobCurrentScale = Configuration.JobBaselineScale;
        }

        Log.Information($"[MilkMeter] {(Configuration.Enabled ? "Enabled" : "Disabled")}");
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        if (!Configuration.Enabled)
        {
            HideDtrBarEntry();
            return;
        }

        if (ObjectTable.LocalPlayer is null)
        {
            HideDtrBarEntry();
            return;
        }

        customizePlus.SetCharacterObjectIndex(ObjectTable.LocalPlayer.ObjectIndex);

        // --- Waist/hunger accumulator (merged from the standalone
        // Hunger Meter plugin) - deliberately placed here, BEFORE the
        // breast-scale ScalingPaused early-return further down, so
        // pausing breast scaling does NOT also freeze waist tracking;
        // per request, the two meters pause independently
        // (WaistScalingPaused below vs ScalingPaused further down).
        // Uses real wall-clock time (NowUnixSeconds), not
        // ImGuiNowSeconds()'s monotonic stopwatch, since decay/growth
        // needs to account for real time elapsed even across a full
        // game restart - see Configuration.LastUpdateUnixSeconds's own
        // doc comment.
        //
        // The continuous growth/decay mechanic here doesn't need
        // discrete "food consumed" event detection at all - it's purely
        // a function of Well Fed's current on/off state each frame.
        // While active, scale grows continuously toward WaistMaxScale
        // (WaistIncreasePerHourWhileWellFed); while inactive, it decays
        // continuously toward WaistMinScale (WaistReductionPerHour) -
        // the two are mutually exclusive every frame. A SEPARATE flat
        // per-food-eaten bump was reintroduced per a later request
        // (WaistFoodEatenBumpEnabled/WaistFoodEatenBumpAmount below,
        // eased in gradually rather than applied instantly) - that one
        // DOES need discrete edge detection, since "was food just
        // eaten" genuinely is a one-time event unlike the continuous
        // mechanic above.
        var nowUnix = NowUnixSeconds();
        var elapsedWaistSeconds = Configuration.LastUpdateUnixSeconds is { } lastUnix ? nowUnix - lastUnix : 0d;
        Configuration.LastUpdateUnixSeconds = nowUnix;

        // Read once, shared by BOTH the waist accumulator immediately
        // below AND the separate breast-scale WellFedBreastGrowthEnabled
        // mechanic further down this method (near the GCD/damage-taken/
        // jump modifiers) - same underlying Well Fed status either way,
        // just two independent consumers of it, so this is read only
        // once per frame rather than redundantly polling player.StatusList
        // twice.
        var (isWellFedActive, remainingSecondsForBump) = foodTracker.GetFoodBuffState();

        // Stashed for UpdateDtrBarEntry() further down, which needs the
        // same reading to compute the Food half's percentage in
        // remaining-time mode - saves polling the whole status list a
        // second time in the same frame just to re-read a value already
        // in hand here. Frozen at its last value while paused, which is
        // what we want: the bar should show whatever the (also frozen)
        // scale corresponds to, not keep ticking down.
        if (!Configuration.WaistScalingPaused && !waistScaleFrozenUntilRevive)
            lastKnownWellFedRemainingSeconds = remainingSecondsForBump;

        // Manual-override lifetime, per request: a /food (or /ass)
        // percentage set while remaining-time mode is on sticks until
        // the BUFF ITSELF next changes, rather than being overwritten
        // by the very next tick's recompute. "Changes" deliberately
        // means a discrete event - the buff appearing, expiring, or
        // being refreshed/extended by eating again - NOT the ordinary
        // second-by-second countdown, which would otherwise clear the
        // override instantly and defeat the whole point. Computed here,
        // before lastFoodBuffActiveForBump is updated further below, so
        // both edges are still available to compare against.
        var wellFedJustAppeared = isWellFedActive && !lastFoodBuffActiveForBump;
        var wellFedJustEnded = !isWellFedActive && lastFoodBuffActiveForBump;
        var wellFedJustRefreshed = isWellFedActive && lastFoodBuffActiveForBump
            && remainingSecondsForBump is { } rNow && lastRemainingSecondsForBump is { } rPrev && rNow > rPrev + 1f;

        if (waistManualOverrideScale is not null && (wellFedJustAppeared || wellFedJustEnded || wellFedJustRefreshed))
        {
            waistManualOverrideScale = null;
            Log.Information("[MilkMeter] Well Fed changed - clearing manual waist override, resuming buff tracking.");
        }

        // Updated unconditionally, AFTER the three edge comparisons
        // above have consumed the previous frame's values - deliberately
        // outside the accumulator-mode branch further below, since
        // remaining-time mode needs these edges too now (for the
        // override-clearing check just above). Leaving them inside that
        // branch, as they originally were, would have left them
        // permanently stale in remaining-time mode and silently broken
        // the override lifetime.
        lastFoodBuffActiveForBump = isWellFedActive;
        lastRemainingSecondsForBump = remainingSecondsForBump;

        // Remaining-time mode, per request - an entirely separate model
        // from the accumulator below, so it short-circuits past ALL of
        // it (growth/decay rates, constant decrease, food-eaten bump,
        // Rationing bonus, and the persisted-state catch-up above) and
        // just recomputes waist scale fresh from the live buff reading
        // every tick. Still respects WaistScalingPaused and the
        // death-freeze flag, so pausing genuinely freezes it at its last
        // value rather than continuing to track the buff - matching how
        // breast scale's own Food/Mana modes were made to behave while
        // paused. See WaistScale.ComputeFromRemainingTime and
        // Configuration.WaistUseRemainingTimeMode for the full reasoning,
        // including why this mode needs no persistence at all.
        if (Configuration.WaistUseRemainingTimeMode)
        {
            if (!Configuration.WaistScalingPaused && !waistScaleFrozenUntilRevive)
            {
                // A manual /food (or /ass) percentage takes precedence
                // over the buff-derived value for as long as it's
                // active - see the override-clearing block above for
                // exactly when it stops applying.
                Configuration.CurrentWaistScale = waistManualOverrideScale
                    ?? WaistScale.ComputeFromRemainingTime(
                        remainingSecondsForBump,
                        Configuration.WaistMinScale,
                        Configuration.WaistMaxScale);
            }
        }
        else
        {

        // Reintroduced food-consumed edge detection, per request -
        // scoped specifically to the flat-bump feature below (the
        // continuous growth/decay above doesn't need it, since it's
        // purely a function of Well Fed's current on/off state, not a
        // discrete event). Same two-edges-count logic as the original
        // pre-continuous-rewrite version: the buff transitioning from
        // not-active to active (first bite), or the buff already being
        // active and RemainingSeconds jumping UP compared to last frame
        // (eating a second item while still buffed, refreshing/
        // extending the timer) - remaining time only ever counts down
        // on its own, so any rise can only mean a new food item was
        // just consumed.
        var foodConsumedEdge = (isWellFedActive && !lastFoodBuffActiveForBump)
            || (isWellFedActive && lastFoodBuffActiveForBump
                && remainingSecondsForBump is { } r && lastRemainingSecondsForBump is { } lastR && r > lastR + 1f);

        if (Configuration.WaistFoodEatenBumpEnabled && foodConsumedEdge)
        {
            // Squadron Rationing Manual bonus, per request - checked
            // only at the instant of the edge itself (see
            // Configuration.WaistRationingManualBumpBonusEnabled's own
            // doc comment for why that's a deliberate choice, not a
            // limitation). IsRationingActive() is a separate query
            // from the isWellFedActive read above - Rationing and
            // Well Fed are entirely independent statuses.
            var bumpAmount = Configuration.WaistFoodEatenBumpAmount;
            if (Configuration.WaistRationingManualBumpBonusEnabled && foodTracker.IsRationingActive())
                bumpAmount *= Configuration.WaistRationingManualBumpMultiplier;

            pendingWaistFoodBumpAmount += bumpAmount;
        }

        if (!Configuration.WaistScalingPaused && !waistScaleFrozenUntilRevive && elapsedWaistSeconds > 0d)
        {
            Configuration.CurrentWaistScale = isWellFedActive
                ? WaistScale.ApplyGrowth(
                    Configuration.CurrentWaistScale,
                    Configuration.WaistMaxScale,
                    Configuration.WaistIncreasePerHourWhileWellFed,
                    elapsedWaistSeconds)
                : WaistScale.ApplyDecay(
                    Configuration.CurrentWaistScale,
                    Configuration.WaistMinScale,
                    Configuration.WaistReductionPerHour,
                    elapsedWaistSeconds);

            // Constant decrease, per request - UNLIKE WaistReductionPerHour
            // above (which only applies while Well Fed is NOT active,
            // mutually exclusive with growth), this applies every frame
            // regardless of Well Fed state, layered additively on top of
            // whichever of growth/decay just happened above. While Well
            // Fed is active, this partially offsets the growth; while
            // it's not, this compounds with the existing decay (both
            // targeting the same WaistMinScale floor, so it just gets
            // there faster, never past it). Same WaistScale.ApplyDecay
            // function as the mutually-exclusive decay above - the only
            // difference is WHEN it's allowed to run.
            if (Configuration.WaistConstantDecreaseEnabled)
            {
                Configuration.CurrentWaistScale = WaistScale.ApplyDecay(
                    Configuration.CurrentWaistScale,
                    Configuration.WaistMinScale,
                    Configuration.WaistConstantDecreasePerHour,
                    elapsedWaistSeconds);
            }

            // Gradually apply whatever's still pending from the flat
            // bump above, ADDITIVE on top of the continuous growth/decay
            // that just happened this same frame - a genuinely separate,
            // event-triggered mechanic layered on top of the
            // state-based one, not a replacement for it. Clamped the
            // same as everything else that writes to CurrentWaistScale.
            if (pendingWaistFoodBumpAmount > 0f)
            {
                var step = System.Math.Min(pendingWaistFoodBumpAmount, WaistFoodBumpEaseRatePerSecond * (float)elapsedWaistSeconds);
                Configuration.CurrentWaistScale = WaistScale.Clamp(
                    Configuration.CurrentWaistScale + step, Configuration.WaistMinScale, Configuration.WaistMaxScale);
                pendingWaistFoodBumpAmount -= step;
            }
        }

        } // end of accumulator-mode "else" - see WaistUseRemainingTimeMode's branch above

        if (!Configuration.WaistScalingPaused && !waistScaleFrozenUntilRevive)
        {
            // Defensive re-clamp in case Min/Max were edited at runtime
            // to a range that no longer contains the current value. Also
            // skipped while frozen for death, same reasoning as the
            // decay/growth block above - the whole point of the freeze
            // is that CurrentWaistScale stays EXACTLY at whatever death
            // set it to, and even a defensive re-clamp could move it if
            // Min/Max were edited to a range that no longer contains
            // that exact death-reset value. Applies in BOTH modes -
            // remaining-time mode already clamps internally, so this is
            // purely belt-and-braces there rather than load-bearing.
            Configuration.CurrentWaistScale = WaistScale.Clamp(
                Configuration.CurrentWaistScale, Configuration.WaistMinScale, Configuration.WaistMaxScale);
        }

        // Tracked unconditionally, every tick, regardless of Mode or
        // whether /dazed is active - see lastAboveDazedDrainFloorTime's
        // own doc comment above for why this needs to be tracked this
        // broadly rather than only within the dazed-specific block
        // further down.
        if (jobCurrentScale > Configuration.DazedDrainFloorScale)
            lastAboveDazedDrainFloorTime = ImGuiNowSeconds();

        // Fires the burp once its scheduled delay elapses, regardless
        // of Mode, freeze state, or whether the Self Sucking Threshold's
        // own conditions still hold by then - the burp is meant to
        // accompany the animation change that already happened, not
        // the live threshold state at playback time.
        if (pendingBurpPlayTime is { } scheduledTime && ImGuiNowSeconds() >= scheduledTime)
        {
            pendingBurpPlayTime = null;
            try
            {
                burpSoundPlayer.Play();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[MilkMeter] Self Sucking Threshold burp sound failed to play.");
            }
        }

        // Scaling Paused, per a later request, no longer an early return
        // here at all - it used to cover everything below (passive
        // growth, ability-use/damage-taken/jump/dazed/guard mechanics,
        // the ease-toward-target animation, AND the Customize+ push
        // itself), but that meant even a manual command
        // (/milk <number>, /food <number>, etc.) writing directly to
        // jobCurrentScale/Configuration.CurrentWaistScale never actually
        // became visible while paused, since the easing/push code that
        // would reflect it never ran either. The actual gating now
        // happens further below, scoped specifically to the AUTOMATIC
        // mechanics (see that block's own comment for the full
        // reasoning) - the easing/push/DTR-update code always runs
        // every frame regardless of this flag now, which is what makes
        // a manual command's effect actually visible while paused.
        // Toggled by left-clicking the HUD gauge itself (see
        // HudGaugeWindow.Draw()) or the settings window checkbox -
        // unlike Configuration.Enabled, this deliberately leaves the HUD
        // gauge and its particle effects still rendering (frozen at
        // whatever scale was applied at the moment of pausing, dimmed to
        // 10% opacity rather than drawing a red X over it), rather than
        // disabling the plugin's visible presence entirely. The
        // threshold effect (vignette/glow/heartbeat sound) is the one
        // exception - it completely stops the instant this is true, per
        // request, rather than continuing to evaluate against the frozen
        // scale value (see ThresholdEffectOverlay.Draw() and
        // HudGaugeWindow's glow block, which each check this
        // independently, since they're driven by the separate UI-draw
        // callback rather than this method).

        var deltaSeconds = (float)framework.UpdateDelta.TotalSeconds;

        // Detect the food buff appearing/disappearing, or the tracked job
        // mechanic's state changing, so we can bypass the push throttle
        // below and let the ease-in/out begin immediately rather than
        // waiting out MinSecondsBetweenPushes. (Food buff removal looks
        // the same to us whether it expired naturally or was clicked off
        // manually - the status is simply no longer present either way,
        // so no separate handling is needed for that case.)
        var forceImmediate = false;

        // Per request: "Scaling Paused" should block every AUTOMATIC
        // mechanic (death-reset, damage-taken, GCD, jump, well-fed
        // breast growth, and the whole Food/Mana/Job passive
        // computation below) while still letting manual commands
        // (/milk <number>, /milk minimum/maximum/moan, /food <number>,
        // /food reset) take effect - those write jobCurrentScale/
        // Configuration.CurrentWaistScale directly from their own
        // command handlers, entirely independent of this method, so
        // skipping this block doesn't block them; it only blocks the
        // automatic recomputation that would otherwise fight with
        // whatever a manual command just set. Everything from here
        // down to targetScale's own computation further below is
        // wrapped in this single check - the easing/push/DTR-update
        // code AFTER that point still runs unconditionally every frame
        // regardless of this flag, which is specifically what makes a
        // manual command's effect actually visible while paused: previously,
        // this whole method returned early the instant ScalingPaused was
        // true, which meant even a direct write from a manual command
        // never got eased toward or pushed to Customize+ until unpaused.
        if (!Configuration.ScalingPaused)
        {

        // Death reset check runs every frame regardless of Mode, so a
        // death that happens while on Food/Mana mode still resets the
        // job scale in the background rather than only while actively
        // viewing Job mode. ConditionFlag.Unconscious is the standard
        // Dalamud flag for "in the dead/ghost-camera state" - this is
        // the one piece of this feature not independently re-verified
        // against current API docs the way ConditionFlag.InCombat was
        // earlier in this project, though it's well-established
        // community usage for death detection.
        //
        // Handles BOTH meters' death-reset toggles in one place, since
        // both key off the same Unconscious edge - breast scale
        // (ResetJobScaleToCombatFloorOnDeath / ResetScaleToBaselineOnDeath,
        // Minimum takes priority if both are checked) and waist scale
        // (ResetWaistScaleToMinimumOnDeath / ResetWaistScaleToBaselineOnDeath,
        // same Minimum-wins precedence), fully independently - either
        // meter's toggles can be on, off, or set to either target
        // without affecting the other.
        //
        // The revival-clear (falling edge of unconscious) runs
        // regardless of whether any of the four toggles are still on,
        // so an active freeze always gets released on revive even if
        // the setting changed mid-death - it never gets stuck. Each
        // meter's own frozen flag clears independently too, in case one
        // meter revives its freeze before the other somehow would
        // (they can't currently, since both key off the same player's
        // same Unconscious state, but this keeps them from being
        // needlessly coupled to each other's flag).
        {
            var unconscious = Condition[ConditionFlag.Unconscious];
            if (unconscious && !lastUnconscious)
            {
                if (Configuration.ResetJobScaleToCombatFloorOnDeath)
                {
                    jobCurrentScale = Configuration.JobCombatFloorScale;
                    jobScaleFrozenUntilRevive = true;
                    forceImmediate = true;
                }
                else if (Configuration.ResetScaleToBaselineOnDeath)
                {
                    jobCurrentScale = Configuration.JobBaselineScale;
                    jobScaleFrozenUntilRevive = true;
                    forceImmediate = true;
                }

                if (Configuration.ResetWaistScaleToMinimumOnDeath)
                {
                    Configuration.CurrentWaistScale = Configuration.WaistMinScale;
                    waistScaleFrozenUntilRevive = true;
                }
                else if (Configuration.ResetWaistScaleToBaselineOnDeath)
                {
                    Configuration.CurrentWaistScale = Configuration.WaistBaselineScale;
                    waistScaleFrozenUntilRevive = true;
                }
            }
            else if (!unconscious && lastUnconscious)
            {
                if (jobScaleFrozenUntilRevive)
                    jobScaleFrozenUntilRevive = false; // revived - passive regen resumes next tick

                if (waistScaleFrozenUntilRevive)
                    waistScaleFrozenUntilRevive = false; // revived - decay/growth resumes next tick
            }
            lastUnconscious = unconscious;
        }

        // Damage-taken check, also unconditional of Mode. HP is read
        // directly off ObjectTable.LocalPlayer (already null-checked
        // above) - this is the first time this codebase has watched HP
        // specifically, though CurrentHp/MaxHp on player/battle-chara
        // objects is about as fundamental and stable a Dalamud property
        // as they come, similar confidence to ObjectTable.LocalPlayer
        // itself. Any decrease counts as "took damage", including DoT
        // ticks - but only while actually in combat; the baseline
        // (lastKnownHp) still updates every frame regardless of combat
        // state, so it never goes stale and can't cause a false trigger
        // the instant combat starts.
        if (Configuration.IncreaseScaleOnDamageTaken)
        {
            var currentHp = ObjectTable.LocalPlayer!.CurrentHp;
            if (lastKnownHp != 0 && currentHp < lastKnownHp && Condition[ConditionFlag.InCombat])
            {
                var triggerNow = ImGuiNowSeconds();
                if (triggerNow - lastDamageTriggerTime >= Configuration.DamageTriggerCooldownSeconds)
                {
                    // Capped/floored at whatever range currently
                    // applies (context-dependent by combat state,
                    // same as passive growth's own ceiling) - Clamp
                    // rather than a plain Min, since
                    // DamageTakenScaleIncrease can now be negative
                    // (Damage Taken Affects Scale, not just
                    // "Increases") - a negative amount needs the LOWER
                    // bound enforced too, not just the upper one Min
                    // alone would have handled for the positive case.
                    var ceiling = Condition[ConditionFlag.InCombat]
                        ? Configuration.JobUpperLimitScale
                        : Configuration.JobBaselineScale;
                    jobCurrentScale = System.Math.Clamp(
                        jobCurrentScale + Configuration.DamageTakenScaleIncrease,
                        Configuration.JobCombatFloorScale,
                        ceiling);

                    // Particle burst only for the reducing case
                    // (negative DamageTakenScaleIncrease) - matches
                    // every other genuine reduction in this file
                    // (GCD, ability-use, dazed-drain) getting the
                    // same "emptying the gauge" payoff. The raising
                    // case (positive amount, the original "Increases"
                    // behavior) still just wakes the gauge from idle,
                    // same as before - no burst, since that's
                    // deliberately reserved for the empty-the-gauge
                    // moment, not a fill-it-up one.
                    if (Configuration.DamageTakenScaleIncrease < 0f)
                        hudGauge.Trigger();
                    else
                        hudGauge.WakeFromIdle();

                    forceImmediate = true;
                    lastDamageTriggerTime = triggerNow;
                }
            }
            lastKnownHp = currentHp;
        }

        // GCD-reduces-scale: the PRIMARY mechanic for fighting the scale
        // back down, per request - on by default, unlike every other
        // scale-modifying toggle in this file. Fires on EITHER of two
        // independent signals (see lastGcdElapsed's own doc comment for
        // why both are needed): a rising edge of the shared GCD recast
        // group going on cooldown (mirroring lastOnCooldownByAbility's
        // exact per-ability pattern above, but for the single group
        // every spell/weaponskill shares), OR Elapsed decreasing while
        // already on cooldown (catches back-to-back GCDs pressed at the
        // exact instant the previous one ends, where OnCooldown may
        // never actually observe a false in between). Mode-
        // agnostic AND combat-agnostic (unlike the damage-taken check
        // above) - a GCD is a GCD whether in combat or not, per request.
        // Clamped between JobCombatFloorScale and JobUpperLimitScale,
        // since GcdScaleReductionAmount can be either sign (GCD
        // Affects Scale, not just "Reduces") - added directly (positive
        // raises scale, negative lowers it), matching the exact same
        // sign convention DamageTakenScaleIncrease/JumpScaleIncreaseAmount
        // use, rather than the subtraction this originally used before
        // that convention was standardized across all three toggles per
        // request. Only fires the particle burst those other
        // reductions get when it's actually lowering the scale (a
        // negative amount) - a positive amount just wakes the gauge
        // from idle instead, same as the raising direction of every
        // other bidirectional toggle.
        if (Configuration.GcdReducesScaleEnabled)
        {
            var (gcdOnCooldown, gcdElapsed, _) = jobTracker.GetGcdCooldownState(Configuration.GcdRecastGroup);

            var risingEdge = wasGcdOnCooldown.HasValue && gcdOnCooldown && !wasGcdOnCooldown.Value;
            var elapsedReset = gcdOnCooldown && gcdElapsed.HasValue && lastGcdElapsed.HasValue && gcdElapsed.Value < lastGcdElapsed.Value;

            if (risingEdge || elapsedReset)
            {
                jobCurrentScale = System.Math.Clamp(
                    jobCurrentScale + Configuration.GcdScaleReductionAmount,
                    Configuration.JobCombatFloorScale,
                    Configuration.JobUpperLimitScale);

                if (Configuration.GcdScaleReductionAmount < 0f)
                    hudGauge.Trigger();
                else
                    hudGauge.WakeFromIdle();

                forceImmediate = true;
            }

            wasGcdOnCooldown = gcdOnCooldown;
            lastGcdElapsed = gcdElapsed;
        }

        // Well Fed breast growth - a SEPARATE, independent mechanic from
        // both the Food Scale Source (which taper-computes a value fresh
        // every frame from remaining time) and the waist/hunger meter's
        // own Well Fed growth above; this one instead nudges
        // jobCurrentScale itself, the same variable GCD/damage-taken/
        // jump all modify, per request. Mode-agnostic like those (only
        // visually apparent while Job mode is the active Scale Source),
        // and reuses the SAME isWellFedActive read from near the top of
        // this method rather than polling the buff a second time.
        // Capped at JobUpperLimitScale (Maximum Scaling In Combat)
        // always, per request - not JobBaselineScale out of combat,
        // matching Jump's own ceiling choice above rather than the
        // damage-taken toggle's context-dependent one. WellFedBreastIncreasePerHour
        // is a per-HOUR rate (matching the waist meter's own rate
        // sliders), so it's divided down to per-second here before
        // calling JobScale.ApplyGrowth, which expects growthPerSecond.
        // Off by default - opt-in, like every other bidirectional/growth
        // toggle in this file except GCD.
        if (Configuration.WellFedBreastGrowthEnabled && isWellFedActive)
        {
            jobCurrentScale = JobScale.ApplyGrowth(
                jobCurrentScale,
                Configuration.JobUpperLimitScale,
                Configuration.WellFedBreastIncreasePerHour / 3600f,
                deltaSeconds);
            hudGauge.WakeFromIdle();
        }

        // Jump-increases-scale: detects a fresh upward vertical-velocity
        // impulse while airborne, rather than a plain rising edge of
        // ConditionFlag.Jumping/Jumping61 - see JumpVelocityThreshold's
        // own doc comment in Configuration.cs for why the plain
        // rising-edge version missed jumps performed immediately upon
        // landing. jumpCountedForCurrentArc resets the instant vertical
        // velocity stops being positive (falling, or the arc's peak), so
        // the next upward impulse - even one immediately following, with
        // isJumping never having gone false in between - still counts as
        // its own jump. Mode-agnostic like the damage-taken checks
        // above, for the same reason (jobCurrentScale persists in the
        // background regardless of Mode). Capped at JobUpperLimitScale
        // (Maximum Scaling In Combat) always - unlike the damage-taken
        // increase variant, this doesn't switch to JobBaselineScale out
        // of combat, per request. Rate-limited by its own dedicated
        // JumpTriggerCooldownSeconds, separate from
        // DamageTriggerCooldownSeconds - jump-spamming shouldn't be
        // throttled by the same cooldown governing damage ticks.
        if (Configuration.JumpIncreasesScaleEnabled)
        {
            var isJumping = Condition[ConditionFlag.Jumping] || Condition[ConditionFlag.Jumping61];
            var currentY = ObjectTable.LocalPlayer?.Position.Y;

            if (isJumping && currentY.HasValue)
            {
                var yVelocity = lastPlayerY.HasValue && deltaSeconds > 0f
                    ? (currentY.Value - lastPlayerY.Value) / deltaSeconds
                    : 0f;

                if (yVelocity > Configuration.JumpVelocityThreshold && !jumpCountedForCurrentArc)
                {
                    var jumpTriggerNow = ImGuiNowSeconds();
                    if (jumpTriggerNow - lastJumpTriggerTime >= Configuration.JumpTriggerCooldownSeconds)
                    {
                        // Clamp, not a plain Min - see the matching
                        // comment on the damage-taken variant above
                        // for why: JumpScaleIncreaseAmount can now be
                        // negative (Jumping Affects Scale, not just
                        // "Increases"), which needs the lower bound
                        // enforced too.
                        jobCurrentScale = System.Math.Clamp(
                            jobCurrentScale + Configuration.JumpScaleIncreaseAmount,
                            Configuration.JobCombatFloorScale,
                            Configuration.JobUpperLimitScale);

                        // Particle burst only for the reducing case,
                        // same reasoning as the damage-taken variant
                        // above - jumping to raise scale just wakes
                        // the gauge from idle like it always did.
                        if (Configuration.JumpScaleIncreaseAmount < 0f)
                            hudGauge.Trigger();
                        else
                            hudGauge.WakeFromIdle();

                        lastJumpTriggerTime = jumpTriggerNow;
                    }

                    jumpCountedForCurrentArc = true;
                }
                else if (yVelocity <= 0f)
                {
                    jumpCountedForCurrentArc = false;
                }
            }
            else
            {
                jumpCountedForCurrentArc = false; // fully grounded
            }

            if (currentY.HasValue)
                lastPlayerY = currentY;
        }

        // /attention - purely wakes the HUD gauge from its idle fade,
        // doesn't touch the job scale at all. Mode-agnostic (unlike
        // shakedrink/dazed, which are Job-mode-specific mechanics) since
        // the gauge itself is visible in every mode - this exists
        // specifically so you can "check the gauge's status" without
        // needing to use an ability or take damage first. Cheap enough
        // to call every frame it's active, no throttling needed.
        if (Configuration.AttentionWakeEnabled && emoteLoopTracker.IsAttentionActive(Configuration))
            hudGauge.WakeFromIdle();

        // /guard (used in place of /guard) - mirrors the /attention
        // wake-check exactly, whether it's currently active because
        // the auto-trigger forced it or because it's being performed
        // manually.
        if (Configuration.GuardWakeEnabled && emoteLoopTracker.IsGuardActive(Configuration))
            hudGauge.WakeFromIdle();

        // Scale-threshold wake: same cheap every-frame WakeFromIdle()
        // pattern as /attention and /guard above, just driven by the
        // applied scale value crossing a configured threshold instead
        // of an emote. Continuously re-wakes for as long as scale
        // stays at or above HudShowAboveScaleThreshold, so the gauge
        // stays visible (or reappears if already faded) the whole time
        // - once scale drops back below, the normal idle timer resumes
        // counting down from that point like any other wake.
        if (Configuration.HudShowAboveScaleEnabled && GetAppliedScale() >= Configuration.HudShowAboveScaleThreshold)
            hudGauge.WakeFromIdle();

        // Self Sucking auto-attention-swap + burp: while /dazed's drain
        // is active AND scale is at/below DazedDrainFloorScale (the
        // SAME floor the drain mechanic itself is capped at - this used
        // to be a separate SelfSuckingThreshold value, unified here per
        // request since scale literally can't drop below
        // DazedDrainFloorScale via the drain alone, so a separate,
        // potentially-inconsistent threshold made little practical
        // sense - SelfSuckingThreshold has been removed entirely).
        // Deliberately mode-agnostic and freeze-agnostic, same reasoning
        // as always: gating this inside Job-mode logic could leave state
        // stale across a mode switch or death.
        //
        // The /attention swap now repeats once per second for as long
        // as both conditions hold, rather than firing once - per
        // request, /dazed should ALWAYS swap to /attention while at the
        // floor, not just attempt it once. This is also a defensive
        // measure against something outside this plugin's control: it's
        // not confirmed whether the game actually allows switching
        // directly from one already-active looping emote to another via
        // a single command injection, or whether there's some brief
        // window where it's rejected - repeating gives it more chances
        // to actually take effect rather than trying exactly once.
        //
        // The burp, however, only fires on a GENUINE falling edge -
        // scale was observed strictly above the floor within the last
        // few seconds (see SelfSuckingBurpRecentAboveFloorWindowSeconds),
        // AND is at/below it now - not on every repeated swap attempt,
        // and NOT just because this is the first tick /dazed happened
        // to be checked while already sitting at/below the floor (e.g.
        // /dazed starting while scale was already down there from an
        // earlier session - a plain single-tick falling-edge check
        // couldn't tell that apart from a real fresh drop, since
        // dazedAtOrBelowFloorLastCheck resets every time /dazed stops
        // being active).
        {
            var dazedActiveForThreshold = Configuration.DazedDrainBoostEnabled && emoteLoopTracker.IsDazedActive(Configuration);
            var atOrBelowFloor = jobCurrentScale <= Configuration.DazedDrainFloorScale;

            if (dazedActiveForThreshold && atOrBelowFloor)
            {
                if (!dazedAtOrBelowFloorLastCheck)
                {
                    var wasRecentlyAboveFloor = lastAboveDazedDrainFloorTime >= 0d
                        && ImGuiNowSeconds() - lastAboveDazedDrainFloorTime <= Configuration.SelfSuckingBurpRecentAboveFloorWindowSeconds;

                    if (wasRecentlyAboveFloor)
                    {
                        // Scheduled rather than played immediately - see
                        // pendingBurpPlayTime's own check further up,
                        // which runs every tick regardless of whether
                        // these specific conditions still hold by the
                        // time the delay elapses.
                        pendingBurpPlayTime = ImGuiNowSeconds() + Configuration.SelfSuckingBurpDelaySeconds;
                    }
                }

                dazedAtOrBelowFloorLastCheck = true;

                if (Configuration.SelfSuckingThresholdAutoAttentionEnabled)
                {
                    var swapCheckNow = ImGuiNowSeconds();
                    if (swapCheckNow - lastDazedAttentionSwapTime >= DazedAttentionSwapIntervalSeconds)
                    {
                        lastDazedAttentionSwapTime = swapCheckNow;
                        try
                        {
                            GameCommandSender.SendCommand("/attention motion");
                        }
                        catch (Exception ex)
                        {
                            Log.Error(ex, "[MilkMeter] Self Sucking auto-/attention swap failed - see GameCommandSender's doc comment.");
                        }
                    }
                }
            }
            else
            {
                dazedAtOrBelowFloorLastCheck = false;
            }
        }

        // Guard auto-trigger: forces "/guard motion" once every second
        // for as long as scale is at/above threshold AND the player is
        // standing still - no longer requires any OTHER emote to
        // already be playing (that requirement was removed per
        // request). Mode-agnostic and freeze-agnostic like the Self
        // Sucking Threshold block above, for the same reason: gating
        // this inside Job-mode logic could leave the timer stale across
        // a mode switch or death. Per a later request, ALSO suppressed
        // entirely (skipped for the tick, regardless of the other
        // conditions) while the player is Charmed or performing Ball
        // Dance, so it doesn't interrupt either of those. Per a further
        // request, mirroring the heartbeat sound's own toggle,
        // GuardAutoTriggerOutOfCombatOnly can additionally restrict
        // this to OUT of combat only - it stays fully suppressed while
        // actually in combat, regardless of every other condition
        // above, once turned on.
        {
            var currentPosition = ObjectTable.LocalPlayer?.Position;
            if (currentPosition is { } position)
            {
                var movedThisFrame = lastPlayerPosition is null
                    || System.Numerics.Vector3.Distance(position, lastPlayerPosition.Value) > PositionStillnessEpsilon;

                if (movedThisFrame)
                    lastPlayerMovementTime = ImGuiNowSeconds();

                lastPlayerPosition = position;
            }

            var isStandingStill = lastPlayerMovementTime >= 0d
                && ImGuiNowSeconds() - lastPlayerMovementTime >= PositionStillnessRequiredSeconds;

            var scaleAtOrAboveGuardThreshold = jobCurrentScale >= Configuration.GuardThresholdScale;

            var guardSuppressed = emoteLoopTracker.IsCharmedActive(Configuration)
                || emoteLoopTracker.IsBallDanceActive(Configuration);

            var shouldGuard = Configuration.GuardAutoTriggerEnabled
                && scaleAtOrAboveGuardThreshold
                && isStandingStill
                && !guardSuppressed
                && !(Configuration.GuardAutoTriggerOutOfCombatOnly && Condition[ConditionFlag.InCombat]);

            if (shouldGuard)
            {
                var guardCheckNow = ImGuiNowSeconds();
                if (guardCheckNow - lastGuardTriggerTime >= GuardTriggerIntervalSeconds)
                {
                    lastGuardTriggerTime = guardCheckNow;
                    try
                    {
                        GameCommandSender.SendCommand("/guard motion");
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "[MilkMeter] Guard auto-trigger failed - see GameCommandSender's doc comment.");
                    }
                }
            }
        }

        if (Configuration.Mode == ScaleMode.Food)
        {
            var active = foodTracker.GetFoodBuffState().Active;
            if (active != lastFoodBuffActive)
            {
                forceImmediate = true;
                lastFoodBuffActive = active;
            }
        }
        else if (Configuration.Mode == ScaleMode.Job && jobTracker.GetTrackingKind() == JobTrackingKind.CombatGrowth)
        {
            var inCombat = Condition[ConditionFlag.InCombat];

            // A job can have more than one tracked ability (Warrior has
            // Provoke, Equilibrium, AND Reprisal; every other tank has
            // Provoke and Reprisal) - either one being used
            // applies its effect, so check every tracked name's rising
            // edge independently and accumulate each fired ability's own
            // configurable multiplier (see JobScale.GetOveruseMultiplier
            // and the Provoke/Equilibrium/Lucid Dreaming/Second Wind/
            // Reprisal sliders in the settings window - these are fully in the
            // user's hands, not automatically balanced, and can now be
            // negative to flip an ability's effect from a reduction into
            // an increase). Using two abilities in the same tick stacks
            // both, consistent with "overusing your actions" - it's not
            // meant to be a single combined pulse.
            //
            // anyAbilityUsedThisTick is tracked separately from
            // totalBonusMultiplier's final value/sign, since with
            // negative multipliers now possible, a tick where abilities
            // fired but their multipliers happened to net to exactly
            // zero (or the net sign flips from what a naive ">0" check
            // would assume) must still be treated as "an ability was
            // used" - not silently fall through to passive growth as if
            // nothing happened this tick.
            //
            // "Just used" now fires on EITHER of two independent
            // signals per ability (see lastElapsedByAbility's own doc
            // comment for why both are needed - same reasoning as the
            // GCD fix): the existing rising edge of OnCooldown, OR
            // Elapsed decreasing while already on cooldown, which
            // catches an ability pressed again at the exact instant its
            // own cooldown ends, where OnCooldown may never actually
            // observe a false in between.
            var totalBonusMultiplier = 0f;
            var anyAbilityUsedThisTick = false;
            foreach (var abilityName in jobTracker.GetTrackedAbilityNames() ?? [])
            {
                var (onCooldown, elapsed, _) = jobTracker.GetCooldownState(abilityName);
                var wasOnCooldown = lastOnCooldownByAbility.GetValueOrDefault(abilityName);
                var lastElapsed = lastElapsedByAbility.GetValueOrDefault(abilityName);

                // Only count a rising edge if we actually have a prior
                // observation to compare against - an ability's very
                // first check (wasOnCooldown is null) just records its
                // current state as the baseline, it never counts as
                // "just used" on its own.
                var risingEdge = wasOnCooldown.HasValue && onCooldown && !wasOnCooldown.Value;
                var elapsedReset = onCooldown && elapsed.HasValue && lastElapsed.HasValue && elapsed.Value < lastElapsed.Value;

                if (risingEdge || elapsedReset)
                {
                    totalBonusMultiplier += JobScale.GetOveruseMultiplier(abilityName, Configuration);
                    anyAbilityUsedThisTick = true;
                }

                lastOnCooldownByAbility[abilityName] = onCooldown;
                lastElapsedByAbility[abilityName] = elapsed;
            }

            if (anyAbilityUsedThisTick)
            {
                var delta = Configuration.JobOveruseBonus * totalBonusMultiplier;
                if (delta >= 0f)
                {
                    // Inverted from the plugin's original design: using a
                    // tracked ability with a positive (or net-zero)
                    // multiplier SHRINKS scale, clamped at the floor
                    // (which applies universally now, not just in
                    // combat) rather than growing it up to a ceiling.
                    jobCurrentScale = System.Math.Max(jobCurrentScale - delta, Configuration.JobCombatFloorScale);

                    // Burst only for a STRICTLY positive delta, per
                    // request - a multiplier landing exactly at 0.00
                    // (whether from a single ability left there, or
                    // several abilities' multipliers netting to zero in
                    // the same tick) is a no-op and stays silent, same
                    // as the negative case below. Per a later request,
                    // it doesn't even wake the gauge from idle anymore
                    // either - a multiplier at 0.00 genuinely does
                    // nothing to the scale, so it shouldn't count as
                    // "activity" for idle-fade purposes any more than it
                    // does for the particle burst.
                    if (delta > 0f)
                    {
                        // Ability-use reductions get a more prominent
                        // burst than damage-taken ones - the "empty the
                        // gauge" payoff moment is meant to feel bigger
                        // here.
                        const float abilityUseBurstIntensity = 1.8f;
                        hudGauge.Trigger(abilityUseBurstIntensity);
                    }
                }
                else
                {
                    // Negative multiplier - flips the effect: this
                    // ability GROWS scale instead, capped at
                    // JobUpperLimitScale (Maximum Scaling In Combat)
                    // ALWAYS - per request, this no longer switches down
                    // to JobBaselineScale out of combat the way the
                    // damage-taken/jump increase variants still do, so a
                    // negative-multiplier ability can push scale past
                    // Baseline even out of combat.
                    jobCurrentScale = System.Math.Min(jobCurrentScale - delta, Configuration.JobUpperLimitScale);
                    // No particle burst here, per request - only a
                    // strictly positive multiplier (the shrink case
                    // above) fires it; growth just wakes the gauge from
                    // idle like any other genuine player-driven
                    // activity.
                    hudGauge.WakeFromIdle();
                }
                forceImmediate = true;
            }
            else if (!jobScaleFrozenUntilRevive)
            {
                // Growth only ever pushes UP toward whichever ceiling
                // currently applies, and only from BELOW it - if the
                // ceiling changes (e.g. entering/leaving combat) and the
                // current value is already at or above the new ceiling,
                // this leaves the value exactly where it is rather than
                // pulling it back down to match. Skipped entirely while
                // frozen (dead, pending revival) - scale stays exactly at
                // whatever death set it to until the freeze clears.
                //
                // Three looping emotes can override this normal growth:
                // /shakedrink dramatically speeds up growth AND forces
                // its ceiling to JobUpperLimitScale (Maximum Scaling In
                // Combat) regardless of actual combat state; /dazed and
                // /water ("Breast Feeding Drain") each do the mirror
                // opposite, dramatically speeding up a DRAIN toward
                // their own independently-configured floor instead of
                // growing at all. Only one can be true at a time (you
                // can only perform one looping emote at once), but
                // dazed is checked first, then water, as a defensive
                // tie-break. The instant
                // whichever emote stops, everything reverts to normal on the
                // very next frame - whatever value was reached simply
                // stays there, it doesn't snap back on its own.
                // /milk moan takes priority over all three of the above -
                // see OnShortCommand and moanRampActive's own field
                // comment for the full story. Checked first since it's
                // an explicit player command overriding whatever
                // emote/passive state would otherwise apply, same
                // "highest priority wins outright" reasoning as dazed
                // being checked before water below.
                var dazedDrainActive = Configuration.DazedDrainBoostEnabled && emoteLoopTracker.IsDazedActive(Configuration);
                var waterDrainActive = Configuration.WaterDrainBoostEnabled && emoteLoopTracker.IsWaterActive(Configuration);
                var shakeDrinkActive = Configuration.ShakeDrinkBoostEnabled && emoteLoopTracker.IsShakeDrinkActive(Configuration);

                if (moanRampActive)
                {
                    moanRampElapsedSeconds += deltaSeconds;
                    var t = System.Math.Clamp(moanRampElapsedSeconds / MoanRampDurationSeconds, 0f, 1f);
                    jobCurrentScale = moanRampStartScale + (Configuration.JobUpperLimitScale - moanRampStartScale) * t;

                    // Ramp complete - stop advancing it; scale simply
                    // stays wherever it ended up (Maximum Scaling In
                    // Combat, barring another mechanic moving it
                    // afterward), same "holds, doesn't snap back"
                    // philosophy as every other mechanic in this file.
                    if (t >= 1f)
                        moanRampActive = false;

                    // Deliberate, player-driven activity for the full
                    // 20 seconds - keep the gauge awake throughout, same
                    // as active shakedrink-boosted growth below.
                    hudGauge.WakeFromIdle();
                }
                else if (dazedDrainActive)
                {
                    var previousBreastScale = jobCurrentScale;
                    var drainPerSecond = Configuration.DazedDrainRatePerSecond;
                    jobCurrentScale = JobScale.ApplyDrain(jobCurrentScale, Configuration.DazedDrainFloorScale, drainPerSecond, deltaSeconds);

                    // Milk-to-Food transfer, per request: whatever
                    // percentage points Milk just lost this frame get
                    // added onto Food's own percentage, entirely in
                    // percent-space (see ScalePercent.PercentToScale's
                    // own doc comment for why) so it's exact regardless
                    // of how differently each meter's own three sliders
                    // happen to be configured relative to each other.
                    // percentPointsLost is normally >=0 here (the drain
                    // above only ever reduces jobCurrentScale, never
                    // grows it), but computed via the actual before/
                    // after percentages rather than assumed, so this
                    // stays correct even if that ever changes.
                    if (Configuration.DazedTransferToFoodEnabled && !Configuration.WaistScalingPaused)
                    {
                        var milkPercentBefore = ScalePercent.ComputeTwoSegmentPercent(
                            previousBreastScale, Configuration.JobCombatFloorScale, Configuration.JobBaselineScale, Configuration.JobUpperLimitScale);
                        var milkPercentAfter = ScalePercent.ComputeTwoSegmentPercent(
                            jobCurrentScale, Configuration.JobCombatFloorScale, Configuration.JobBaselineScale, Configuration.JobUpperLimitScale);
                        var percentPointsLost = milkPercentBefore - milkPercentAfter;

                        if (percentPointsLost > 0f)
                        {
                            var currentFoodPercent = ScalePercent.ComputeTwoSegmentPercent(
                                Configuration.CurrentWaistScale, Configuration.WaistMinScale, Configuration.WaistBaselineScale, Configuration.WaistMaxScale);
                            var newFoodPercent = System.Math.Clamp(currentFoodPercent + percentPointsLost, 0f, 200f);
                            Configuration.CurrentWaistScale = ScalePercent.PercentToScale(
                                newFoodPercent, Configuration.WaistMinScale, Configuration.WaistBaselineScale, Configuration.WaistMaxScale);
                        }
                    }

                    // Repeating milk burst (bottle-local only now) while
                    // the drain is actively running, once per second, at
                    // baseline (non-ability-use) intensity.
                    var dazedBurstNow = ImGuiNowSeconds();
                    if (dazedBurstNow - lastDazedBurstTime >= DazedBurstIntervalSeconds)
                    {
                        hudGauge.Trigger();
                        lastDazedBurstTime = dazedBurstNow;
                    }
                }
                else if (waterDrainActive)
                {
                    // Mirror of the dazedDrainActive branch above, just
                    // against WaterDrainRatePerSecond/
                    // WaterDrainFloorScale instead of Dazed's own.
                    var drainPerSecond = Configuration.WaterDrainRatePerSecond;
                    jobCurrentScale = JobScale.ApplyDrain(jobCurrentScale, Configuration.WaterDrainFloorScale, drainPerSecond, deltaSeconds);

                    var waterBurstNow = ImGuiNowSeconds();
                    if (waterBurstNow - lastWaterBurstTime >= WaterBurstIntervalSeconds)
                    {
                        hudGauge.Trigger();
                        lastWaterBurstTime = waterBurstNow;
                    }
                }
                else
                {
                    var ceiling = (shakeDrinkActive || inCombat) ? Configuration.JobUpperLimitScale : Configuration.JobBaselineScale;

                    var growthPerSecond = shakeDrinkActive
                        ? Configuration.ShakeDrinkGrowthRatePerSecond
                        : Configuration.PassiveScaleGenPerSecond;

                    jobCurrentScale = JobScale.ApplyGrowth(jobCurrentScale, ceiling, growthPerSecond, deltaSeconds);

                    // Active, player-driven shakedrink-boosted growth is
                    // deliberate activity, not "just passively
                    // generating gauge" - wake every frame it's active
                    // (WakeFromIdle() is a cheap timestamp write, no
                    // throttling needed the way the dazed-drain burst
                    // above needs one). Ordinary unboosted passive
                    // growth deliberately does NOT do this - see the
                    // class doc comment on HudGaugeWindow for why.
                    if (shakeDrinkActive)
                        hudGauge.WakeFromIdle();
                }

                // Extra Scale Gen: a SECOND, independent rate, standalone
                // and NOT related to DazedDrainRatePerSecond/
                // WaterDrainRatePerSecond/ShakeDrinkGrowthRatePerSecond
                // above (each of which is itself now its own flat,
                // independent rate too, no longer a multiplier of
                // PassiveScaleGenPerSecond the way they originally
                // worked) or anything else - Configuration.ExtraScaleGenPerSecond
                // is used exactly
                // as configured. Gated behind
                // !dazedDrainActive && !waterDrainActive && !moanRampActive
                // per request, so
                // it can no longer
                // generate ANY scaling change - positive or negative -
                // at the same time /dazed's OR /water's own drain, or
                // the /milk moan ramp, is
                // actively
                // running; previously this ran unconditionally, which
                // meant a positive value here could partially or fully
                // counteract the drain in the very same tick. Still
                // IGNORES the in/out of combat ceiling switch entirely:
                // a positive rate GROWS toward JobUpperLimitScale
                // regardless of combat state; a negative rate DRAINS
                // toward JobCombatFloorScale (Minimum Scaling, Always)
                // instead, using ApplyDrain with the magnitude of the
                // configured value (ApplyDrain itself expects a positive
                // rate, so the negative configured value is negated back
                // to positive here) - the floor choice mirrors the
                // positive case's ceiling choice: both target the
                // universal, combat-state-independent bound rather than
                // switching based on actual combat state. Off (0) by
                // default, so this has no effect unless explicitly
                // configured. A negative value here still works AGAINST
                // ordinary passive growth/shakedrink-boosted growth
                // above whenever nothing else is active, since both would
                // be pushing scale in opposite directions within the
                // same tick - that interaction is unchanged, only the
                // drain-active/moan-ramp cases were fixed.
                if (!dazedDrainActive && !waterDrainActive && !moanRampActive)
                {
                    if (Configuration.ExtraScaleGenPerSecond > 0f)
                        jobCurrentScale = JobScale.ApplyGrowth(jobCurrentScale, Configuration.JobUpperLimitScale, Configuration.ExtraScaleGenPerSecond, deltaSeconds);
                    else if (Configuration.ExtraScaleGenPerSecond < 0f)
                        jobCurrentScale = JobScale.ApplyDrain(jobCurrentScale, Configuration.JobCombatFloorScale, -Configuration.ExtraScaleGenPerSecond, deltaSeconds);
                }

                // Out-of-Combat Scale Gen: a THIRD independent rate, per
                // request - the distinguishing feature is that BOTH
                // signs converge on the SAME target
                // (JobBaselineScale / "Maximum Scaling - Out of
                // Combat") rather than heading for opposite bounds the
                // way ExtraScaleGenPerSecond's own two signs do:
                //   - POSITIVE grows UP toward Baseline, and stops
                //     there - it can never push past it (ApplyGrowth's
                //     ceiling IS Baseline here, not JobUpperLimitScale)
                //   - NEGATIVE drains DOWN toward Baseline, and stops
                //     there - it can never fall below it (ApplyDrain's
                //     floor IS Baseline here, not JobCombatFloorScale)
                // So whichever side of Baseline scale currently sits on,
                // a configured value of the matching sign pulls it back
                // to Baseline and then holds it there - and a value of
                // the OPPOSITE sign simply does nothing at all from that
                // side (ApplyGrowth already at/above its ceiling, or
                // ApplyDrain already at/below its floor, both of which
                // return unchanged). Gated on being OUT of combat, per
                // request - unlike every other rate in this method,
                // which are all either combat-agnostic or
                // combat-specific in the other direction. Also gated
                // behind the same drain/ramp exclusions as Extra Scale
                // Gen above, for the same reason: this shouldn't fight
                // an actively-running /dazed or /water drain or a moan
                // ramp within the same tick.
                //
                // OutOfCombatScaleGenPerMinute is a PER-MINUTE rate (see
                // its own doc comment for why it differs in unit from
                // the two per-second rates above), so it's divided by 60
                // here before being handed to ApplyGrowth/ApplyDrain,
                // which both expect a per-second rate.
                //
                // The rate itself is a magnitude only (0 to 1, never
                // negative) - which DIRECTION it moves is decided
                // automatically by which side of JobBaselineScale the
                // scale currently sits on, per request, rather than by
                // the sign of the configured value the way
                // ExtraScaleGenPerSecond above works. Below Baseline it
                // grows up toward it; above Baseline it drains down
                // toward it; either way the target IS Baseline, so it
                // can never overshoot past it in either direction, and
                // once it arrives both branches naturally become no-ops
                // (ApplyGrowth returns unchanged at/above its ceiling,
                // ApplyDrain likewise at/below its floor) so it simply
                // holds there.
                if (!inCombat && !dazedDrainActive && !waterDrainActive && !moanRampActive
                    && Configuration.OutOfCombatScaleGenPerMinute > 0f)
                {
                    var outOfCombatPerSecond = Configuration.OutOfCombatScaleGenPerMinute / 60f;
                    if (jobCurrentScale < Configuration.JobBaselineScale)
                        jobCurrentScale = JobScale.ApplyGrowth(jobCurrentScale, Configuration.JobBaselineScale, outOfCombatPerSecond, deltaSeconds);
                    else if (jobCurrentScale > Configuration.JobBaselineScale)
                        jobCurrentScale = JobScale.ApplyDrain(jobCurrentScale, Configuration.JobBaselineScale, outOfCombatPerSecond, deltaSeconds);
                }
            }
        }

        } // end of "if (!Configuration.ScalingPaused)" - see its own comment above

        // Food/Mana mode's own ComputeCurrentScale() path
        // (FoodScale.Compute/ManaScale.Compute) recomputes fresh from
        // LIVE game state every single call, entirely independent of
        // the pause flag above - unlike Job mode, which just reads back
        // whatever jobCurrentScale currently is (itself already frozen
        // by the block above, except for direct manual-command writes,
        // which is exactly what we still want reflected). Left
        // unguarded, Food/Mana would keep tracking your real Well
        // Fed/MP the whole time "paused," which isn't a freeze at all -
        // so while paused AND on Food or Mana specifically, reuse
        // currentAppliedScale itself as the target (diff against itself
        // = 0, so the easing step below is a genuine no-op) instead of
        // calling ComputeCurrentScale() again. Job mode is deliberately
        // exempted from this substitution, since ComputeCurrentScale()
        // is exactly how a manual command's direct jobCurrentScale
        // write becomes visible while paused in the first place.
        var targetScale = Configuration.ScalingPaused && Configuration.Mode != ScaleMode.Job
            ? GetAppliedScale()
            : ComputeCurrentScale();

        // Ease currentAppliedScale toward targetScale every single frame
        // (not just when we're about to push) so the animation rate is
        // correct regardless of how the IPC push throttle below skips
        // frames. First-ever tick starts from neutral (1.0) so even
        // logging in mid-buff/mid-cooldown eases in.
        if (currentAppliedScale < 0f)
            currentAppliedScale = 1f;

        var maxStep = Configuration.ScaleTransitionRate * deltaSeconds;
        var diff = targetScale - currentAppliedScale;

        currentAppliedScale = maxStep <= 0f || System.Math.Abs(diff) <= maxStep
            ? targetScale
            : currentAppliedScale + System.Math.Sign(diff) * maxStep;

        // Waist easing, per request - an exact mirror of the breast
        // easing immediately above, just with its own independent rate
        // (WaistScaleTransitionRate) and its own applied value.
        // Configuration.CurrentWaistScale remains the TARGET (whatever
        // the accumulator or remaining-time model computed for this
        // frame); currentAppliedWaistScale is what actually gets pushed
        // to Customize+ below, so a sudden target jump - a food-eaten
        // bump, a death reset, a /food command, or the remaining-time
        // model's own recompute after a buff appears/expires - now
        // animates smoothly rather than snapping. Same first-ever-tick
        // handling too: start from neutral (1.0) so logging in
        // mid-buff eases in rather than popping.
        if (currentAppliedWaistScale < 0f)
            currentAppliedWaistScale = 1f;

        var waistMaxStep = Configuration.WaistScaleTransitionRate * deltaSeconds;
        var waistDiff = Configuration.CurrentWaistScale - currentAppliedWaistScale;

        currentAppliedWaistScale = waistMaxStep <= 0f || System.Math.Abs(waistDiff) <= waistMaxStep
            ? Configuration.CurrentWaistScale
            : currentAppliedWaistScale + System.Math.Sign(waistDiff) * waistMaxStep;

        // Placed here specifically - after currentAppliedScale is
        // finalized for this frame, but BEFORE the IPC push throttle's
        // early-returns below - so the DTR bar text always stays
        // current every single frame regardless of whether this frame
        // actually pushes to Customize+.
        UpdateDtrBarEntry();

        // Only the actual IPC push to Customize+ is throttled - the
        // animation state above always stays current, and the waist
        // accumulator above updates every frame regardless too (that's
        // its own independent value, not something eased frame-to-frame
        // the way currentAppliedScale is - see the merged-in waist
        // block near the top of this method).
        //
        // Combined push, per the fix described in Plugin.cs's class doc
        // comment and CustomizePlusIpc.SetScales: pushes BOTH chest and
        // waist scale in one call, so it fires if EITHER meter's value
        // moved enough (or either has never been pushed at all yet -
        // the same -1f "never pushed" sentinel lastPushedScale already
        // used, now also applied to lastPushedWaistScale), not just
        // chest alone. Both sides now compare their own EASED applied
        // value (currentAppliedScale / currentAppliedWaistScale) rather
        // than their raw target, so the throttle correctly keeps firing
        // across an in-progress animation on either meter instead of
        // deciding it's already arrived. forceImmediate itself remains
        // chest-specific (set by breast-scale events like the food buff
        // appearing or a tracked job mechanic changing - see where it's
        // set further up this method).
        var now = ImGuiNowSeconds();
        var firstEverPush = lastPushedScale < 0f || lastPushedWaistScale < 0f;
        var chestDeltaBigEnough = System.Math.Abs(currentAppliedScale - lastPushedScale) >= MinScaleDelta;
        var waistDeltaBigEnough = System.Math.Abs(currentAppliedWaistScale - lastPushedWaistScale) >= MinScaleDelta;
        var dueForPush = now - lastPushTime >= MinSecondsBetweenPushes;

        if (!forceImmediate && !firstEverPush && !(dueForPush && (chestDeltaBigEnough || waistDeltaBigEnough)))
            return;

        customizePlus.SetScales(currentAppliedScale, currentAppliedWaistScale);
        lastPushedScale = currentAppliedScale;
        lastPushedWaistScale = currentAppliedWaistScale;
        lastPushTime = now;
    }

    /// <summary>Reads whichever source is active in config and returns the resulting scale.</summary>
    private float ComputeCurrentScale()
    {
        return Configuration.Mode switch
        {
            ScaleMode.Food => FoodScale.Compute(
                foodTracker.GetFoodBuffState().RemainingSeconds,
                Configuration.FoodMinScale,
                Configuration.FoodMaxScale,
                Configuration.FoodTaperMinutes),
            ScaleMode.Mana => ManaScale.Compute(manaTracker.GetManaFraction(), Configuration.ManaInverted),
            ScaleMode.Job => JobScaleFromState(),
            _ => Configuration.FoodMinScale,
        };
    }

    private float JobScaleFromState()
    {
        return jobTracker.GetTrackingKind() switch
        {
            JobTrackingKind.CombatGrowth => jobCurrentScale,
            _ => Configuration.JobBaselineScale, // not tracked - baseline, mechanic doesn't apply
        };
    }

    /// <summary>The actual scale currently pushed to Customize+ (post-animation), for the settings window's monitor.</summary>
    private float GetAppliedScale() => currentAppliedScale < 0f ? 1f : currentAppliedScale;

    /// <summary>
    /// Refreshes the DTR (server info bar) entry to show a single
    /// COMBINED string covering both meters this plugin tracks, per
    /// request: "Food: X% | Milk: Y%" - Food (waist) percentage first,
    /// then Milk (breast) percentage, in that exact order. Each half
    /// uses ScalePercent.ComputeTwoSegmentPercent against its own
    /// meter's three sliders (Minimum/Baseline/Maximum for waist,
    /// Minimum/Maximum-Out-of-Combat/Maximum-In-Combat for breast) - see
    /// that method's own doc comment for the shared two-segment shape.
    /// There is only ONE entry/toggle for both halves
    /// (Configuration.ShowDtrBarEntry) - no separate per-meter DTR
    /// toggle, per request. Called from OnFrameworkUpdate after
    /// currentAppliedScale is finalized for the frame but BEFORE the IPC
    /// push throttle's early-returns, so it stays live every frame
    /// regardless of whether that frame actually pushes to Customize+ -
    /// and, since OnFrameworkUpdate itself early-returns the instant
    /// Configuration.ScalingPaused is true, the MILK half naturally
    /// freezes at its last value while breast scaling is paused (the
    /// FOOD half keeps updating regardless, since the waist accumulator
    /// block sits before that early-return - see OnFrameworkUpdate's
    /// class doc comment for why the two meters pause independently).
    /// Only actually rebuilds the SeString when EITHER rounded
    /// percentage changes, to avoid needless SeString-rebuild churn -
    /// Shown is still updated unconditionally every call, since that's a
    /// cheap bool set and the toggle should take effect immediately
    /// regardless of whether either percentage happens to be changing at
    /// the same moment. IDtrBarEntry.Text/Tooltip are typed SeString?,
    /// not plain string, and SeString has NO implicit conversion from
    /// string - an earlier version of this method assigned a plain
    /// interpolated string directly, which compiled but rendered as
    /// blank in-game; fixed to build via
    /// SeStringBuilder().AddText(...).Build() instead, the documented
    /// way to construct one, confirmed working via the equivalent code
    /// in this author's other (now-merged) plugin, Hunger Meter.
    /// </summary>
    private void UpdateDtrBarEntry()
    {
        dtrBarEntry.Shown = Configuration.ShowDtrBarEntry;
        if (!Configuration.ShowDtrBarEntry)
            return;

        var milkPercent = (int)System.Math.Round(ScalePercent.ComputeTwoSegmentPercent(
            GetAppliedScale(),
            Configuration.JobCombatFloorScale,
            Configuration.JobBaselineScale,
            Configuration.JobUpperLimitScale));

        // Food half: in remaining-time mode this maps TIME directly onto
        // percent (0% no buff, 100% at 30 min, 200% at 60, 300% at 90 -
        // per request), entirely independent of the Min/Max scale
        // sliders. In accumulator mode there's no meaningful "remaining
        // time" to map, so it falls back to the scale-value-based
        // two-segment 0-200% mapping that mode has always used.
        var foodPercent = Configuration.WaistUseRemainingTimeMode
            ? (int)System.Math.Round(WaistScale.ComputeRemainingTimePercent(lastKnownWellFedRemainingSeconds))
            : (int)System.Math.Round(ScalePercent.ComputeTwoSegmentPercent(
                Configuration.CurrentWaistScale,
                Configuration.WaistMinScale,
                Configuration.WaistBaselineScale,
                Configuration.WaistMaxScale));

        if (milkPercent == lastDtrBarPercent && foodPercent == lastDtrBarWaistPercent)
            return;

        lastDtrBarPercent = milkPercent;
        lastDtrBarWaistPercent = foodPercent;
        dtrBarEntry.Text = new SeStringBuilder().AddText($"| Food: {foodPercent}% | Milk: {milkPercent}% |").Build();
        dtrBarEntry.Tooltip = new SeStringBuilder().AddText(
            $"Milk Meter: Food (waist) {foodPercent}% (applied scale {Configuration.CurrentWaistScale:F2}), " +
            $"Milk (breast) {milkPercent}% (applied scale {GetAppliedScale():F2})").Build();
    }

    /// <summary>
    /// Hides the DTR entry outright (rather than leaving it showing a
    /// stale percentage) for the two early-exit cases in
    /// OnFrameworkUpdate where nothing about scale is meaningful right
    /// now: the plugin disabled entirely, or no local player yet
    /// (logged out/at character select). Resets both lastDtrBarPercent
    /// and lastDtrBarWaistPercent back to their "never set" sentinel
    /// too, so the very next UpdateDtrBarEntry() call after either
    /// condition clears always writes fresh combined text rather than
    /// potentially skipping the write because BOTH halves happen to
    /// match whatever was last shown before hiding.
    /// </summary>
    private void HideDtrBarEntry()
    {
        dtrBarEntry.Shown = false;
        lastDtrBarPercent = -1;
        lastDtrBarWaistPercent = -1;
    }

    /// <summary>
    /// Called by HudGaugeWindow the instant the gauge is right-clicked -
    /// a standalone reset to 1.0, distinct from left-click's pause
    /// toggle: doesn't touch Configuration.ScalingPaused at all (works
    /// identically whether currently paused or not), and doesn't freeze
    /// anything afterward - growth/drain/whatever mechanic is currently
    /// active just continues normally from 1.0 on the very next tick.
    /// Also cancels an in-progress moan ramp, same as the 'minimum'/
    /// 'maximum'/direct-set commands in OnShortCommand, since this is
    /// the same kind of explicit override. Resets jobCurrentScale (the
    /// only mode with an internally-tracked value to reset - Food/Mana
    /// modes compute their target fresh from live external state every
    /// frame instead, so there's nothing to reset there) AND
    /// currentAppliedScale directly, then pushes to Customize+
    /// immediately rather than waiting for the next OnFrameworkUpdate
    /// tick to naturally pick it up - not strictly necessary the way it
    /// was for the (since-reverted) pause-resets-to-1.0 feature, since
    /// nothing is skipped here the way ScalingPaused's early-return
    /// skips OnFrameworkUpdate, but pushing immediately still means the
    /// visual snap to 1.0 doesn't wait on the next tick's own throttling
    /// (MinSecondsBetweenPushes/MinScaleDelta) to decide it's worth
    /// pushing.
    /// </summary>
    private void ResetScaleToBaseline()
    {
        moanRampActive = false;
        jobCurrentScale = 1.0f;
        currentAppliedScale = 1.0f;
        // Waist scale is intentionally left untouched here - this reset
        // is specifically the breast-only right-click action (see
        // HudGaugeWindow's onRightClicked callback), not a reset of the
        // waist meter, so the combined push carries forward whatever
        // the waist's own EASED applied value currently is rather than
        // resetting that too (or snapping it to its un-eased target,
        // which would visibly jolt the waist mid-animation just because
        // the breast gauge happened to be right-clicked).
        var waistNow = currentAppliedWaistScale < 0f ? 1.0f : currentAppliedWaistScale;
        customizePlus.SetScales(1.0f, waistNow);
        lastPushedScale = 1.0f;
        lastPushedWaistScale = waistNow;
        lastPushTime = ImGuiNowSeconds();
        Log.Information("[MilkMeter] Gauge right-clicked - breast scale reset to 1.0.");
    }


    private static double ImGuiNowSeconds() =>
        System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;

    public void Dispose()
    {
        Framework.Update -= OnFrameworkUpdate;
        PluginInterface.UiBuilder.Draw -= settingsWindow.Draw;
        PluginInterface.UiBuilder.Draw -= hungerSettingsWindow.Draw;
        PluginInterface.UiBuilder.Draw -= hudGauge.Draw;
        PluginInterface.UiBuilder.Draw -= DrawThresholdEffect;
        PluginInterface.UiBuilder.OpenConfigUi -= OnOpenConfigUi;
        CommandManager.RemoveHandler(CommandName);
        CommandManager.RemoveHandler(ShortCommandName);
        CommandManager.RemoveHandler(HungerCommandName);
        CommandManager.RemoveHandler(FoodCommandName);
        CommandManager.RemoveHandler(AssCommandName);
        CommandManager.RemoveHandler(TitsCommandName);
        dtrBarEntry.Remove();
        customizePlus.RevertScales();
        customizePlus.Dispose();
        heartbeatSoundPlayer.Dispose();
        moanSoundPlayer.Dispose();
        burpSoundPlayer.Dispose();
    }
}
