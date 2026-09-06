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

    public Configuration Configuration { get; }
    private readonly CustomizePlusIpc customizePlus;
    private readonly FoodBuffTracker foodTracker;
    private readonly ManaTracker manaTracker;
    private readonly JobBuffTracker jobTracker;
    private readonly EmoteLoopTracker emoteLoopTracker;
    private readonly SettingsWindow settingsWindow;
    private readonly HudGaugeWindow hudGauge;
    private readonly IDtrBarEntry dtrBarEntry;
    private int lastDtrBarPercent = -1;
    private readonly HeartbeatSoundPlayer heartbeatSoundPlayer;
    private readonly MoanSoundPlayer moanSoundPlayer;
    private readonly BurpSoundPlayer burpSoundPlayer;
    private readonly ThresholdEffectOverlay thresholdEffectOverlay;

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
                + "displaying.",
        });

        CommandManager.AddHandler(ShortCommandName, new CommandInfo(OnShortCommand)
        {
            HelpMessage = "'/milk' alone opens the settings window. 'minimum' eases the job scale toward "
                + "Minimum Scaling over time; 'maximum' eases it toward Maximum Scaling (Out of Combat) "
                + "over time - both read whatever those sliders are currently set to, and work regardless "
                + "of the currently active Scale Source. 'moan' plays the moan sound and ramps job scale "
                + "up to Maximum Scaling (In Combat) over 20 seconds. A plain number (e.g. '1.3') sets job "
                + "scale directly to that value, clamped between Minimum Scaling and Maximum Scaling (In "
                + "Combat).",
        });

        Framework.Update += OnFrameworkUpdate;
        PluginInterface.UiBuilder.Draw += settingsWindow.Draw;
        PluginInterface.UiBuilder.Draw += hudGauge.Draw;
        PluginInterface.UiBuilder.Draw += DrawThresholdEffect;
        PluginInterface.UiBuilder.OpenConfigUi += OnOpenConfigUi;
    }

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

        if (float.TryParse(args, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var requestedScale))
        {
            // Direct manual set, clamped between Minimum Scaling and
            // Maximum Scaling (In Combat) - matches 'minimum'/'maximum'
            // above in being a plain jobCurrentScale write (same
            // Mode-agnostic caveat applies: only visually apparent while
            // Job mode is the active Scale Source), but to an arbitrary
            // caller-specified value instead of one of the two fixed
            // presets. Cancels any in-progress moan ramp, same
            // reasoning as minimum/maximum above.
            moanRampActive = false;
            jobCurrentScale = System.Math.Clamp(requestedScale, Configuration.JobCombatFloorScale, Configuration.JobUpperLimitScale);
            Log.Information($"[MilkMeter] Setting job scale to {jobCurrentScale:F2} (requested {requestedScale:F2}, clamped between Minimum Scaling {Configuration.JobCombatFloorScale:F2} and Maximum Scaling - In Combat {Configuration.JobUpperLimitScale:F2}).");
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
            var inCombatNow = Condition[ConditionFlag.InCombat];
            var ceiling = inCombatNow ? Configuration.JobUpperLimitScale : Configuration.JobBaselineScale;
            var range = ceiling - Configuration.JobCombatFloorScale;
            var fraction = range > 0f
                ? System.Math.Clamp((GetAppliedScale() - Configuration.JobCombatFloorScale) / range, 0f, 1f)
                : 0f;
            var percent = (int)System.Math.Round(fraction * 100f);

            Log.Information("[MilkMeter] DTR bar debug info:\n" +
                $"ShowDtrBarEntry: {Configuration.ShowDtrBarEntry}\n" +
                $"dtrBarEntry.Shown (actual current value read back from Dalamud): {dtrBarEntry.Shown}\n" +
                $"GetAppliedScale(): {GetAppliedScale():F3}\n" +
                $"In combat: {inCombatNow}, ceiling used: {ceiling:F2} (JobCombatFloorScale: {Configuration.JobCombatFloorScale:F2})\n" +
                $"Computed percent this instant: {percent}, lastDtrBarPercent (last one actually written): {lastDtrBarPercent}\n" +
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
            // than jumping back to wherever it left off.
            customizePlus.RevertChestScale();
            lastPushedScale = -1f;
            currentAppliedScale = -1f;
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

        // Scaling Paused: an early return covering everything below
        // that would modify or push scale - passive growth, all the
        // ability-use/damage-taken/jump/dazed/guard mechanics, the
        // ease-toward-target animation, and the Customize+ push itself.
        // Deliberately placed AFTER the two checks above (dazed-floor
        // tracking and the scheduled burp), since neither of those
        // actually changes scale - the burp is a sound effect already
        // committed to before pausing, and the floor-tracking timestamp
        // is a passive observation, not a mutation. Toggled by
        // left-clicking the HUD gauge itself (see HudGaugeWindow.Draw())
        // or the settings window checkbox - unlike Configuration.Enabled,
        // this deliberately leaves the HUD gauge and its particle
        // effects still rendering (frozen at whatever scale was applied
        // at the moment of pausing, dimmed to 10%
        // opacity rather than drawing a red X over it),
        // rather than disabling the plugin's visible presence entirely.
        // The threshold effect (vignette/glow/heartbeat sound) is the
        // one exception - it completely stops the instant this is true,
        // per request, rather than continuing to evaluate against the
        // frozen scale value (see ThresholdEffectOverlay.Draw() and
        // HudGaugeWindow's glow block, which each check this
        // independently, since they're driven by the separate UI-draw
        // callback rather than this method).
        if (Configuration.ScalingPaused)
        {
            // Still refresh the DTR bar even while paused - otherwise,
            // if ScalingPaused happens to already be true from the very
            // first frame after plugin load, UpdateDtrBarEntry() further
            // down this method would never run even once, leaving the
            // entry's Text permanently unset (blank, but still
            // reserving a slot, since Shown was set in the constructor)
            // for as long as the plugin stays paused - which could be
            // forever, if nothing else in the session happens to
            // unpause it. Uses whatever GetAppliedScale() currently
            // returns, which is itself frozen at its last value while
            // paused (nothing updates currentAppliedScale during the
            // early-return below), so this correctly shows the frozen
            // percentage rather than a stale/uninitialized one.
            UpdateDtrBarEntry();
            return;
        }

        var deltaSeconds = (float)framework.UpdateDelta.TotalSeconds;

        // Detect the food buff appearing/disappearing, or the tracked job
        // mechanic's state changing, so we can bypass the push throttle
        // below and let the ease-in/out begin immediately rather than
        // waiting out MinSecondsBetweenPushes. (Food buff removal looks
        // the same to us whether it expired naturally or was clicked off
        // manually - the status is simply no longer present either way,
        // so no separate handling is needed for that case.)
        var forceImmediate = false;

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
        // The revival-clear (falling edge of unconscious) runs
        // regardless of whether ResetJobScaleToCombatFloorOnDeath is
        // still on, so an active freeze always gets released on revive
        // even if the setting changed mid-death - it never gets stuck.
        {
            var unconscious = Condition[ConditionFlag.Unconscious];
            if (Configuration.ResetJobScaleToCombatFloorOnDeath && unconscious && !lastUnconscious)
            {
                jobCurrentScale = Configuration.JobCombatFloorScale;
                jobScaleFrozenUntilRevive = true;
                forceImmediate = true;
            }
            else if (!unconscious && lastUnconscious && jobScaleFrozenUntilRevive)
            {
                jobScaleFrozenUntilRevive = false; // revived - passive regen resumes next tick
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
                    var drainPerSecond = Configuration.PassiveScaleGenPerSecond * Configuration.DazedDrainRateMultiplier;
                    jobCurrentScale = JobScale.ApplyDrain(jobCurrentScale, Configuration.DazedDrainFloorScale, drainPerSecond, deltaSeconds);

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
                    // against WaterDrainRateMultiplier/
                    // WaterDrainFloorScale instead of Dazed's own.
                    var drainPerSecond = Configuration.PassiveScaleGenPerSecond * Configuration.WaterDrainRateMultiplier;
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
                        ? Configuration.PassiveScaleGenPerSecond * Configuration.ShakeDrinkGrowthRateMultiplier
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
                // and NOT multiplicative of any other factor -
                // Configuration.ExtraScaleGenPerSecond is used exactly
                // as configured, never scaled by DazedDrainRateMultiplier,
                // WaterDrainRateMultiplier, ShakeDrinkGrowthRateMultiplier,
                // or anything else the way
                // PassiveScaleGenPerSecond above is. Gated behind
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
            }
        }

        var targetScale = ComputeCurrentScale();

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

        // Placed here specifically - after currentAppliedScale is
        // finalized for this frame, but BEFORE the IPC push throttle's
        // early-returns below - so the DTR bar text always stays
        // current every single frame regardless of whether this frame
        // actually pushes to Customize+.
        UpdateDtrBarEntry();

        // Only the actual IPC push to Customize+ is throttled - the
        // animation state above always stays current.
        var now = ImGuiNowSeconds();
        if (!forceImmediate && now - lastPushTime < MinSecondsBetweenPushes)
            return;

        if (!forceImmediate && lastPushedScale >= 0f && System.Math.Abs(currentAppliedScale - lastPushedScale) < MinScaleDelta)
            return;

        customizePlus.SetChestScale(currentAppliedScale);
        lastPushedScale = currentAppliedScale;
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
    /// Refreshes the DTR (server info bar) entry to show the current
    /// applied scale as a percentage - Minimum Scaling is 0%, and
    /// whichever Maximum Scaling currently applies (In Combat or Out of
    /// Combat, matching the exact same context-dependent ceiling choice
    /// used for ordinary passive growth elsewhere in this file) is
    /// 100%, per request. Universal across all three Scale Sources
    /// (Food/Mana/Job), not just Job mode, since Minimum/Maximum Scaling
    /// are themselves already treated as global bounds elsewhere in
    /// this file (the death-reset floor, Extra Scale Gen's drain
    /// target, and so on all reference them regardless of Mode) rather
    /// than something Job-mode-specific despite the "Job*" property
    /// name prefix. Called from OnFrameworkUpdate after
    /// currentAppliedScale is finalized for the frame but BEFORE the IPC
    /// push throttle's early-returns, so it stays live every frame
    /// regardless of whether that frame actually pushes to Customize+ -
    /// and, since OnFrameworkUpdate itself early-returns the instant
    /// Configuration.ScalingPaused is true, this naturally freezes at
    /// its last value while paused too, same as everything else in this
    /// plugin, with no special-casing needed here. Only actually writes
    /// to the entry's Text when the rounded percentage changes, to
    /// avoid needless SeString-rebuild churn - Shown is still updated
    /// unconditionally
    /// every call, since that's a cheap bool set and the toggle should
    /// take effect immediately regardless of whether the percentage
    /// happens to be changing at the same moment. IDtrBarEntry.Text/
    /// Tooltip are typed SeString?, not plain string, and SeString has
    /// NO implicit conversion from string - an earlier version of this
    /// method assigned a plain interpolated string directly, which
    /// compiled but rendered as blank in-game; fixed to build via
    /// SeStringBuilder().AddText(...).Build() instead, the documented
    /// way to construct one, confirmed working via the equivalent code
    /// in this author's other plugin (Hunger Meter).
    /// </summary>
    private void UpdateDtrBarEntry()
    {
        dtrBarEntry.Shown = Configuration.ShowDtrBarEntry;
        if (!Configuration.ShowDtrBarEntry)
            return;

        var ceiling = Condition[ConditionFlag.InCombat] ? Configuration.JobUpperLimitScale : Configuration.JobBaselineScale;
        var range = ceiling - Configuration.JobCombatFloorScale;
        var fraction = range > 0f
            ? (GetAppliedScale() - Configuration.JobCombatFloorScale) / range
            : 0f;
        var percent = (int)System.Math.Round(System.Math.Clamp(fraction, 0f, 1f) * 100f);

        if (percent == lastDtrBarPercent)
            return;

        lastDtrBarPercent = percent;
        dtrBarEntry.Text = new SeStringBuilder().AddText($"Milk: {percent}%").Build();
        dtrBarEntry.Tooltip = new SeStringBuilder().AddText($"Milk Meter: {percent}% (applied scale {GetAppliedScale():F2})").Build();
    }

    /// <summary>
    /// Hides the DTR entry outright (rather than leaving it showing a
    /// stale percentage) for the two early-exit cases in
    /// OnFrameworkUpdate where nothing about scale is meaningful right
    /// now: the plugin disabled entirely, or no local player yet
    /// (logged out/at character select). Resets lastDtrBarPercent back
    /// to its "never set" sentinel too, so the very next
    /// UpdateDtrBarEntry() call after either condition clears always
    /// writes fresh text rather than potentially skipping the write
    /// because the percentage happens to match whatever was last shown
    /// before hiding.
    /// </summary>
    private void HideDtrBarEntry()
    {
        dtrBarEntry.Shown = false;
        lastDtrBarPercent = -1;
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
        customizePlus.SetChestScale(1.0f);
        lastPushedScale = 1.0f;
        lastPushTime = ImGuiNowSeconds();
        Log.Information("[MilkMeter] Gauge right-clicked - scale reset to 1.0.");
    }


    private static double ImGuiNowSeconds() =>
        System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;

    public void Dispose()
    {
        Framework.Update -= OnFrameworkUpdate;
        PluginInterface.UiBuilder.Draw -= settingsWindow.Draw;
        PluginInterface.UiBuilder.Draw -= hudGauge.Draw;
        PluginInterface.UiBuilder.Draw -= DrawThresholdEffect;
        PluginInterface.UiBuilder.OpenConfigUi -= OnOpenConfigUi;
        CommandManager.RemoveHandler(CommandName);
        CommandManager.RemoveHandler(ShortCommandName);
        dtrBarEntry.Remove();
        customizePlus.RevertChestScale();
        customizePlus.Dispose();
        heartbeatSoundPlayer.Dispose();
        moanSoundPlayer.Dispose();
        burpSoundPlayer.Dispose();
    }
}
