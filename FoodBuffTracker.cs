using Dalamud.Plugin.Services;

namespace MilkMeter;

/// <summary>
/// Looks at the local player's status effects and reports whether a food
/// ("Well Fed") buff is active, and how much time is left on it - plus,
/// separately, whether the "Meat and Mead" buff is active (applied by
/// the Squadron Rationing Manual item, or the equivalent Free Company
/// action of the same name - both apply the identical status, just from
/// different sources).
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
/// "Meat and Mead" matching is a prefix match too (StartsWith), for the
/// same reason - it comes in ranked tiers (I/II/III depending on source;
/// the Squadron Rationing Manual specifically applies tier III), and a
/// prefix match catches all of them without needing a tier-by-tier list.
/// Confirmed via community wiki sources describing exactly this status
/// name being applied by the item in question, though NOT independently
/// re-verified against this specific client's actual Character.StatusList
/// output the way "Well Fed" itself effectively has been through
/// long-standing use in this project - if Squadron-Rationing-Manual-boosted
/// bumps don't seem to be triggering, this status name is the first thing
/// to check (a debug print of the full StatusList while the buff is up
/// would confirm the real name quickly).
/// </summary>
public sealed class FoodBuffTracker(IObjectTable objectTable, Configuration configuration)
{
    private const string WellFedStatusName = "Well Fed";
    private const string MeatAndMeadStatusName = "Meat and Mead";

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
    /// True while any tier of "Meat and Mead" is currently active -
    /// used to boost the waist meter's flat food-eaten bump (see
    /// Configuration.WaistRationingManualBumpMultiplier and Plugin.cs's
    /// foodConsumedEdge handling). A separate query from
    /// GetFoodBuffState() above since this buff is entirely independent
    /// of Well Fed - both can be active or inactive in any combination.
    /// </summary>
    public bool IsMeatAndMeadActive()
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
            if (!string.IsNullOrEmpty(name) && name.StartsWith(MeatAndMeadStatusName, System.StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
