namespace MilkMeter;

/// <summary>
/// Pure math for the waist/hunger accumulator - originally ported from
/// the standalone Hunger Meter plugin now merged into this one (see
/// Plugin.cs's class doc comment for why: the two plugins independently
/// pushing to Customize+ at the same time was causing them to
/// intermittently erase each other's bone edits, and merging into a
/// single process with one combined push eliminates that race entirely
/// rather than just narrowing it).
///
/// UPDATED per request: the original design applied continuous decay
/// every frame REGARDLESS of Well Fed state, plus a separate flat bump
/// on each detected "food consumed" event (see the git history for
/// ApplyFoodConsumed, since removed). That's been replaced with a
/// simpler, more intuitive continuous rule instead: while Well Fed is
/// currently active, scale grows continuously toward WaistMaxScale
/// (ApplyGrowth); while it's NOT active, scale decays continuously
/// toward WaistMinScale (ApplyDecay) - same as before. No more discrete
/// "did I just eat" event detection needed at all, since it's now
/// purely a function of the buff's current on/off state each frame,
/// not a transition.
///
/// Unlike Milk Meter's own FoodScale (scale = f(remaining Well Fed
/// duration), recomputed fresh every tick from a single live reading),
/// this is a running total: the caller (Plugin.cs) owns the actual
/// float value in Configuration.CurrentWaistScale and calls into
/// ApplyGrowth or ApplyDecay every frame depending on the buff's
/// current state, proportional to elapsed real time. Kept as static,
/// stateless functions (mirroring FoodScale.cs's own shape) so the
/// accumulation logic itself is trivially testable independent of
/// Dalamud, ObjectTable polling, or persistence.
/// </summary>
public static class WaistScale
{
    public const float DefaultMinScale = 0.8f;
    public const float DefaultBaselineScale = 1.0f;
    public const float DefaultMaxScale = 1.2f;
    public const float DefaultIncreasePerHourWhileWellFed = 0.2f;
    public const float DefaultReductionPerHour = 0.2f;

    /// <summary>
    /// Reduces currentScale by (reductionPerHour * elapsedSeconds / 3600),
    /// clamped so it never drops below minScale. Called every frame
    /// Well Fed is NOT active, with that frame's own small
    /// elapsedSeconds (continuous decay, not a once-an-hour step) AND
    /// on startup with however many real seconds passed since
    /// Configuration.LastUpdateUnixSeconds, so a decay rate tuned
    /// per-hour behaves the same whether it's applied in 1/60th-second
    /// slices while playing or in one large catch-up slice after the
    /// game was closed for a while.
    /// </summary>
    public static float ApplyDecay(float currentScale, float minScale, float reductionPerHour, double elapsedSeconds)
    {
        if (elapsedSeconds <= 0d)
            return currentScale;

        var reduction = (float)(reductionPerHour * (elapsedSeconds / 3600.0));
        var result = currentScale - reduction;
        return result < minScale ? minScale : result;
    }

    /// <summary>
    /// Mirror of ApplyDecay, in the opposite direction - increases
    /// currentScale by (increasePerHour * elapsedSeconds / 3600),
    /// clamped so it never exceeds maxScale. Called every frame Well
    /// Fed IS active, same continuous-not-stepped and catch-up-safe
    /// reasoning as ApplyDecay above.
    /// </summary>
    public static float ApplyGrowth(float currentScale, float maxScale, float increasePerHour, double elapsedSeconds)
    {
        if (elapsedSeconds <= 0d)
            return currentScale;

        var increase = (float)(increasePerHour * (elapsedSeconds / 3600.0));
        var result = currentScale + increase;
        return result > maxScale ? maxScale : result;
    }

    /// <summary>
    /// Clamps an arbitrary scale value into [minScale, maxScale] -
    /// used defensively whenever the configured min/max range itself
    /// might have changed since CurrentWaistScale was last written
    /// (e.g. the user drags Maximum below the current live value).
    /// </summary>
    public static float Clamp(float scale, float minScale, float maxScale)
    {
        if (scale < minScale)
            return minScale;
        if (scale > maxScale)
            return maxScale;
        return scale;
    }

    /// <summary>
    /// "Remaining time" mode, per request - an entirely different model
    /// from the ApplyDecay/ApplyGrowth accumulator above. Rather than a
    /// running total mutated a little each frame, this is a PURE
    /// FUNCTION of how much Well Fed time is currently left, recomputed
    /// fresh every tick from a single live reading (much closer in
    /// shape to Milk Meter's own FoodScale.Compute than to the
    /// accumulator this sits alongside):
    ///   - no buff at all        -> minScale
    ///   - baselineMinutes left  -> baselineScale
    ///   - maxMinutes left       -> maxScale
    /// with linear interpolation between those anchors, in two separate
    /// segments (0..baselineMinutes, then baselineMinutes..maxMinutes)
    /// rather than one formula across the whole range - because
    /// baselineScale isn't necessarily the exact midpoint of
    /// minScale/maxScale, and baselineMinutes isn't necessarily the
    /// midpoint of 0/maxMinutes; all five are independently
    /// configurable. Same two-segment shape
    /// ScalePercent.ComputeTwoSegmentPercent uses, just mapping minutes
    /// onto scale rather than scale onto percent.
    ///
    /// PERSISTENCE, and why this mode needs none: the Well Fed status is
    /// tracked by the game itself, not by this plugin, and survives
    /// logout - so on logging back in (or switching characters) the
    /// remaining time read here is simply whatever the game currently
    /// reports for THAT character, automatically correct with no
    /// catch-up math and no saved state. That also quietly fixes a real
    /// limitation of the accumulator mode: Configuration.CurrentWaistScale
    /// is a single value shared across every character on the account,
    /// so alts share one waist scale there, whereas this mode is
    /// inherently per-character since each character has its own status
    /// list.
    ///
    /// Clamped to [minScale, maxScale] at the end regardless of how the
    /// anchors are configured - remaining time above maxMinutes (which
    /// can genuinely happen; see WaistRemainingTimeMaximumMinutes' own
    /// doc comment) holds at maxScale rather than extrapolating past it.
    /// </summary>
    public static float ComputeFromRemainingTime(
        float? remainingSeconds,
        float minScale,
        float baselineScale,
        float maxScale,
        float baselineMinutes,
        float maxMinutes)
    {
        if (remainingSeconds is not { } seconds || seconds <= 0f)
            return minScale;

        var minutes = seconds / 60f;

        // Defensive: a zero-or-negative first segment would divide by
        // zero below, and an out-of-order pair of anchors would produce
        // a nonsensical result rather than just a steeper curve.
        if (baselineMinutes <= 0f)
            return Clamp(baselineScale, minScale, maxScale);

        if (minutes <= baselineMinutes)
        {
            var t = minutes / baselineMinutes;
            return Clamp(minScale + (baselineScale - minScale) * t, minScale, maxScale);
        }

        var secondSegmentSpan = maxMinutes - baselineMinutes;
        if (secondSegmentSpan <= 0f)
            return Clamp(baselineScale, minScale, maxScale);

        var t2 = System.Math.Min(1f, (minutes - baselineMinutes) / secondSegmentSpan);
        return Clamp(baselineScale + (maxScale - baselineScale) * t2, minScale, maxScale);
    }
}
