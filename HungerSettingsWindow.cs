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
        ImGui.TextDisabled("Freezes growth and decay in place - independent of the " +
            "main Milk Meter window's own pause, which only affects breast scaling.");

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
