using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace MilkMeter;

/// <summary>
/// Configuration + live monitoring window. Toggled via /milkmeter
/// config, or the gear icon in the plugin installer (wired to
/// IDalamudPluginInterface.UiBuilder.OpenConfigUi in Plugin.cs).
///
/// Uses Dalamud's own ImGui binding (Dalamud.Bindings.ImGui, derived from
/// Hexa.NET.ImGui as of Dalamud v13+) rather than classic ImGui.NET - the
/// method surface used here (Begin/End, Checkbox, RadioButton,
/// SliderFloat, Text) matches Dalamud's own documented usage pattern.
/// </summary>
public sealed class SettingsWindow(
    Configuration configuration,
    Func<float> getTargetScale,
    Func<float> getAppliedScale,
    Func<(bool Active, float? RemainingSeconds)> getFoodState,
    Func<float?> getManaFraction,
    Func<string?> getJobAbilityName,
    Func<(bool InCombat, float CurrentScale)> getJobLiveState)
{
    public bool IsOpen;

    public void Draw()
    {
        if (!IsOpen)
            return;

        ImGui.SetNextWindowSize(new Vector2(400, 620), ImGuiCond.FirstUseEver);

        if (!ImGui.Begin("Milk Meter Settings", ref IsOpen))
        {
            ImGui.End();
            return;
        }

        var enabled = configuration.Enabled;
        if (ImGui.Checkbox("Enabled", ref enabled))
        {
            configuration.Enabled = enabled;
            configuration.Save();
        }

        var scalingPaused = configuration.ScalingPaused;
        if (ImGui.Checkbox("Scaling Paused", ref scalingPaused))
        {
            configuration.ScalingPaused = scalingPaused;
            configuration.Save();
        }
        ImGui.TextDisabled("Freezes every mechanic that would modify or push chest scale (and " +
            "resets scale to exactly 1.0 the instant it's paused), while " +
            "leaving the HUD gauge and its particle effects still visible, dimmed to 10% opacity " +
            "- unlike unchecking Enabled above, which hides the gauge entirely. The " +
            "threshold effect (vignette/glow/heartbeat) completely stops while paused instead of " +
            "continuing on the frozen value. Also toggleable by left-clicking the HUD gauge itself " +
            "while it's locked in place.");

        ImGui.Separator();
        ImGui.Text("Scale Source");

        if (ImGui.RadioButton("Food Buff", configuration.Mode == ScaleMode.Food))
        {
            configuration.Mode = ScaleMode.Food;
            configuration.Save();
        }

        ImGui.SameLine();
        if (ImGui.RadioButton("Mana", configuration.Mode == ScaleMode.Mana))
        {
            configuration.Mode = ScaleMode.Mana;
            configuration.Save();
        }

        ImGui.SameLine();
        if (ImGui.RadioButton("Job Actions", configuration.Mode == ScaleMode.Job))
        {
            configuration.Mode = ScaleMode.Job;
            configuration.Save();
        }

        if (configuration.Mode == ScaleMode.Mana)
        {
            var inverted = configuration.ManaInverted;
            if (ImGui.Checkbox("Invert (Smaller at Full MP)", ref inverted))
            {
                configuration.ManaInverted = inverted;
                configuration.Save();
            }
        }

        if (configuration.Mode == ScaleMode.Food)
        {
            ImGui.Separator();
            ImGui.Text("Food Scaling Range");

            var minScale = configuration.FoodMinScale;
            if (ImGui.SliderFloat("Minimum (No Buff)", ref minScale, 0.10f, 1.00f, "%.2f"))
            {
                if (minScale > configuration.FoodMaxScale)
                    minScale = configuration.FoodMaxScale;
                configuration.FoodMinScale = minScale;
                configuration.Save();
            }

            var maxScale = configuration.FoodMaxScale;
            if (ImGui.SliderFloat("Maximum (Fresh Buff)", ref maxScale, 1.00f, 2.00f, "%.2f"))
            {
                if (maxScale < configuration.FoodMinScale)
                    maxScale = configuration.FoodMinScale;
                configuration.FoodMaxScale = maxScale;
                configuration.Save();
            }

            // NOTE: this still controls the taper WINDOW (how long before
            // expiry the shrink begins, e.g. 30 min) - not a cap on the
            // food's total duration (which can be up to 90 min and isn't
            // something this plugin needs to know, since the taper only
            // looks at time remaining). Label renamed per request; if you
            // actually want a true duration cap, that's a different
            // feature and this slider's behavior would need to change,
            // not just its name.
            var taperMinutes = configuration.FoodTaperMinutes;
            if (ImGui.SliderFloat("Max Food Duration", ref taperMinutes, 1f, 90f, "%.0f"))
            {
                configuration.FoodTaperMinutes = taperMinutes;
                configuration.Save();
            }
            ImGui.TextDisabled("How long before the buff runs out the shrink begins - not how fast it grows when you eat.");
        }
        else if (configuration.Mode == ScaleMode.Job)
        {
            ImGui.Separator();
            ImGui.Text("Job Actions Scaling");

            var overuseBonus = configuration.JobOveruseBonus;
            if (ImGui.SliderFloat("Base Reduction Per Action", ref overuseBonus, 0.00f, 1.00f, "%.2f"))
            {
                configuration.JobOveruseBonus = overuseBonus;
                configuration.Save();
            }

            var passiveScaleGen = configuration.PassiveScaleGenPerSecond;
            if (ImGui.SliderFloat("Passive Scale Gen (Per Second)", ref passiveScaleGen, -0.10f, 0.10f, "%.3f"))
            {
                configuration.PassiveScaleGenPerSecond = passiveScaleGen;
                configuration.Save();
            }
            ImGui.TextDisabled("How much scale grows per SECOND, directly - 0.05 means +0.05 scale/second. " +
                "Still normally capped at Maximum Scaling (Out of Combat) while out of combat, or " +
                "Maximum Scaling (In Combat) while in combat. NOTE: negative values currently have " +
                "NO distinct effect - 0 or below just means no growth, and this value also feeds " +
                "the /cackle drain rate, which similarly goes inert once this is negative. The whole " +
                "negative half of this slider is currently a dead zone, identical to 0.");

            var extraScaleGen = configuration.ExtraScaleGenPerSecond;
            if (ImGui.SliderFloat("Extra Scale Gen (Per Second)", ref extraScaleGen, -0.10f, 0.10f, "%.3f"))
            {
                configuration.ExtraScaleGenPerSecond = extraScaleGen;
                configuration.Save();
            }
            ImGui.TextDisabled("A SECOND, independent rate on top of the one above - standalone, never " +
                "multiplied by anything else. IGNORES the " +
                "in/out of combat rule entirely. Positive GROWS toward Maximum Scaling (In Combat) " +
                "regardless of actual combat state; negative DRAINS toward Minimum Scaling (Always) " +
                "instead, same combat-independent reasoning in reverse. Off (0) by default. Paused " +
                "entirely while /cackle's drain is active, so it can never fight against that drain " +
                "- outside of that, a negative value here still works against ordinary passive " +
                "growth.");

            var combatFloor = configuration.JobCombatFloorScale;
            if (ImGui.SliderFloat("Minimum Scaling (Always)", ref combatFloor, 0.10f, 1.00f, "%.2f"))
            {
                if (combatFloor > configuration.JobBaselineScale)
                    combatFloor = configuration.JobBaselineScale;
                configuration.JobCombatFloorScale = combatFloor;
                configuration.Save();
            }

            var baseline = configuration.JobBaselineScale;
            if (ImGui.SliderFloat("Maximum Scaling (Out of Combat)", ref baseline, 0.50f, 2.00f, "%.2f"))
            {
                configuration.JobBaselineScale = baseline;
                configuration.Save();
            }

            var upperLimit = configuration.JobUpperLimitScale;
            if (ImGui.SliderFloat("Maximum Scaling (In Combat)", ref upperLimit, 1.00f, 3.00f, "%.2f"))
            {
                configuration.JobUpperLimitScale = upperLimit;
                configuration.Save();
            }

            var resetToFloorOnDeath = configuration.ResetJobScaleToCombatFloorOnDeath;
            if (ImGui.Checkbox("On Death Set Scale to Minimum Scaling", ref resetToFloorOnDeath))
            {
                configuration.ResetJobScaleToCombatFloorOnDeath = resetToFloorOnDeath;
                configuration.Save();
            }
            ImGui.TextDisabled("Scale freezes at Minimum Scaling while dead - no Passive Scale Gen happens - " +
                "until you're revived, then resumes normally.");

            var gcdReducesScale = configuration.GcdReducesScaleEnabled;
            if (ImGui.Checkbox("GCD Affects Scale", ref gcdReducesScale))
            {
                configuration.GcdReducesScaleEnabled = gcdReducesScale;
                configuration.Save();
            }

            var gcdReduction = configuration.GcdScaleReductionAmount;
            if (ImGui.SliderFloat("GCD Scale Amount", ref gcdReduction, -0.25f, 0.25f, "%.3f"))
            {
                configuration.GcdScaleReductionAmount = gcdReduction;
                configuration.Save();
            }

            var gcdRecastGroup = configuration.GcdRecastGroup;
            if (ImGui.InputInt("GCD Recast Group", ref gcdRecastGroup))
            {
                configuration.GcdRecastGroup = gcdRecastGroup;
                configuration.Save();
            }
            ImGui.TextDisabled("The PRIMARY mechanic for fighting the scale back down - fires once per " +
                "GCD (spell or weaponskill), both in AND out of combat. Positive values raise scale " +
                "(just wakes the gauge from idle, no burst), negative values lower it instead " +
                "(and play the milk-droplet burst, same as any other reduction) - either direction is " +
                "clamped between Minimum Scaling and Maximum Scaling (In Combat), always. Negative by " +
                "default so this still fights the scale down out of the box. Recast group default " +
                "(57) confirmed correct via /milkmeter gcddebug's active-group scan - community " +
                "documentation had suggested 58, which was wrong on this client.");

            var increaseOnDamage = configuration.IncreaseScaleOnDamageTaken;
            if (ImGui.Checkbox("Damage Taken Affects Scale", ref increaseOnDamage))
            {
                configuration.IncreaseScaleOnDamageTaken = increaseOnDamage;
                configuration.Save();
            }

            var damageIncrease = configuration.DamageTakenScaleIncrease;
            if (ImGui.SliderFloat("Damage Taken Scale Amount", ref damageIncrease, -0.25f, 0.25f, "%.3f"))
            {
                configuration.DamageTakenScaleIncrease = damageIncrease;
                configuration.Save();
            }
            ImGui.TextDisabled("Fires on every HP decrease while in combat, including DoT ticks (never " +
                "out of combat). Positive values raise scale, negative values lower it - either " +
                "direction is clamped between Minimum Scaling and whichever Maximum Scaling currently " +
                "applies.");

            var triggerCooldown = configuration.DamageTriggerCooldownSeconds;
            if (ImGui.SliderFloat("Damage Trigger Cooldown (Seconds)", ref triggerCooldown, 0.0f, 60.0f, "%.1f"))
            {
                configuration.DamageTriggerCooldownSeconds = triggerCooldown;
                configuration.Save();
            }
            ImGui.TextDisabled("0 = no limit, fires on every HP decrease.");

            var jumpEnabled = configuration.JumpIncreasesScaleEnabled;
            if (ImGui.Checkbox("Jumping Affects Scale", ref jumpEnabled))
            {
                configuration.JumpIncreasesScaleEnabled = jumpEnabled;
                configuration.Save();
            }

            var jumpIncrease = configuration.JumpScaleIncreaseAmount;
            if (ImGui.SliderFloat("Jump Scale Amount", ref jumpIncrease, -0.25f, 0.25f, "%.3f"))
            {
                configuration.JumpScaleIncreaseAmount = jumpIncrease;
                configuration.Save();
            }
            ImGui.TextDisabled("Positive values raise scale per jump, negative values lower it - either " +
                "direction is clamped between Minimum Scaling and Maximum Scaling (In Combat), always, " +
                "same as before.");

            var jumpTriggerCooldown = configuration.JumpTriggerCooldownSeconds;
            if (ImGui.SliderFloat("Jump Trigger Cooldown (Seconds)", ref jumpTriggerCooldown, 0.0f, 5.0f, "%.1f"))
            {
                configuration.JumpTriggerCooldownSeconds = jumpTriggerCooldown;
                configuration.Save();
            }

            var jumpVelocityThreshold = configuration.JumpVelocityThreshold;
            if (ImGui.SliderFloat("Jump Velocity Threshold", ref jumpVelocityThreshold, 0.1f, 5.0f, "%.2f"))
            {
                configuration.JumpVelocityThreshold = jumpVelocityThreshold;
                configuration.Save();
            }
            ImGui.TextDisabled("Fires once per jump (detected as a fresh upward velocity impulse, not " +
                "continuously while airborne), unlike the damage " +
                "taken toggles above this isn't restricted to combat. Always capped at Maximum Scaling " +
                "In Combat. Its own cooldown, separate from Damage Trigger Cooldown above - 0 = no " +
                "limit. Velocity threshold is a GUESSED starting value - use " +
                "/milkmeter jumpdebug to tune it if jumps are missed or over-triggered.");

            var shakeDrinkEnabled = configuration.ShakeDrinkBoostEnabled;
            if (ImGui.Checkbox("Breast Massage (/shakedrink)", ref shakeDrinkEnabled))
            {
                configuration.ShakeDrinkBoostEnabled = shakeDrinkEnabled;
                configuration.Save();
            }

            var shakeDrinkRate = configuration.ShakeDrinkGrowthRateMultiplier;
            if (ImGui.SliderFloat("Shake Drink Growth Rate Multiplier", ref shakeDrinkRate, 1.0f, 30.0f, "%.1fx"))
            {
                configuration.ShakeDrinkGrowthRateMultiplier = shakeDrinkRate;
                configuration.Save();
            }

            var shakeDrinkModeParam = configuration.ShakeDrinkEmoteModeParam;
            if (ImGui.InputInt("Shake Drink ModeParam (-1 = Any Looping Emote)", ref shakeDrinkModeParam))
            {
                configuration.ShakeDrinkEmoteModeParam = shakeDrinkModeParam;
                configuration.Save();
            }
            ImGui.TextDisabled("While /shakedrink is active, Passive Scale Gen speeds up dramatically and " +
                "targets Maximum Scaling (In Combat) even out of combat - reverting instantly once the " +
                "emote stops. Default value (76) confirmed via /milkmeter emotedebug; -1 " +
                "would match ANY looping emote instead, in case a game update ever changes it.");

            var cackleDrainEnabled = configuration.CackleDrainBoostEnabled;
            if (ImGui.Checkbox("Self Sucking Drain (/cackle)", ref cackleDrainEnabled))
            {
                configuration.CackleDrainBoostEnabled = cackleDrainEnabled;
                configuration.Save();
            }

            var cackleDrainRate = configuration.CackleDrainRateMultiplier;
            if (ImGui.SliderFloat("Cackle Drain Rate Multiplier", ref cackleDrainRate, 1.0f, 30.0f, "%.1fx"))
            {
                configuration.CackleDrainRateMultiplier = cackleDrainRate;
                configuration.Save();
            }

            var cackleDrainFloor = configuration.CackleDrainFloorScale;
            if (ImGui.SliderFloat("Cackle Drain Floor", ref cackleDrainFloor, 0.10f, 2.00f, "%.2f"))
            {
                configuration.CackleDrainFloorScale = cackleDrainFloor;
                configuration.Save();
            }

            var cackleModeParam = configuration.CackleEmoteModeParam;
            if (ImGui.InputInt("Cackle ModeParam (-1 = Any Looping Emote)", ref cackleModeParam))
            {
                configuration.CackleEmoteModeParam = cackleModeParam;
                configuration.Save();
            }
            ImGui.TextDisabled("Mirror opposite of Shake Drink: while /cackle is active, scale drains " +
                "dramatically toward Cackle Drain Floor (a separate value from Minimum Scaling) instead " +
                "of growing - reverting instantly once the emote stops. ModeParam default (98) " +
                "confirmed via /milkmeter emotedebug.");

            var waterDrainEnabled = configuration.WaterDrainBoostEnabled;
            if (ImGui.Checkbox("Breast Feeding Drain (/water)", ref waterDrainEnabled))
            {
                configuration.WaterDrainBoostEnabled = waterDrainEnabled;
                configuration.Save();
            }

            var waterDrainRate = configuration.WaterDrainRateMultiplier;
            if (ImGui.SliderFloat("Water Drain Rate Multiplier", ref waterDrainRate, 1.0f, 30.0f, "%.1fx"))
            {
                configuration.WaterDrainRateMultiplier = waterDrainRate;
                configuration.Save();
            }

            var waterDrainFloor = configuration.WaterDrainFloorScale;
            if (ImGui.SliderFloat("Water Drain Floor", ref waterDrainFloor, 0.10f, 2.00f, "%.2f"))
            {
                configuration.WaterDrainFloorScale = waterDrainFloor;
                configuration.Save();
            }

            var waterModeParam = configuration.WaterEmoteModeParam;
            if (ImGui.InputInt("Water ModeParam (-1 = Any Looping Emote)", ref waterModeParam))
            {
                configuration.WaterEmoteModeParam = waterModeParam;
                configuration.Save();
            }
            ImGui.TextDisabled("A second, independent drain alongside Self Sucking Drain above: while " +
                "/water is active, scale drains dramatically toward Water Drain Floor (its own " +
                "separate value) instead of growing - reverting instantly once the emote stops. Only " +
                "one looping emote can be active at a time, so this and Self Sucking Drain never run " +
                "simultaneously. ModeParam default (75) confirmed via /milkmeter emotedebug.");

            var selfSuckingAutoAttention = configuration.SelfSuckingThresholdAutoAttentionEnabled;
            if (ImGui.Checkbox("Self Sucking Threshold (/cackle to /attention)", ref selfSuckingAutoAttention))
            {
                configuration.SelfSuckingThresholdAutoAttentionEnabled = selfSuckingAutoAttention;
                configuration.Save();
            }

            var selfSuckingBurpDelay = configuration.SelfSuckingBurpDelaySeconds;
            if (ImGui.SliderFloat("Self Sucking Burp Delay (Seconds)", ref selfSuckingBurpDelay, 0.0f, 3.0f, "%.1f"))
            {
                configuration.SelfSuckingBurpDelaySeconds = selfSuckingBurpDelay;
                configuration.Save();
            }

            var selfSuckingBurpWindow = configuration.SelfSuckingBurpRecentAboveFloorWindowSeconds;
            if (ImGui.SliderFloat("Self Sucking Burp Recency Window (Seconds)", ref selfSuckingBurpWindow, 0.5f, 15.0f, "%.1f"))
            {
                configuration.SelfSuckingBurpRecentAboveFloorWindowSeconds = selfSuckingBurpWindow;
                configuration.Save();
            }
            ImGui.TextDisabled("While /cackle is active and scale is at or below Cackle Drain Floor (set " +
                "above, no longer a separate value here), repeatedly forces \"/attention motion\" once " +
                "per second for as long as both hold - not just once. The burp sound only plays if " +
                "scale was above the floor within this many seconds beforehand - not if /cackle starts " +
                "while scale is already sitting at/below the floor.");

            var attentionEnabled = configuration.AttentionWakeEnabled;
            if (ImGui.Checkbox("Check Breasts (/attention)", ref attentionEnabled))
            {
                configuration.AttentionWakeEnabled = attentionEnabled;
                configuration.Save();
            }

            var attentionModeParam = configuration.AttentionEmoteModeParam;
            if (ImGui.InputInt("Attention ModeParam (-1 = Any Looping Emote)", ref attentionModeParam))
            {
                configuration.AttentionEmoteModeParam = attentionModeParam;
                configuration.Save();
            }
            ImGui.TextDisabled("While /attention is active, wakes the HUD gauge from its idle fade - " +
                "doesn't touch the scale itself at all, purely a way to check the gauge's current status " +
                "without needing to use an ability or take damage first. ModeParam default (29) " +
                "confirmed via /milkmeter emotedebug.");

            var guardEnabled = configuration.GuardAutoTriggerEnabled;
            if (ImGui.Checkbox("Guard Auto-Trigger", ref guardEnabled))
            {
                configuration.GuardAutoTriggerEnabled = guardEnabled;
                configuration.Save();
            }

            var guardThreshold = configuration.GuardThresholdScale;
            if (ImGui.SliderFloat("Guard Threshold", ref guardThreshold, 0.10f, 3.00f, "%.2f"))
            {
                configuration.GuardThresholdScale = guardThreshold;
                configuration.Save();
            }

            var guardModeParam = configuration.GuardEmoteModeParam;
            if (ImGui.InputInt("Guard ModeParam (-1 = Any Looping Emote)", ref guardModeParam))
            {
                configuration.GuardEmoteModeParam = guardModeParam;
                configuration.Save();
            }
            ImGui.TextDisabled("Forces \"/guard motion\" once per second for as long as scale is at or " +
                "above the threshold above AND you're standing still - both at once, repeating on its " +
                "own rather than firing just once. ModeParam " +
                "default (58) confirmed via /milkmeter emotedebug.");

            var guardWakeEnabled = configuration.GuardWakeEnabled;
            if (ImGui.Checkbox("Guard Wakes HUD Gauge", ref guardWakeEnabled))
            {
                configuration.GuardWakeEnabled = guardWakeEnabled;
                configuration.Save();
            }
            ImGui.TextDisabled("While /guard is active (whether forced above or performed manually), " +
                "wakes the HUD gauge from its idle fade - same as Check Breasts (/attention) does.");

            var charmedModeParam = configuration.CharmedEmoteModeParam;
            if (ImGui.InputInt("Charmed ModeParam", ref charmedModeParam))
            {
                configuration.CharmedEmoteModeParam = charmedModeParam;
                configuration.Save();
            }

            var ballDanceModeParam = configuration.BallDanceEmoteModeParam;
            if (ImGui.InputInt("Ball Dance ModeParam", ref ballDanceModeParam))
            {
                configuration.BallDanceEmoteModeParam = ballDanceModeParam;
                configuration.Save();
            }
            ImGui.TextDisabled("The Guard Auto-Trigger above is suppressed entirely while either of these " +
                "is active, so it doesn't interrupt them. Defaults: Charmed = 33, Ball Dance = 6.");

            ImGui.TextDisabled("Ability Multipliers (Base Reduction x Multiplier = Amount Subtracted Per Use - " +
                "negative flips it to an increase instead)");

            var provokeMultiplier = configuration.ProvokeMultiplier;
            if (ImGui.SliderFloat("Provoke", ref provokeMultiplier, -5.0f, 5.0f, "%.2f"))
            {
                configuration.ProvokeMultiplier = provokeMultiplier;
                configuration.Save();
            }

            var equilibriumMultiplier = configuration.EquilibriumMultiplier;
            if (ImGui.SliderFloat("Equilibrium", ref equilibriumMultiplier, -5.0f, 5.0f, "%.2f"))
            {
                configuration.EquilibriumMultiplier = equilibriumMultiplier;
                configuration.Save();
            }

            var lucidDreamingMultiplier = configuration.LucidDreamingMultiplier;
            if (ImGui.SliderFloat("Lucid Dreaming", ref lucidDreamingMultiplier, -5.0f, 5.0f, "%.2f"))
            {
                configuration.LucidDreamingMultiplier = lucidDreamingMultiplier;
                configuration.Save();
            }

            var secondWindMultiplier = configuration.SecondWindMultiplier;
            if (ImGui.SliderFloat("Second Wind", ref secondWindMultiplier, -5.0f, 5.0f, "%.2f"))
            {
                configuration.SecondWindMultiplier = secondWindMultiplier;
                configuration.Save();
            }

            var reprisalMultiplier = configuration.ReprisalMultiplier;
            if (ImGui.SliderFloat("Reprisal", ref reprisalMultiplier, -5.0f, 5.0f, "%.2f"))
            {
                configuration.ReprisalMultiplier = reprisalMultiplier;
                configuration.Save();
            }

            ImGui.TextDisabled("Using a tracked ability subtracts (Base Reduction Per Action x that ability's " +
                "own multiplier above) from your current scale, stacking if you spam it - so overusing your " +
                "job action can shrink size all the way down to Minimum Scaling. A negative multiplier flips " +
                "that ability's effect into an INCREASE instead, capped at whichever Maximum Scaling currently " +
                "applies rather than Minimum Scaling - and only a STRICTLY positive multiplier plays the " +
                "milk-droplet burst; a multiplier sitting at exactly 0.00 (a no-op) or a negative one (the " +
                "increasing case) both stay silent. Passive Scale Gen continuously " +
                "grows scale back up over " +
                "time: toward Maximum Scaling (In Combat) while fighting, or only up to Maximum Scaling (Out " +
                "of Combat) once out of combat - but only from BELOW that ceiling. Entering/leaving combat " +
                "while already above the new ceiling does NOT pull you back down to it; size stays exactly " +
                "where it was until you use the ability again.");
        }

        ImGui.Separator();
        ImGui.Text("Transition Smoothing");

        var transitionRate = configuration.ScaleTransitionRate;
        if (ImGui.SliderFloat("Speed (Scale/Second)", ref transitionRate, 0.01f, 1.00f, "%.3f"))
        {
            configuration.ScaleTransitionRate = transitionRate;
            configuration.Save();
        }

        var (currentMin, currentMax) = configuration.Mode switch
        {
            ScaleMode.Job => (configuration.JobCombatFloorScale, configuration.JobUpperLimitScale),
            _ => (configuration.FoodMinScale, configuration.FoodMaxScale),
        };
        var range = System.MathF.Abs(currentMax - currentMin);
        var transitionSeconds = transitionRate > 0f ? range / transitionRate : 0f;
        ImGui.TextDisabled($"At this speed, going from min to max takes about {transitionSeconds:F1}s.");

        ImGui.Separator();
        ImGui.Text("Live Status");

        ImGui.Text($"Target Scale: {getTargetScale():F3}");
        ImGui.Text($"Applied Scale (Animating): {getAppliedScale():F3}");

        if (configuration.Mode == ScaleMode.Food)
        {
            var (active, remaining) = getFoodState();
            if (active && remaining is not null)
            {
                var minutes = (int)(remaining.Value / 60f);
                var seconds = (int)(remaining.Value % 60f);
                ImGui.Text($"Well Fed Remaining: {minutes:D2}:{seconds:D2}");
            }
            else
            {
                ImGui.Text("Well Fed: Not Active");
            }
        }
        else if (configuration.Mode == ScaleMode.Job)
        {
            var abilityName = getJobAbilityName();
            if (abilityName is null)
            {
                ImGui.Text("Current Job: Not Tracked");
                ImGui.TextDisabled("Switch to any combat job to use this mode.");
            }
            else
            {
                ImGui.Text($"Tracked: {abilityName}");

                var (inCombat, currentScale) = getJobLiveState();
                var ceiling = inCombat ? configuration.JobUpperLimitScale : configuration.JobBaselineScale;
                ImGui.Text(inCombat ? "In Combat" : "Not In Combat");
                ImGui.Text($"Current Job Scale: {currentScale:F3}");
                ImGui.Text($"Minimum Scaling: {configuration.JobCombatFloorScale:F2}  |  Applicable Maximum: {ceiling:F2}");
            }
        }
        else
        {
            var mana = getManaFraction();
            ImGui.Text(mana is null ? "MP: Unavailable" : $"MP: {mana.Value * 100f:F0}%");
        }

        ImGui.Separator();
        ImGui.Text("HUD Gauge");

        var showHud = configuration.ShowHudGauge;
        if (ImGui.Checkbox("Show HUD Gauge", ref showHud))
        {
            configuration.ShowHudGauge = showHud;
            configuration.Save();
        }

        var showMilkBurst = configuration.ShowMilkBurstEffect;
        if (ImGui.Checkbox("Milk Burst Effect on Reduction", ref showMilkBurst))
        {
            configuration.ShowMilkBurstEffect = showMilkBurst;
            configuration.Save();
        }
        ImGui.TextDisabled("A brief burst of droplets around the bottle every time the gauge is genuinely " +
            "reduced - a tracked ability's own reduction, a negative-amount Damage Taken/Jumping Affects " +
            "Scale, or the repeating burst while /cackle's drain is active.");

        var hudFadeOnIdle = configuration.HudFadeOnIdleEnabled;
        if (ImGui.Checkbox("Fade Out When Idle", ref hudFadeOnIdle))
        {
            configuration.HudFadeOnIdleEnabled = hudFadeOnIdle;
            configuration.Save();
        }

        var hudFadeIdleSeconds = configuration.HudFadeIdleSeconds;
        if (ImGui.SliderFloat("Fade Out After (Seconds)", ref hudFadeIdleSeconds, 1.0f, 30.0f, "%.1f"))
        {
            configuration.HudFadeIdleSeconds = hudFadeIdleSeconds;
            configuration.Save();
        }

        var hudFadeDuration = configuration.HudFadeDurationSeconds;
        if (ImGui.SliderFloat("Fade Transition Duration (Seconds)", ref hudFadeDuration, 0.0f, 3.0f, "%.1f"))
        {
            configuration.HudFadeDurationSeconds = hudFadeDuration;
            configuration.Save();
        }
        ImGui.TextDisabled("Fades out after this long with no genuine activity - passively generating " +
            "gauge from ordinary growth alone still counts as idle and will fade. Ability use, " +
            "damage-taken events, an active Self Sucking/Shake Drink boost, or the /attention emote all " +
            "count as activity and fade it back in instantly. 0 duration means an instant snap instead " +
            "of an eased fade.");

        var hudShowAboveScaleEnabled = configuration.HudShowAboveScaleEnabled;
        if (ImGui.Checkbox("Show When Scale Reaches Threshold", ref hudShowAboveScaleEnabled))
        {
            configuration.HudShowAboveScaleEnabled = hudShowAboveScaleEnabled;
            configuration.Save();
        }

        var hudShowAboveScaleThreshold = configuration.HudShowAboveScaleThreshold;
        if (ImGui.SliderFloat("Show Above Scale", ref hudShowAboveScaleThreshold, 0.10f, 3.00f, "%.2f"))
        {
            configuration.HudShowAboveScaleThreshold = hudShowAboveScaleThreshold;
            configuration.Save();
        }
        ImGui.TextDisabled("While the applied scale is at or above this value, the gauge is kept awake " +
            "(or woken back up if already faded) the same way an ability use or /attention would - " +
            "independent of the idle-fade settings above, so it works even while HudFadeOnIdleEnabled " +
            "would otherwise have hidden it by now.");

        var hudHideOutOfCombat = configuration.HudHideOutOfCombat;
        if (ImGui.Checkbox("Hide Entirely Out of Combat", ref hudHideOutOfCombat))
        {
            configuration.HudHideOutOfCombat = hudHideOutOfCombat;
            configuration.Save();
        }
        ImGui.TextDisabled("Independent of the idle fade above - either one can hide the gauge, both need " +
            "to want it visible for it to show.");

        ImGui.Separator();
        ImGui.Text("Threshold Effect");

        var thresholdEffectEnabled = configuration.ThresholdEffectEnabled;
        if (ImGui.Checkbox("Enable Threshold Effect", ref thresholdEffectEnabled))
        {
            configuration.ThresholdEffectEnabled = thresholdEffectEnabled;
            configuration.Save();
        }
        ImGui.TextDisabled("A full-screen pink hazy vignette (plus an optional looping heartbeat sound) " +
            "whose intensity ramps continuously between the two scale values below - 0% at the start, " +
            "100% at the end. Purely cosmetic - drawn " +
            "on top of the game frame, never touches input, movement, or anything mechanical.");

        var thresholdEffectRampStart = configuration.ThresholdEffectRampStartScale;
        if (ImGui.SliderFloat("Threshold Effect Ramp Start", ref thresholdEffectRampStart, 0.10f, 3.00f, "%.2f"))
        {
            configuration.ThresholdEffectRampStartScale = thresholdEffectRampStart;
            configuration.Save();
        }

        var thresholdEffectRampEnd = configuration.ThresholdEffectRampEndScale;
        if (ImGui.SliderFloat("Threshold Effect Ramp End", ref thresholdEffectRampEnd, 0.10f, 3.00f, "%.2f"))
        {
            configuration.ThresholdEffectRampEndScale = thresholdEffectRampEnd;
            configuration.Save();
        }

        var thresholdEffectFade = configuration.ThresholdEffectFadeSeconds;
        if (ImGui.SliderFloat("Threshold Effect Fade (Seconds)", ref thresholdEffectFade, 0.0f, 5.0f, "%.1f"))
        {
            configuration.ThresholdEffectFadeSeconds = thresholdEffectFade;
            configuration.Save();
        }

        var thresholdEffectIntensity = configuration.ThresholdEffectIntensity;
        if (ImGui.SliderFloat("Threshold Effect Intensity", ref thresholdEffectIntensity, 0.0f, 1.0f, "%.2f"))
        {
            configuration.ThresholdEffectIntensity = thresholdEffectIntensity;
            configuration.Save();
        }

        var thresholdEffectSize = configuration.ThresholdEffectVignetteSize;
        if (ImGui.SliderFloat("Threshold Effect Vignette Size", ref thresholdEffectSize, 0.05f, 0.60f, "%.2f"))
        {
            configuration.ThresholdEffectVignetteSize = thresholdEffectSize;
            configuration.Save();
        }

        var thresholdEffectColor = new Vector3(configuration.ThresholdEffectColorR, configuration.ThresholdEffectColorG, configuration.ThresholdEffectColorB);
        if (ImGui.ColorEdit3("Threshold Effect Color", ref thresholdEffectColor))
        {
            configuration.ThresholdEffectColorR = thresholdEffectColor.X;
            configuration.ThresholdEffectColorG = thresholdEffectColor.Y;
            configuration.ThresholdEffectColorB = thresholdEffectColor.Z;
            configuration.Save();
        }

        var thresholdEffectHeartbeatSound = configuration.ThresholdEffectHeartbeatSoundEnabled;
        if (ImGui.Checkbox("Threshold Effect Heartbeat Sound", ref thresholdEffectHeartbeatSound))
        {
            configuration.ThresholdEffectHeartbeatSoundEnabled = thresholdEffectHeartbeatSound;
            configuration.Save();
        }

        var thresholdEffectHeartbeatSoundThreshold = configuration.ThresholdEffectHeartbeatSoundThreshold;
        if (ImGui.SliderFloat("Threshold Effect Heartbeat Sound Threshold", ref thresholdEffectHeartbeatSoundThreshold, 0.10f, 3.00f, "%.2f"))
        {
            configuration.ThresholdEffectHeartbeatSoundThreshold = thresholdEffectHeartbeatSoundThreshold;
            configuration.Save();
        }
        ImGui.TextDisabled("A looping heartbeat sound that starts once scaling reaches this value - " +
            "on/off only, independent of the vignette's own ramp start/end above, and independent " +
            "of the separate moan sound below.");

        var thresholdEffectMoanSound = configuration.ThresholdEffectMoanSoundEnabled;
        if (ImGui.Checkbox("Threshold Effect Moan Sound", ref thresholdEffectMoanSound))
        {
            configuration.ThresholdEffectMoanSoundEnabled = thresholdEffectMoanSound;
            configuration.Save();
        }

        var thresholdEffectMoanSoundThreshold = configuration.ThresholdEffectMoanSoundThreshold;
        if (ImGui.SliderFloat("Threshold Effect Moan Sound Threshold", ref thresholdEffectMoanSoundThreshold, 0.10f, 3.00f, "%.2f"))
        {
            configuration.ThresholdEffectMoanSoundThreshold = thresholdEffectMoanSoundThreshold;
            configuration.Save();
        }
        ImGui.TextDisabled("A separate looping sound that starts once scaling reaches this value - " +
            "on/off only, independent of the vignette's own ramp start/end above, and independent " +
            "of the heartbeat sound above. Used to be mixed into the same file as the heartbeat " +
            "sound; split into its own file and threshold per request.");

        var thresholdEffectGlow = configuration.ThresholdEffectGlowEnabled;
        if (ImGui.Checkbox("Threshold Effect Glow", ref thresholdEffectGlow))
        {
            configuration.ThresholdEffectGlowEnabled = thresholdEffectGlow;
            configuration.Save();
        }

        var thresholdEffectGlowIntensity = configuration.ThresholdEffectGlowIntensity;
        if (ImGui.SliderFloat("Threshold Effect Glow Intensity", ref thresholdEffectGlowIntensity, 0.0f, 1.0f, "%.2f"))
        {
            configuration.ThresholdEffectGlowIntensity = thresholdEffectGlowIntensity;
            configuration.Save();
        }

        var thresholdEffectGlowSpeed = configuration.ThresholdEffectGlowPulseSpeed;
        if (ImGui.SliderFloat("Threshold Effect Glow Pulse Speed", ref thresholdEffectGlowSpeed, 0.1f, 5.0f, "%.1f"))
        {
            configuration.ThresholdEffectGlowPulseSpeed = thresholdEffectGlowSpeed;
            configuration.Save();
        }

        var thresholdEffectGlowSize = configuration.ThresholdEffectGlowSize;
        if (ImGui.SliderFloat("Threshold Effect Glow Size", ref thresholdEffectGlowSize, 0.5f, 10.0f, "%.1f"))
        {
            configuration.ThresholdEffectGlowSize = thresholdEffectGlowSize;
            configuration.Save();
        }
        ImGui.TextDisabled("A soft radiating glow centered on the bottle, pulsing rhythmically (not a " +
            "literal strobe) whenever the ramp is active - same color as the vignette above.");

        var hudLocked = configuration.HudLocked;
        if (ImGui.Checkbox("Lock HUD Position", ref hudLocked))
        {
            configuration.HudLocked = hudLocked;
            configuration.Save(); // capture wherever it was dragged to before re-locking
        }
        ImGui.TextDisabled("Uncheck to drag the on-screen gauge somewhere else, then re-check to lock it in place.");

        var hudScale = configuration.HudScale;
        if (ImGui.SliderFloat("HUD Scale", ref hudScale, 0.3f, 3.0f, "%.2f"))
        {
            configuration.HudScale = hudScale;
            configuration.Save();
        }
        ImGui.TextDisabled("Scales the whole bottle - nipple, cap, and body all together.");

        var hudLabelText = configuration.HudLabelText;
        if (ImGui.InputText("Label Text", ref hudLabelText, 32))
        {
            configuration.HudLabelText = hudLabelText;
            configuration.Save();
        }
        ImGui.TextDisabled("Overlaid on the center of the bottle in bold outlined white text, like the game's own HP/MP labels. Blank hides it entirely.");

        var hudLabelFontScale = configuration.HudLabelFontScale;
        if (ImGui.SliderFloat("Text Size", ref hudLabelFontScale, 0.5f, 3.0f, "%.2f"))
        {
            configuration.HudLabelFontScale = hudLabelFontScale;
            configuration.Save();
        }

        var hudGauge1Start = configuration.HudGauge1StartScale;
        if (ImGui.SliderFloat("Gauge 1 Start", ref hudGauge1Start, 0.10f, 3.00f, "%.2f"))
        {
            configuration.HudGauge1StartScale = hudGauge1Start;
            configuration.Save();
        }

        var hudGauge1End = configuration.HudGauge1EndScale;
        if (ImGui.SliderFloat("Gauge 1 End", ref hudGauge1End, 0.10f, 3.00f, "%.2f"))
        {
            configuration.HudGauge1EndScale = hudGauge1End;
            configuration.Save();
        }

        var hudGauge2Start = configuration.HudGauge2StartScale;
        if (ImGui.SliderFloat("Gauge 2 Start", ref hudGauge2Start, 0.10f, 3.00f, "%.2f"))
        {
            configuration.HudGauge2StartScale = hudGauge2Start;
            configuration.Save();
        }

        var hudGauge2End = configuration.HudGauge2EndScale;
        if (ImGui.SliderFloat("Gauge 2 End", ref hudGauge2End, 0.10f, 3.00f, "%.2f"))
        {
            configuration.HudGauge2EndScale = hudGauge2End;
            configuration.Save();
        }

        var hudGauge3Start = configuration.HudGauge3StartScale;
        if (ImGui.SliderFloat("Gauge 3 Start", ref hudGauge3Start, 0.10f, 3.00f, "%.2f"))
        {
            configuration.HudGauge3StartScale = hudGauge3Start;
            configuration.Save();
        }

        var hudGauge3End = configuration.HudGauge3EndScale;
        if (ImGui.SliderFloat("Gauge 3 End", ref hudGauge3End, 0.10f, 3.00f, "%.2f"))
        {
            configuration.HudGauge3EndScale = hudGauge3End;
            configuration.Save();
        }
        ImGui.TextDisabled("Job mode only. All six are purely visual thresholds for the HUD gauge - " +
            "fully independent of each other and of Minimum/Maximum Scaling, which still govern the " +
            "actual growth mechanics elsewhere. Can overlap, leave a gap, or run in either direction. " +
            "Gauge 3 specifically is INVERTED and fills top-down, unlike Gauges 1 and 2: it's fully " +
            "filled at or below its own Start, empty at or above its own End - the opposite direction " +
            "from Gauges 1 and 2, and its fill grows downward from the top of the bottle rather than " +
            "upward from the bottom.");

        var hudFillColor = new Vector3(configuration.HudFillColorR, configuration.HudFillColorG, configuration.HudFillColorB);
        if (ImGui.ColorEdit3("Fill Color (Gauge 1)", ref hudFillColor))
        {
            configuration.HudFillColorR = hudFillColor.X;
            configuration.HudFillColorG = hudFillColor.Y;
            configuration.HudFillColorB = hudFillColor.Z;
            configuration.Save();
        }

        var hudFillColor2 = new Vector3(configuration.HudFillColor2R, configuration.HudFillColor2G, configuration.HudFillColor2B);
        if (ImGui.ColorEdit3("Fill Color (Gauge 2)", ref hudFillColor2))
        {
            configuration.HudFillColor2R = hudFillColor2.X;
            configuration.HudFillColor2G = hudFillColor2.Y;
            configuration.HudFillColor2B = hudFillColor2.Z;
            configuration.Save();
        }

        var hudFillColor3 = new Vector3(configuration.HudFillColor3R, configuration.HudFillColor3G, configuration.HudFillColor3B);
        if (ImGui.ColorEdit3("Fill Color (Gauge 3)", ref hudFillColor3))
        {
            configuration.HudFillColor3R = hudFillColor3.X;
            configuration.HudFillColor3G = hudFillColor3.Y;
            configuration.HudFillColor3B = hudFillColor3.Z;
            configuration.Save();
        }
        ImGui.TextDisabled("Job mode only: all three colors span the whole bottle, overlaid on top of " +
            "each other - Gauge 2 covers Gauge 1 once scaling reaches Gauge 2's own start, and Gauge 3 " +
            "(inverted, filling top-down as scaling DROPS toward its own start) covers BOTH once " +
            "scaling falls that low. Food and " +
            "Mana modes always use just Gauge 1's color.");

        ImGui.End();
    }
}
