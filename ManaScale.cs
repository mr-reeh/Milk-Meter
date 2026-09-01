using Dalamud.Plugin.Services;

namespace MilkMeter;

/// <summary>
/// The original ManaMune signal: current MP as a fraction of max MP.
/// Kept as its own tracker/scale pair, symmetric with FoodBuffTracker /
/// FoodScale, so Plugin.cs can pick whichever one is active in config.
/// </summary>
public sealed class ManaTracker(IObjectTable objectTable)
{
    /// <returns>Current MP fraction in [0, 1], or null if no local player.</returns>
    public float? GetManaFraction()
    {
        var player = objectTable.LocalPlayer;
        if (player is null || player.MaxMp == 0)
            return null;

        return (float)player.CurrentMp / player.MaxMp;
    }
}

public static class ManaScale
{
    public const float MinScale = 0.60f;
    public const float MaxScale = 1.00f;

    /// <summary>
    /// Full size at 100% MP, minimum size at 0% MP - or reversed, per
    /// config, matching the original plugin's description ("Full at 100%,
    /// smaller as you spend - or the other way round").
    /// </summary>
    public static float Compute(float? manaFraction, bool inverted)
    {
        if (manaFraction is null)
            return MinScale;

        var fraction = inverted ? 1f - manaFraction.Value : manaFraction.Value;
        return MinScale + (MaxScale - MinScale) * fraction;
    }
}
