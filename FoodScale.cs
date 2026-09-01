namespace MilkMeter;

/// <summary>
/// The actual "concept swap" from the original ManaMune lives here.
/// Instead of scale = f(currentMP / maxMP), scale = f(remaining Well Fed duration).
///
/// Rules:
///   - No food buff at all                 -> minimum size
///   - Buff freshly consumed / at or above
///     the taper window remaining          -> maximum size
///   - Inside the taper window             -> linear taper from max down to min
///   - Buff expires (0:00 remaining)       -> minimum size
///
/// This works correctly regardless of the buff's total duration (30/45/90
/// minutes, whatever the food item grants) - the taper only cares about
/// remaining time, not how long the buff originally was, so it holds at
/// max size for however long the buff has left above the taper window.
///
/// Min/max scale and the taper window length are configurable at runtime
/// (see Configuration.FoodMinScale/FoodMaxScale/FoodTaperMinutes and the
/// settings window) - the constants here are just the defaults used to
/// seed a new Configuration.
/// </summary>
public static class FoodScale
{
    public const float DefaultMinScale = 0.75f;
    public const float DefaultMaxScale = 1.00f;

    /// <summary>Default taper window, in minutes, before buff expiry.</summary>
    public const float DefaultTaperMinutes = 30f;

    /// <summary>
    /// Compute the chest scale for a given amount of remaining Well Fed
    /// time. Pass null (or &lt;= 0) when there's no active food buff -
    /// this covers both natural expiry and the player manually removing
    /// the buff, since both look identical from a polling perspective
    /// (the status is simply no longer present either way).
    /// </summary>
    public static float Compute(float? remainingSeconds, float minScale, float maxScale, float taperMinutes)
    {
        var taperSeconds = taperMinutes * 60f;

        if (remainingSeconds is null || remainingSeconds.Value <= 0f)
            return minScale;

        if (remainingSeconds.Value >= taperSeconds)
            return maxScale;

        // Linear interpolation: 0s left -> minScale, taperSeconds left -> maxScale.
        var t = remainingSeconds.Value / taperSeconds; // 0..1
        return minScale + (maxScale - minScale) * t;
    }
}
