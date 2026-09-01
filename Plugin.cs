using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Command;
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
    // per second for as long as /cackle's drain is actively running -
    // -1 sentinel means "never fired yet", so the very first tick of an
    // active drain fires immediately rather than waiting out the first
    // interval.
    private const double CackleBurstIntervalSeconds = 1.0;
    private double lastCackleBurstTime = -1d;

    // Falling-edge tracker for the Self Sucking auto-attention-swap's
    // burp sound - true once scale has been observed at/below
    // CackleDrainFloorScale, reset to false the moment it's back above
    // (or /cackle stops being active). The burp fires only on the
    // transition into "at/below the floor," not on every tick it stays
    // there.
    private bool cackleAtOrBelowFloorLastCheck;

    // Timestamp of the most recent tick scale was observed strictly
    // above CackleDrainFloorScale - tracked unconditionally every tick,
    // regardless of Mode or whether /cackle is active, since scale can
    // be pushed above the floor by anything (passive growth, ability
    // use, etc.), not just /cackle stopping. Used by the burp trigger
    // below to distinguish "scale just NOW fell from above the floor"
    // (burp should play) from "scale was ALREADY at/below the floor
    // when /cackle started" (burp should NOT play) - a plain single-tick
    // falling-edge check alone couldn't tell these apart, since
    // cackleAtOrBelowFloorLastCheck resets to false every time /cackle
    // stops being active, so starting /cackle while already at/below the
    // floor looked identical to a genuine fresh drop.
    private double lastAboveCackleDrainFloorTime = -1d;

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
    // second, matching CackleAttentionSwapIntervalSeconds below) - -1
    // sentinel means "never sent yet", so the first tick the floor is
    // reached fires immediately rather than waiting out the first
    // interval. Repeats for as long as the conditions hold, per
    // request, rather than firing just once - see the trigger block's
    // own comment for why.
    private const double CackleAttentionSwapIntervalSeconds = 1.0;
    private double lastCackleAttentionSwapTime = -1d;

    // Scheduled burp playback time (see SelfSuckingBurpDelaySeconds) -
    // null means nothing is pending. Checked every tick regardless of
    // whether the Self Sucking Threshold's own conditions still hold by
    // the time the delay elapses, since the burp is meant to accompany
    // the animation change that already happened, not the live
    // threshold state at playback time.
    private double? pendingBurpPlayTime;

    // Periodic re-trigger timer for the guard auto-trigger (once per
    // second, matching CackleBurstIntervalSeconds's own established
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
        hudGauge = new HudGaugeWindow(Configuration, GetAppliedScale, () => Condition[ConditionFlag.InCombat]);
        heartbeatSoundPlayer = new HeartbeatSoundPlayer(Log);
        moanSoundPlayer = new MoanSoundPlayer(Log);
        burpSoundPlayer = new BurpSoundPlayer(Log);
        thresholdEffectOverlay = new ThresholdEffectOverlay(Configuration, heartbeatSoundPlayer, moanSoundPlayer);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "'/milkmeter' toggles on/off. 'status' prints current scale. "
                + "'config' opens the settings window. "
                + "'mode food', 'mode mana', or 'mode job' switches the scaling source. "
                + "'rebaseline' re-reads your current chest scale as the new baseline. "
                + "'dumpprofile' prints your active Customize+ profile's raw JSON to /xllog. "
                + "'jobdebug' prints diagnostic info for Job Buff mode's ability/cooldown lookup. "
                + "'emotedebug' prints your current Character.Mode/ModeParam - use while performing "
                + "/shakedrink, /cackle, /attention, or /guard to find the ShakeDrinkEmoteModeParam/"
                + "CackleEmoteModeParam/AttentionEmoteModeParam/GuardEmoteModeParam values for precise "
                + "emote matching. 'guarddebug' breaks down the guard auto-trigger's two conditions "
                + "(scale threshold, standing-still) separately, plus when it'll next fire. "
                + "'gcddebug' shows the configured GCD recast group's live cooldown state - use it "
                + "while pressing different GCD spells/weaponskills to confirm or correct "
                + "GcdRecastGroup if GCD-based reduction doesn't seem to be firing. 'jumpdebug' shows "
                + "live Y-position/threshold info for tuning JumpVelocityThreshold if jumps are being "
                + "missed or over-triggered.",
        });

        CommandManager.AddHandler(ShortCommandName, new CommandInfo(OnShortCommand)
        {
            HelpMessage = "'/milk' alone opens the settings window. 'minimum' eases the job scale toward "
                + "Minimum Scaling over time; 'maximum' eases it toward Maximum Scaling (Out of Combat) "
                + "over time - both read whatever those sliders are currently set to, and work regardless "
                + "of the currently active Scale Source.",
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
            Log.Information($"[MilkMeter] Easing job scale toward Minimum Scaling ({Configuration.JobCombatFloorScale:F2}).");
            return;
        }

        if (args.Equals("maximum", System.StringComparison.OrdinalIgnoreCase))
        {
            jobCurrentScale = Configuration.JobBaselineScale;
            Log.Information($"[MilkMeter] Easing job scale toward Maximum Scaling - Out of Combat ({Configuration.JobBaselineScale:F2}).");
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
    private void DrawThresholdEffect() => thresholdEffectOverlay.Draw(GetAppliedScale());

    private void OnCommand(string command, string args)
    {
        args = args.Trim();

        if (args.Equals("status", System.StringComparison.OrdinalIgnoreCase))
        {
            Log.Information($"[MilkMeter] Mode={Configuration.Mode}, " +
                $"target={ComputeCurrentScale():F3}, applied={GetAppliedScale():F3}");
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
            var wouldTrigger = Configuration.GuardAutoTriggerEnabled && scaleAtOrAbove && standingStill && !guardSuppressed;
            var secondsUntilNextFire = lastGuardTriggerTime < 0d
                ? 0d
                : System.Math.Max(0d, GuardTriggerIntervalSeconds - (ImGuiNowSeconds() - lastGuardTriggerTime));

            Log.Information("[MilkMeter] Guard auto-trigger debug info:\n" +
                $"GuardAutoTriggerEnabled: {Configuration.GuardAutoTriggerEnabled}\n" +
                $"Current job scale: {jobCurrentScale:F3}, GuardThresholdScale: {Configuration.GuardThresholdScale:F3}, at or above threshold: {scaleAtOrAbove}\n" +
                $"Seconds since last movement: {(secondsSinceMovement.HasValue ? secondsSinceMovement.Value.ToString("F2") : "never moved yet")}, " +
                $"required: {PositionStillnessRequiredSeconds:F1}, standing still: {standingStill}\n" +
                $"Charmed active: {charmedActive}, Ball Dance active: {ballDanceActive} (either one suppresses the trigger entirely)\n" +
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
            return;

        if (ObjectTable.LocalPlayer is null)
            return;

        customizePlus.SetCharacterObjectIndex(ObjectTable.LocalPlayer.ObjectIndex);

        // Tracked unconditionally, every tick, regardless of Mode or
        // whether /cackle is active - see lastAboveCackleDrainFloorTime's
        // own doc comment above for why this needs to be tracked this
        // broadly rather than only within the cackle-specific block
        // further down.
        if (jobCurrentScale > Configuration.CackleDrainFloorScale)
            lastAboveCackleDrainFloorTime = ImGuiNowSeconds();

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
        // ability-use/damage-taken/jump/cackle/guard mechanics, the
        // ease-toward-target animation, and the Customize+ push itself.
        // Deliberately placed AFTER the two checks above (cackle-floor
        // tracking and the scheduled burp), since neither of those
        // actually changes scale - the burp is a sound effect already
        // committed to before pausing, and the floor-tracking timestamp
        // is a passive observation, not a mutation. Toggled by
        // left-clicking the HUD gauge itself (see HudGaugeWindow.Draw())
        // or the settings window checkbox - unlike Configuration.Enabled,
        // this deliberately leaves the HUD gauge and its particle
        // effects still rendering (frozen at whatever value scale was
        // at when paused, with a big red X drawn over the bottle),
        // rather than disabling the plugin's visible presence entirely.
        // The threshold effect (vignette/glow/heartbeat sound) is the
        // one exception - it completely stops the instant this is true,
        // per request, rather than continuing to evaluate against the
        // frozen scale value (see ThresholdEffectOverlay.Draw() and
        // HudGaugeWindow's glow block, which each check this
        // independently, since they're driven by the separate UI-draw
        // callback rather than this method).
        if (Configuration.ScalingPaused)
            return;

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
                    // Capped at the same ceiling passive growth
                    // targets (context-dependent by combat state),
                    // not a fixed value - this acts like an
                    // accelerated burst of the same regen, not its
                    // own separate cap.
                    var ceiling = Condition[ConditionFlag.InCombat]
                        ? Configuration.JobUpperLimitScale
                        : Configuration.JobBaselineScale;
                    jobCurrentScale = System.Math.Min(
                        jobCurrentScale + Configuration.DamageTakenScaleIncrease,
                        ceiling);
                    // No particle burst for the increase variant
                    // (that's specifically an "empty the gauge"
                    // payoff), but it's still genuine activity that
                    // should wake the gauge from its idle fade.
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
        // Floored at JobCombatFloorScale, same as every other reduction
        // mechanic, and fires the same particle burst those get too.
        if (Configuration.GcdReducesScaleEnabled)
        {
            var (gcdOnCooldown, gcdElapsed, _) = jobTracker.GetGcdCooldownState(Configuration.GcdRecastGroup);

            var risingEdge = wasGcdOnCooldown.HasValue && gcdOnCooldown && !wasGcdOnCooldown.Value;
            var elapsedReset = gcdOnCooldown && gcdElapsed.HasValue && lastGcdElapsed.HasValue && gcdElapsed.Value < lastGcdElapsed.Value;

            if (risingEdge || elapsedReset)
            {
                jobCurrentScale = System.Math.Max(
                    jobCurrentScale - Configuration.GcdScaleReductionAmount,
                    Configuration.JobCombatFloorScale);
                hudGauge.Trigger();
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
                        jobCurrentScale = System.Math.Min(jobCurrentScale + Configuration.JumpScaleIncreaseAmount, Configuration.JobUpperLimitScale);
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
        // shakedrink/cackle, which are Job-mode-specific mechanics) since
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

        // Self Sucking auto-attention-swap + burp: while /cackle's drain
        // is active AND scale is at/below CackleDrainFloorScale (the
        // SAME floor the drain mechanic itself is capped at - this used
        // to be a separate SelfSuckingThreshold value, unified here per
        // request since scale literally can't drop below
        // CackleDrainFloorScale via the drain alone, so a separate,
        // potentially-inconsistent threshold made little practical
        // sense - SelfSuckingThreshold has been removed entirely).
        // Deliberately mode-agnostic and freeze-agnostic, same reasoning
        // as always: gating this inside Job-mode logic could leave state
        // stale across a mode switch or death.
        //
        // The /attention swap now repeats once per second for as long
        // as both conditions hold, rather than firing once - per
        // request, /cackle should ALWAYS swap to /attention while at the
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
        // and NOT just because this is the first tick /cackle happened
        // to be checked while already sitting at/below the floor (e.g.
        // /cackle starting while scale was already down there from an
        // earlier session - a plain single-tick falling-edge check
        // couldn't tell that apart from a real fresh drop, since
        // cackleAtOrBelowFloorLastCheck resets every time /cackle stops
        // being active).
        {
            var cackleActiveForThreshold = Configuration.CackleDrainBoostEnabled && emoteLoopTracker.IsCackleActive(Configuration);
            var atOrBelowFloor = jobCurrentScale <= Configuration.CackleDrainFloorScale;

            if (cackleActiveForThreshold && atOrBelowFloor)
            {
                if (!cackleAtOrBelowFloorLastCheck)
                {
                    var wasRecentlyAboveFloor = lastAboveCackleDrainFloorTime >= 0d
                        && ImGuiNowSeconds() - lastAboveCackleDrainFloorTime <= Configuration.SelfSuckingBurpRecentAboveFloorWindowSeconds;

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

                cackleAtOrBelowFloorLastCheck = true;

                if (Configuration.SelfSuckingThresholdAutoAttentionEnabled)
                {
                    var swapCheckNow = ImGuiNowSeconds();
                    if (swapCheckNow - lastCackleAttentionSwapTime >= CackleAttentionSwapIntervalSeconds)
                    {
                        lastCackleAttentionSwapTime = swapCheckNow;
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
                cackleAtOrBelowFloorLastCheck = false;
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
        // Dance, so it doesn't interrupt either of those.
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
                && !guardSuppressed;

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
            // both Provoke and Equilibrium) - either one being used
            // applies its effect, so check every tracked name's rising
            // edge independently and accumulate each fired ability's own
            // configurable multiplier (see JobScale.GetOveruseMultiplier
            // and the Provoke/Equilibrium/Lucid Dreaming/Second Wind
            // sliders in the settings window - these are fully in the
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
                    // Ability-use reductions get a more prominent burst
                    // than damage-taken ones - the "empty the gauge"
                    // payoff moment is meant to feel bigger here.
                    const float abilityUseBurstIntensity = 1.8f;
                    hudGauge.Trigger(abilityUseBurstIntensity);
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
                    // No particle burst here either (same reasoning as
                    // the damage-taken increase variant), but still
                    // genuine player-driven activity.
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
                // Two looping emotes can override this normal growth:
                // /shakedrink dramatically speeds up growth AND forces
                // its ceiling to JobUpperLimitScale (Maximum Scaling In
                // Combat) regardless of actual combat state; /cackle does
                // the mirror opposite, dramatically speeding up a DRAIN
                // toward JobCombatFloorScale (Minimum Scaling) instead of
                // growing at all. Only one can be true at a time (you can
                // only perform one looping emote at once), but cackle is
                // checked first as a defensive tie-break. The instant
                // either emote stops, everything reverts to normal on the
                // very next frame - whatever value was reached simply
                // stays there, it doesn't snap back on its own.
                var cackleDrainActive = Configuration.CackleDrainBoostEnabled && emoteLoopTracker.IsCackleActive(Configuration);
                var shakeDrinkActive = Configuration.ShakeDrinkBoostEnabled && emoteLoopTracker.IsShakeDrinkActive(Configuration);

                if (cackleDrainActive)
                {
                    var drainPerSecond = Configuration.PassiveScaleGenPerSecond * Configuration.CackleDrainRateMultiplier;
                    jobCurrentScale = JobScale.ApplyDrain(jobCurrentScale, Configuration.CackleDrainFloorScale, drainPerSecond, deltaSeconds);

                    // Repeating milk burst (bottle-local only now) while
                    // the drain is actively running, once per second, at
                    // baseline (non-ability-use) intensity.
                    var cackleBurstNow = ImGuiNowSeconds();
                    if (cackleBurstNow - lastCackleBurstTime >= CackleBurstIntervalSeconds)
                    {
                        hudGauge.Trigger();
                        lastCackleBurstTime = cackleBurstNow;
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
                    // throttling needed the way the cackle-drain burst
                    // above needs one). Ordinary unboosted passive
                    // growth deliberately does NOT do this - see the
                    // class doc comment on HudGaugeWindow for why.
                    if (shakeDrinkActive)
                        hudGauge.WakeFromIdle();
                }

                // Extra Scale Gen: a SECOND, independent rate, standalone
                // and NOT multiplicative of any other factor -
                // Configuration.ExtraScaleGenPerSecond is used exactly
                // as configured, never scaled by CackleDrainRateMultiplier,
                // ShakeDrinkGrowthRateMultiplier, or anything else the way
                // PassiveScaleGenPerSecond above is. Gated behind
                // !cackleDrainActive per request, so it can no longer
                // generate ANY scaling change - positive or negative -
                // at the same time /cackle's own drain is actively
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
                // above whenever cackle ISN'T active, since both would
                // be pushing scale in opposite directions within the
                // same tick - that interaction is unchanged, only the
                // cackle-drain case was fixed.
                if (!cackleDrainActive)
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
        customizePlus.RevertChestScale();
        customizePlus.Dispose();
        heartbeatSoundPlayer.Dispose();
        moanSoundPlayer.Dispose();
        burpSoundPlayer.Dispose();
    }
}
