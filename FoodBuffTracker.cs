using Dalamud.Plugin.Services;

namespace MilkMeter;

/// <summary>
/// Looks at the local player's status effects and reports whether a food
/// ("Well Fed") buff is active, and how much time is left on it - plus,
/// separately, whether the "Rationing" buff is active (applied by the
/// Squadron Rationing Manual item, or the equivalent Free Company action
/// - both apply the identical status, just from different sources).
///
/// FFXIV's food buff is applied as a status effect named "Well Fed"
/// (localized) with a per-food-item duration (30 min for normal quality,
/// 45 min for HQ). We don't need to know the original duration - only
/// how much time is left - so we just read Status.RemainingTime directly
/// off whichever status matches.
///
/// Matching is done by name rather than a hardcoded ID list because new
/// "Well Fed" status IDs are added with new food items every patch; a
/// fixed ID list would silently go stale. If your friend's client isn't
/// English, swap the comparison for the right localized string or match
/// on GameData.RowId against Lumina's Status sheet where Name == "Well Fed"
/// for the player's configured client language.
///
/// "Rationing" matching is a prefix match too (StartsWith), same
/// reasoning. CONFIRMED via /hungermeter statuslist (or /food statuslist)
/// dumping the real Character.StatusList output while the buff was
/// active - an earlier version of this guessed "Meat and Mead" based on
/// wiki descriptions of the item's effect, which turned out to be wrong;
/// the actual in-game status name is "Rationing", 7173s (~2 hours)
/// remaining duration confirmed matching the item's own described 120m
/// duration plus whatever time had already passed.
/// </summary>
public sealed class FoodBuffTracker(IObjectTable objectTable, Configuration configuration)
{
    private const string WellFedStatusName = "Well Fed";
    private const string RationingStatusName = "Rationing";

    /// <returns>
    /// (true, remainingSeconds) if a food buff is active, otherwise (false, null).
    /// </returns>
    public (bool Active, float? RemainingSeconds) GetFoodBuffState()
    {
        var player = objectTable.LocalPlayer;
        if (player is null)
            return (false, null);

        foreach (var status in player.StatusList)
        {
            var data = status.GameData;
            var row = data.ValueNullable;
            if (row is null)
                continue;

            var name = row.Value.Name.ExtractText();
            if (string.IsNullOrEmpty(name))
                continue;

            if (!name.StartsWith(WellFedStatusName, System.StringComparison.OrdinalIgnoreCase)
                && !(configuration.ExtraWellFedStatusNames.Contains(name)))
                continue;

            // RemainingTime counts down to 0 while the status is active.
            return (true, status.RemainingTime);
        }

        return (false, null);
    }

    /// <summary>
    /// True while "Rationing" is currently active - used to boost the
    /// waist meter's flat food-eaten bump (see
    /// Configuration.WaistRationingManualBumpMultiplier and Plugin.cs's
    /// foodConsumedEdge handling). A separate query from
    /// GetFoodBuffState() above since this buff is entirely independent
    /// of Well Fed - both can be active or inactive in any combination.
    /// </summary>
    public bool IsRationingActive()
    {
        var player = objectTable.LocalPlayer;
        if (player is null)
            return false;

        foreach (var status in player.StatusList)
        {
            var data = status.GameData;
            var row = data.ValueNullable;
            if (row is null)
                continue;

            var name = row.Value.Name.ExtractText();
            if (!string.IsNullOrEmpty(name) && name.StartsWith(RationingStatusName, System.StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
