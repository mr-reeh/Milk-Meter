using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace MilkMeter;

/// <summary>
/// Configuration + live monitoring window for the waist/hunger meter -
/// merged in from the standalone Hunger Meter plugin (see Plugin.cs's
/// class doc comment for why), opened via /hungermeter or /food rather
/// than /milkmeter or /milk, and kept entirely separate from
/// SettingsWindow.cs (the original breast-scale settings window) rather
/// than combined into one - the two meters are independently paused
/// (Configuration.WaistScalingPaused vs Configuration.ScalingPaused) and
/// otherwise unrelated feature sets, so a shared window would mostly
/// just be two unrelated sections stacked in one place for no real
/// benefit. Same ImGui Begin/End/SliderFloat pattern as the original
/// Hunger Meter's own SettingsWindow.cs, stripped down to just this
/// meter's five sliders plus a status readout - no Enabled checkbox
/// here (that's the shared master switch, Configuration.Enabled, which
/// still only lives in the main settings window) and no server-info-bar
/// toggle here (the DTR entry is a single COMBINED one now, shown/hidden
/// by Configuration.ShowDtrBarEntry in the main settings window, not a
/// separate per-meter toggle).
/// </summary>
public sealed class HungerSettingsWindow(
    Configuration configuration,
    Func<float> getCurrentWaistScale,
    Func<float> getAppliedWaistScale,
    Func<(bool Active, float? RemainingSeconds)> getFoodState,
    Action resetWaistToBaseline)
{
    public bool IsOpen;

    public void Draw()
    {
        if (!IsOpen)
            return;

        ImGui.SetNextWindowSize(new Vector2(400, 380), ImGuiCond.FirstUseEver);

        if (!ImGui.Begin("Milk Meter - Food/Hunger Settings", ref IsOpen))
        {
            ImGui.End();
            return;
        }

        var scalingPaused = configuration.WaistScalingPaused;
        if (ImGui.Checkbox("Scaling Paused", ref scalingPaused))
        {
            configuration.WaistScalingPaused = scalingPaused;
            configuration.Save();
        }
        ImGui.TextDisabled("Freezes AUTOMATIC growth and decay in place - independent of the " +
            "main Milk Meter window's own pause, which only affects breast scaling. Manual commands " +
            "(/food <number>, /food reset) still work and become visible even while paused.");

        var resetWaistToMinOnDeath = configuration.ResetWaistScaleToMinimumOnDeath;
        if (ImGui.Checkbox("On Death Set Waist Scale to Minimum Scaling", ref resetWaistToMinOnDeath))
        {
            configuration.ResetWaistScaleToMinimumOnDeath = resetWaistToMinOnDeath;
            configuration.Save();
        }
        ImGui.TextDisabled("Waist scale freezes at Minimum while dead - no growth or decay happens - " +
            "until you're revived, then resumes normally.");

        var resetWaistToBaselineOnDeath = configuration.ResetWaistScaleToBaselineOnDeath;
        if (ImGui.Checkbox("On Death Set Waist Scale to Baseline Scaling", ref resetWaistToBaselineOnDeath))
        {
            configuration.ResetWaistScaleToBaselineOnDeath = resetWaistToBaselineOnDeath;
            configuration.Save();
        }
        ImGui.TextDisabled("Alternative to the Minimum option above - freezes at Baseline instead while " +
            "dead, until revived. If both are checked, Minimum wins and this one is skipped.");

        ImGui.Separator();
        ImGui.Text("Scaling Model");

        var waistTransitionRate = configuration.WaistScaleTransitionRate;
        if (ImGui.SliderFloat("Scale Transition Rate (Per Second)", ref waistTransitionRate, 0.00f, 0.50f, "%.3f"))
        {
            configuration.WaistScaleTransitionRate = waistTransitionRate;
            configuration.Save();
        }
        ImGui.TextDisabled("How fast waist scale eases toward its target, in scale units per second - " +
            "so changes animate smoothly instead of snapping. E.g. at 0.05, moving across the default " +
            "0.4 range (0.8-1.2) takes about 8 seconds. Set to 0 for instant snapping. Independent of " +
            "the breast gauge's own transition rate in the main window.");

        var transferDecay = configuration.WaistTransferBonusDecayPerMinute;
        if (ImGui.SliderFloat("Transferred Milk % Decay (Per Minute)", ref transferDecay, 0.0f, 100.0f, "%.1f"))
        {
            configuration.WaistTransferBonusDecayPerMinute = transferDecay;
            configuration.Save();
        }
        ImGui.TextDisabled("Food % granted by \"Transfer Drained Milk % to Food %\" (in the main Milk " +
            "window) is TEMPORARY - it stacks on top of whatever the normal model computes, then fades " +
            "away at this many percentage points per minute. E.g. draining Milk from 200% to 100% " +
            "grants 100 points, which fades over about 5 minutes at the default 20. Set to 0 to make " +
            "transferred Food % permanent instead. Works in both scaling models.");

        var useRemainingTime = configuration.WaistUseRemainingTimeMode;
        if (ImGui.Checkbox("Scale From Remaining Well Fed Time", ref useRemainingTime))
        {
            configuration.WaistUseRemainingTimeMode = useRemainingTime;
            configuration.Save();
        }
        ImGui.TextDisabled("When ON, waist scale is derived purely from how much Well Fed time is left " +
            "right now - no buff at all is Minimum, and 90 minutes left is Maximum, sliding smoothly " +
            "between them as the buff ticks down. Nothing is accumulated or " +
            "saved, so it's automatically correct after logging out, logging back in, or swapping " +
            "characters (the game tracks the buff itself, per character). The server info bar shows " +
            "30 minutes as 100%, 60 as 200%, and 90 as 300%. When OFF, the old model is " +
            "used instead: a running total driven by the Rates section further below. Each model " +
            "completely ignores the other's settings.");

        ImGui.Separator();
        ImGui.Text("Waist Scaling Range");

        var minScale = configuration.WaistMinScale;
        if (ImGui.SliderFloat("Minimum Waist Scaling", ref minScale, 0.10f, 2.00f, "%.2f"))
        {
            if (minScale > configuration.WaistMaxScale)
                minScale = configuration.WaistMaxScale;
            configuration.WaistMinScale = minScale;
            configuration.CurrentWaistScale = WaistScale.Clamp(configuration.CurrentWaistScale, configuration.WaistMinScale, configuration.WaistMaxScale);
            configuration.Save();
        }

        var baselineScale = configuration.WaistBaselineScale;
        if (ImGui.SliderFloat("Baseline Waist Scale", ref baselineScale, 0.10f, 2.00f, "%.2f"))
        {
            configuration.WaistBaselineScale = baselineScale;
            configuration.Save();
        }
        ImGui.TextDisabled("Only used as the starting value on first run, and by the Reset button below - " +
            "not a value scaling is pulled toward. Also the midpoint of the DTR bar's percentage mapping " +
            "(0% at Minimum, 100% here, 200% at Maximum).");

        var maxScale = configuration.WaistMaxScale;
        if (ImGui.SliderFloat("Maximum Waist Scaling", ref maxScale, 0.10f, 3.00f, "%.2f"))
        {
            if (maxScale < configuration.WaistMinScale)
                maxScale = configuration.WaistMinScale;
            configuration.WaistMaxScale = maxScale;
            configuration.CurrentWaistScale = WaistScale.Clamp(configuration.CurrentWaistScale, configuration.WaistMinScale, configuration.WaistMaxScale);
            configuration.Save();
        }

        ImGui.Separator();
        if (configuration.WaistUseRemainingTimeMode)
        {
            ImGui.Text("Rates (unused in this mode)");
            ImGui.TextDisabled("These only apply when \"Scale From Remaining Well Fed Time\" above is " +
                "OFF - the remaining-time model doesn't accumulate anything, so there are no rates to " +
                "tune. Turn that off to use these instead.");
        }
        else
        {
        ImGui.Text("Rates");

        var increasePerHourWhileWellFed = configuration.WaistIncreasePerHourWhileWellFed;
        if (ImGui.SliderFloat("Well Fed Increase Per Hour", ref increasePerHourWhileWellFed, 0.00f, 1.00f, "%.2f"))
        {
            configuration.WaistIncreasePerHourWhileWellFed = increasePerHourWhileWellFed;
            configuration.Save();
        }
        ImGui.TextDisabled("Applies continuously while the Well Fed buff is active - grows toward Maximum " +
            "instead of decaying, mutually exclusive with the reduction rate below.");

        var reductionPerHour = configuration.WaistReductionPerHour;
        if (ImGui.SliderFloat("Well Fed Decrease Per Hour", ref reductionPerHour, 0.00f, 1.00f, "%.2f"))
        {
            configuration.WaistReductionPerHour = reductionPerHour;
            configuration.Save();
        }
        ImGui.TextDisabled("Applies continuously while Well Fed is NOT active - decays toward Minimum.");

        var constantDecreaseEnabled = configuration.WaistConstantDecreaseEnabled;
        if (ImGui.Checkbox("Constant Decrease Regardless of Well Fed", ref constantDecreaseEnabled))
        {
            configuration.WaistConstantDecreaseEnabled = constantDecreaseEnabled;
            configuration.Save();
        }

        var constantDecreasePerHour = configuration.WaistConstantDecreasePerHour;
        if (ImGui.SliderFloat("Constant Decrease Per Hour", ref constantDecreasePerHour, 0.00f, 1.00f, "%.2f"))
        {
            configuration.WaistConstantDecreasePerHour = constantDecreasePerHour;
            configuration.Save();
        }
        ImGui.TextDisabled("A THIRD, independent rate - unlike the two above (only one of which applies " +
            "at a time, based on Well Fed), this drains toward Minimum every hour of real time " +
            "REGARDLESS of Well Fed state, layered additively on top of whichever of the other two just " +
            "applied. Off by default.");

        var foodBumpEnabled = configuration.WaistFoodEatenBumpEnabled;
        if (ImGui.Checkbox("Food Eaten Adds Flat Amount", ref foodBumpEnabled))
        {
            configuration.WaistFoodEatenBumpEnabled = foodBumpEnabled;
            configuration.Save();
        }

        var foodBumpAmount = configuration.WaistFoodEatenBumpAmount;
        if (ImGui.SliderFloat("Food Eaten Bump Amount", ref foodBumpAmount, 0.00f, 1.00f, "%.2f"))
        {
            configuration.WaistFoodEatenBumpAmount = foodBumpAmount;
            configuration.Save();
        }
        ImGui.TextDisabled("Adds this amount once per food item eaten (whether that's your first bite or " +
            "refreshing an already-active buff), ON TOP OF the Well Fed rates above rather than instead " +
            "of them. Eases in gradually rather than jumping instantly - eating several times in a row " +
            "just queues up more to ease in, it doesn't stack as a sudden jump.");

        var rationingBonusEnabled = configuration.WaistRationingManualBumpBonusEnabled;
        if (ImGui.Checkbox("Squadron Rationing Manual Bonus", ref rationingBonusEnabled))
        {
            configuration.WaistRationingManualBumpBonusEnabled = rationingBonusEnabled;
            configuration.Save();
        }

        var rationingMultiplier = configuration.WaistRationingManualBumpMultiplier;
        if (ImGui.SliderFloat("Rationing Manual Bump Multiplier", ref rationingMultiplier, 1.0f, 3.0f, "%.2fx"))
        {
            configuration.WaistRationingManualBumpMultiplier = rationingMultiplier;
            configuration.Save();
        }
        ImGui.TextDisabled("If the \"Rationing\" buff is active at the moment food is eaten (from the " +
            "Squadron Rationing Manual item, or the equivalent Free Company action), the bump amount " +
            "above is multiplied by this instead of applied plain - e.g. at the defaults, 0.10 becomes " +
            "0.15. Only checked at the instant of eating, not continuously.");
        } // end of accumulator-mode-only Rates section

        ImGui.Separator();
        ImGui.Text("PvP (Mana) Scaling");

        var waistPvpEnabled = configuration.WaistPvpManaEnabled;
        if (ImGui.Checkbox("Track Mana During PvP", ref waistPvpEnabled))
        {
            configuration.WaistPvpManaEnabled = waistPvpEnabled;
            configuration.Save();
        }
        ImGui.TextDisabled("When ON, waist scale tracks your MP during PvP matches exactly as breast " +
            "scale does, overriding whichever scaling model is selected above for the duration of the " +
            "match. The Wolves' Den hub doesn't count - only actual matches. On by default.");

        var pvpWaistMin = configuration.PvpWaistManaMinScale;
        if (ImGui.SliderFloat("PvP Minimum Waist Scaling", ref pvpWaistMin, 0.10f, 2.00f, "%.2f"))
        {
            if (pvpWaistMin > configuration.PvpWaistManaMaxScale)
                pvpWaistMin = configuration.PvpWaistManaMaxScale;
            configuration.PvpWaistManaMinScale = pvpWaistMin;
            configuration.Save();
        }

        var pvpWaistMax = configuration.PvpWaistManaMaxScale;
        if (ImGui.SliderFloat("PvP Maximum Waist Scaling", ref pvpWaistMax, 0.10f, 3.00f, "%.2f"))
        {
            if (pvpWaistMax < configuration.PvpWaistManaMinScale)
                pvpWaistMax = configuration.PvpWaistManaMinScale;
            configuration.PvpWaistManaMaxScale = pvpWaistMax;
            configuration.Save();
        }
        ImGui.TextDisabled("The range MP maps onto during PvP - separate from both this window's own " +
            "Minimum/Maximum above AND the breast gauge's own PvP range, so all three are tunable " +
            "independently. Minimum is 0% MP, Maximum is 100% MP (or reversed, if Invert below).");

        var waistInverted = configuration.WaistManaInverted;
        if (ImGui.Checkbox("Invert Waist (Smaller at Full MP)", ref waistInverted))
        {
            configuration.WaistManaInverted = waistInverted;
            configuration.Save();
        }

        var resetWaistOnPvpExit = configuration.ResetWaistScaleOnPvpExit;
        if (ImGui.Checkbox("Return to Baseline When Exiting PvP", ref resetWaistOnPvpExit))
        {
            configuration.ResetWaistScaleOnPvpExit = resetWaistOnPvpExit;
            configuration.Save();
        }
        ImGui.TextDisabled("When ON, leaving a PvP match resets waist scale to Baseline. Note this only " +
            "sticks in the accumulator model - with \"Scale From Remaining Well Fed Time\" on, the very " +
            "next tick recomputes from your actual buff anyway, which is the correct behavior there " +
            "rather than a bug.");

        var pvpWaistDeathCustom = configuration.PvpWaistDeathCustomScaleEnabled;
        if (ImGui.Checkbox("On Death, Set Custom Waist Scale (PvP)", ref pvpWaistDeathCustom))
        {
            configuration.PvpWaistDeathCustomScaleEnabled = pvpWaistDeathCustom;
            configuration.Save();
        }

        var pvpWaistDeathScale = configuration.PvpWaistDeathCustomScale;
        if (ImGui.SliderFloat("PvP Death Waist Scale", ref pvpWaistDeathScale, 0.10f, 3.00f, "%.2f"))
        {
            configuration.PvpWaistDeathCustomScale = pvpWaistDeathScale;
            configuration.Save();
        }
        ImGui.TextDisabled("While dead during a PvP match, forces waist scale to this exact value and " +
            "holds it there regardless of your MP, until you're revived. Separate from the death " +
            "toggles at the top of this window, which don't apply during PvP. Off by default.");

        ImGui.Separator();
        ImGui.Text("Status");

        var current = getCurrentWaistScale();
        var applied = getAppliedWaistScale();
        var percent = ScalePercent.ComputeTwoSegmentPercent(current, configuration.WaistMinScale, configuration.WaistBaselineScale, configuration.WaistMaxScale);
        ImGui.Text($"Current scale: {current:F3} ({percent:F0}%)  (applied to Customize+: {applied:F3})");

        var (foodActive, remaining) = getFoodState();
        ImGui.Text(foodActive
            ? $"Well Fed: active ({remaining:F0}s remaining)"
            : "Well Fed: not active");

        if (ImGui.Button("Reset to Baseline"))
            resetWaistToBaseline();

        ImGui.End();
    }
}
