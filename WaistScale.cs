namespace MilkMeter;

/// <summary>
/// Pure math for the waist/hunger accumulator - ported essentially
/// unchanged from the standalone Hunger Meter plugin now merged into
/// this one (see Plugin.cs's class doc comment for why: the two
/// plugins independently pushing to Customize+ at the same time was
/// causing them to intermittently erase each other's bone edits, and
/// merging into a single process with one combined push eliminates
/// that race entirely rather than just narrowing it).
///
/// Unlike Milk Meter's own FoodScale (scale = f(remaining Well Fed
/// duration), recomputed fresh every tick from a single live reading),
/// this is a running total: the caller (Plugin.cs) owns the actual
/// float value in Configuration.CurrentWaistScale and calls into these
/// two functions to mutate it -
///   - ApplyDecay every frame, proportional to elapsed real time
///   - ApplyFoodConsumed once per detected food-consumed edge
/// Kept as static, stateless functions (mirroring FoodScale.cs's own
/// shape) so the accumulation logic itself is trivially testable
/// independent of Dalamud, ObjectTable polling, or persistence.
/// </summary>
public static class WaistScale
{
    public const float DefaultMinScale = 0.8f;
    public const float DefaultBaselineScale = 1.0f;
    public const float DefaultMaxScale = 1.2f;
    public const float DefaultIncreasePerFood = 0.1f;
    public const float DefaultReductionPerHour = 0.2f;

    /// <summary>
    /// Reduces currentScale by (reductionPerHour * elapsedSeconds / 3600),
    /// clamped so it never drops below minScale. Called every frame with
    /// that frame's own small elapsedSeconds (continuous decay, not a
    /// once-an-hour step) AND on startup with however many real seconds
    /// passed since Configuration.LastUpdateUnixSeconds, so a decay rate
    /// tuned per-hour behaves the same whether it's applied in 1/60th-
    /// second slices while playing or in one large catch-up slice after
    /// the game was closed for a while.
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
    /// Adds increasePerFood to currentScale once per call - callers are
    /// responsible for calling this exactly once per detected
    /// food-consumed edge, not once per frame the food buff happens to
    /// be active (see Plugin.cs's edge detection). Clamped so it never
    /// exceeds maxScale.
    /// </summary>
    public static float ApplyFoodConsumed(float currentScale, float maxScale, float increasePerFood)
    {
        var result = currentScale + increasePerFood;
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
}
