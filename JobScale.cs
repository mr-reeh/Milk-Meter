using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace MilkMeter;

/// <summary>
/// Whether the player's current job has any tracked ability at all. Every
/// tracked job now uses the same mechanic (see JobScale.ApplyGrowth) - there's
/// no longer a per-job-category distinction in how the scale is computed,
/// only in which ability name(s) are tracked.
/// </summary>
public enum JobTrackingKind
{
    NotTracked,
    CombatGrowth,
}

/// <summary>
/// Tracks the recast state of whichever job-relevant ability(-ies) apply
/// to the player's current job (Provoke plus each tank job's own extra
/// abilities - see TrackedAbilityNames; Second Wind for melee/physical
/// ranged DPS; Lucid Dreaming for casters/healers). Most non-tank jobs
/// have exactly one tracked ability, but several tanks now have more -
/// any one of them applies the overuse penalty (see Plugin.cs).
///
/// None of these abilities' own cooldowns drive the scale directly - each
/// is only read as a "was it just used" pulse, which Plugin.cs uses to
/// subtract Configuration.JobOveruseBonus from a persistent scale value
/// it owns (see JobScale.ApplyGrowth for the growth math, and Plugin.cs
/// for the penalty/growth/ceiling-snap rules). This tracker itself stays
/// a stateless query object - Plugin.cs tracks per-ability rising edges
/// itself, keyed by ability name, since a job can have more than one.
///
/// Verified against FFXIVClientStructs' own published API docs
/// (ffxiv.wildwolf.dev) rather than guessed:
///   - ActionManager.Instance()->GetRecastGroup((int)ActionType.Action, actionId)
///     -> int recastGroup
///   - ActionManager.Instance()->GetRecastGroupDetail(recastGroup) -> RecastDetail*
///   - RecastDetail.IsActive (bool) - true while on cooldown
///   - RecastDetail.Elapsed (float seconds) - time since last use
///   - RecastDetail.Total (float seconds) - full recast time
///
/// Action IDs are resolved by name from Dalamud's data sheets at runtime
/// (same robustness reasoning as FoodBuffTracker's name-based status
/// match) rather than hardcoded, since hardcoded IDs are the kind of
/// thing that silently breaks on game updates or client-language
/// differences.
///
/// One remaining unverified detail: ActionType.Action (used below as the
/// action type for GetRecastGroup) is passed as its well-established
/// community value of 1 - this specific enum value wasn't independently
/// confirmed against FFXIVClientStructs' source the way everything else
/// in this file was. If cooldown tracking doesn't work at all (recast
/// group always comes back negative), this is the first thing to check.
///
/// GetGcdCooldownState() below is a second, separate query this class
/// now answers - the shared GCD recast group (any spell/weaponskill
/// invocation, not one specific tracked ability) - reusing the exact
/// same GetRecastGroupDetail mechanism, just given the recast group
/// number directly instead of resolving one from an action name.
/// </summary>
public sealed class JobBuffTracker
{
    // Job abbreviation -> ability name(s) to track for that job. Most
    // jobs have one; several tanks now have more (any one firing applies
    // its own overuse penalty - see Plugin.cs). Every tracked job feeds
    // the same combat-growth mechanic - only which ability name(s) are
    // watched differs. Extend to support more jobs.
    private static readonly Dictionary<string, string[]> TrackedAbilityNames = new()
    {
        // Tanks - Provoke, plus each job's own extra tracked abilities
        ["PLD"] = ["Provoke"],
        ["WAR"] = ["Provoke", "Equilibrium"],
        ["DRK"] = ["Provoke"],
        ["GNB"] = ["Provoke"],

        // Melee & physical ranged DPS - Second Wind
        ["MNK"] = ["Second Wind"],
        ["DRG"] = ["Second Wind"],
        ["NIN"] = ["Second Wind"],
        ["SAM"] = ["Second Wind"],
        ["RPR"] = ["Second Wind"],
        ["VPR"] = ["Second Wind"],
        ["BRD"] = ["Second Wind"],
        ["MCH"] = ["Second Wind"],
        ["DNC"] = ["Second Wind"],

        // Healers & casters - Lucid Dreaming
        ["WHM"] = ["Lucid Dreaming"],
        ["SCH"] = ["Lucid Dreaming"],
        ["AST"] = ["Lucid Dreaming"],
        ["SGE"] = ["Lucid Dreaming"],
        ["BLM"] = ["Lucid Dreaming"],
        ["SMN"] = ["Lucid Dreaming"],
        ["RDM"] = ["Lucid Dreaming"],
        ["PCT"] = ["Lucid Dreaming"],
        ["BLU"] = ["Lucid Dreaming"],
    };

    private readonly IObjectTable objectTable;
    private readonly IDataManager dataManager;
    private readonly IPluginLog log;

    private readonly Dictionary<string, uint> resolvedActionIds = new();

    public JobBuffTracker(IObjectTable objectTable, IDataManager dataManager, IPluginLog log)
    {
        this.objectTable = objectTable;
        this.dataManager = dataManager;
        this.log = log;
    }

    /// <summary>Whether the player's current job has any tracked ability.</summary>
    public JobTrackingKind GetTrackingKind() =>
        GetTrackedAbilityNames() is null ? JobTrackingKind.NotTracked : JobTrackingKind.CombatGrowth;

    /// <summary>
    /// The tracked ability name(s) for the current job, or null if the
    /// current job isn't tracked (or no player yet). Almost always one
    /// name; Warrior has two.
    /// </summary>
    public string[]? GetTrackedAbilityNames()
    {
        var jobAbbreviation = GetCurrentJobAbbreviation();
        if (jobAbbreviation is null)
            return null;

        return TrackedAbilityNames.GetValueOrDefault(jobAbbreviation);
    }

    /// <summary>
    /// Display-friendly version of GetTrackedAbilityNames() - joins
    /// multiple names (e.g. Warrior's "Provoke, Equilibrium") for the
    /// settings window. Null if the current job isn't tracked.
    /// </summary>
    public string? GetTrackedAbilityDisplayName()
    {
        var names = GetTrackedAbilityNames();
        return names is null ? null : string.Join(", ", names);
    }

    /// <returns>
    /// OnCooldown: true if the named ability is currently recharging.
    /// Elapsed/Total: seconds into/out of the recast cycle - both null if
    /// not on cooldown (ability is ready) or the name doesn't resolve to
    /// a real action. Read by Plugin.cs purely as a "was it just used"
    /// pulse (rising edge of OnCooldown) per ability name, not as a
    /// direct scale driver.
    /// </returns>
    public (bool OnCooldown, float? Elapsed, float? Total) GetCooldownState(string abilityName)
    {
        var actionId = ResolveActionId(abilityName);
        if (actionId is null)
            return (false, null, null);

        unsafe
        {
            var actionManager = ActionManager.Instance();
            if (actionManager is null)
                return (false, null, null);

            var recastGroup = actionManager->GetRecastGroup((int)ActionType.Action, actionId.Value);
            if (recastGroup < 0)
                return (false, null, null);

            var detail = actionManager->GetRecastGroupDetail(recastGroup);
            if (detail is null || !detail->IsActive)
                return (false, null, null); // off cooldown - ready to use

            return (true, detail->Elapsed, detail->Total);
        }
    }

    /// <summary>
    /// Reads GetRecastGroupDetail directly for a GIVEN recast group
    /// number (Configuration.GcdRecastGroup), rather than resolving one
    /// from an action name the way GetCooldownState above does - the
    /// GCD is a single recast group shared by every GCD spell/
    /// weaponskill, not tied to any one specific action. Community
    /// documentation suggested group 58, but that turned out to be
    /// wrong on this client version - confirmed via ScanActiveRecastGroups
    /// below to actually be 57. Unlike GetCooldownState above,
    /// Plugin.cs does NOT rely purely on "rising edge of OnCooldown = just
    /// used" here - it also watches Elapsed for a decrease while already
    /// on cooldown, since pressing the next GCD at the exact instant the
    /// previous one ends can mean OnCooldown never actually observes a
    /// false in between (found this the hard way - it caused missed
    /// detections for GCDs pressed immediately as the previous one came
    /// off cooldown).
    /// </summary>
    public (bool OnCooldown, float? Elapsed, float? Total) GetGcdCooldownState(int recastGroup)
    {
        unsafe
        {
            var actionManager = ActionManager.Instance();
            if (actionManager is null)
                return (false, null, null);

            var detail = actionManager->GetRecastGroupDetail(recastGroup);
            if (detail is null || !detail->IsActive)
                return (false, null, null); // off cooldown - ready to use

            return (true, detail->Elapsed, detail->Total);
        }
    }

    /// <summary>
    /// Scans every recast group from 0 through maxGroup (inclusive) and
    /// returns whichever ones are currently active - built specifically
    /// to help empirically discover the correct GcdRecastGroup value
    /// (this is exactly how the community-suggested default of 58 was
    /// found to be wrong, and the real value of 57 confirmed), rather
    /// than making the user guess-and-check one number at a time
    /// through settings. Intended usage: press one specific GCD
    /// spell/weaponskill, then immediately call this - whichever group
    /// appears with a low Elapsed and a Total in the general
    /// neighborhood of a GCD recast (~2.5 seconds, adjusted by skill/
    /// spell speed - typically not far outside roughly 1.5-3 seconds)
    /// is a strong GCD candidate. Confirming it takes a second step:
    /// press a DIFFERENT GCD ability and check that the SAME group
    /// number goes active again - a group that only reacts to one
    /// specific ability is that ability's own individual cooldown, not
    /// the shared GCD group. Still useful if a future game patch shifts
    /// the group number again.
    /// </summary>
    public List<(int Group, float Elapsed, float Total)> ScanActiveRecastGroups(int maxGroup = 100)
    {
        var results = new List<(int Group, float Elapsed, float Total)>();

        unsafe
        {
            var actionManager = ActionManager.Instance();
            if (actionManager is null)
                return results;

            for (var group = 0; group <= maxGroup; group++)
            {
                var detail = actionManager->GetRecastGroupDetail(group);
                if (detail is not null && detail->IsActive)
                    results.Add((group, detail->Elapsed, detail->Total));
            }
        }

        return results;
    }

    private string? GetCurrentJobAbbreviation()
    {
        var player = objectTable.LocalPlayer;
        if (player is null)
            return null;

        return player.ClassJob.ValueNullable?.Abbreviation.ExtractText();
    }

    /// <summary>
    /// Full diagnostic dump for /milkmeter jobdebug: current
    /// job, every tracked ability name for it, and for each - every
    /// Action-sheet row matching that name with their RowIds, which one
    /// ResolveActionId actually picked (see that method for how - it
    /// validates against GetRecastGroup rather than trusting sheet
    /// order, since duplicate-named rows are common and not all of them
    /// are real trackable actions), and the live recast state for the
    /// resolved action.
    /// </summary>
    public string GetDebugInfo()
    {
        var lines = new List<string>();

        var jobAbbreviation = GetCurrentJobAbbreviation();
        lines.Add($"Current job: {jobAbbreviation ?? "(no player)"}");

        var abilityNames = GetTrackedAbilityNames();
        lines.Add($"Tracked names: {(abilityNames is null ? "(job not tracked)" : string.Join(", ", abilityNames))}");

        if (abilityNames is null)
            return string.Join("\n", lines);

        foreach (var abilityName in abilityNames)
        {
            lines.Add($"--- {abilityName} ---");

            try
            {
                var sheet = dataManager.GetExcelSheet<Lumina.Excel.Sheets.Action>();
                var matchCount = 0;
                foreach (var row in sheet)
                {
                    var name = row.Name.ExtractText();
                    if (!string.Equals(name, abilityName, StringComparison.OrdinalIgnoreCase))
                        continue;

                    matchCount++;
                    lines.Add($"  Match #{matchCount}: RowId={row.RowId}");
                }
                lines.Add($"  Total matching rows in Action sheet: {matchCount}");
            }
            catch (Exception ex)
            {
                lines.Add($"  Error enumerating Action sheet: {ex.Message}");
            }

            var actionId = ResolveActionId(abilityName);
            lines.Add($"  Resolved actionId (validated against GetRecastGroup, not just first match): {actionId?.ToString() ?? "(none)"}");

            if (actionId is null)
                continue;

            unsafe
            {
                var actionManager = ActionManager.Instance();
                if (actionManager is null)
                {
                    lines.Add("  ActionManager.Instance() is null");
                    continue;
                }

                var recastGroup = actionManager->GetRecastGroup((int)ActionType.Action, actionId.Value);
                lines.Add($"  GetRecastGroup((int)ActionType.Action, {actionId.Value}) = {recastGroup}");

                if (recastGroup < 0)
                    continue;

                var detail = actionManager->GetRecastGroupDetail(recastGroup);
                if (detail is null)
                {
                    lines.Add("  GetRecastGroupDetail returned null");
                }
                else
                {
                    lines.Add($"  RecastDetail: ActionId={detail->ActionId}, IsActive={detail->IsActive}, " +
                        $"Elapsed={detail->Elapsed:F2}, Total={detail->Total:F2}");
                }
            }
        }

        return string.Join("\n", lines);
    }

    /// <summary>
    /// Resolves an ability name to the one Action-sheet RowId that's
    /// actually a real, trackable player action. Confirmed against real
    /// data that simply taking the first name match isn't safe: "Second
    /// Wind" had 5 rows sharing that name, and the first one (a low
    /// RowId, likely an old/reserved/unused sheet entry) returned an
    /// invalid recast group, silently breaking tracking for every job
    /// that used it. This instead tests each candidate against
    /// GetRecastGroup (which we already know behaves correctly) and
    /// picks the first one that resolves to a real group, rather than
    /// trusting sheet order.
    /// </summary>
    private uint? ResolveActionId(string abilityName)
    {
        if (resolvedActionIds.TryGetValue(abilityName, out var cached))
            return cached;

        List<uint> candidates;
        try
        {
            var sheet = dataManager.GetExcelSheet<Lumina.Excel.Sheets.Action>();
            candidates = new List<uint>();
            foreach (var row in sheet)
            {
                if (string.Equals(row.Name.ExtractText(), abilityName, StringComparison.OrdinalIgnoreCase))
                    candidates.Add(row.RowId);
            }
        }
        catch (Exception ex)
        {
            log.Warning(ex, $"[MilkMeter] Failed to search Action sheet for '{abilityName}'");
            return null;
        }

        if (candidates.Count == 0)
        {
            log.Warning($"[MilkMeter] Could not find action named '{abilityName}' in the Action sheet " +
                "- if your client isn't English, this name needs localizing.");
            return null;
        }

        unsafe
        {
            var actionManager = ActionManager.Instance();
            if (actionManager is null)
                return candidates[0]; // don't cache - retry once ActionManager is available

            foreach (var candidate in candidates)
            {
                if (actionManager->GetRecastGroup((int)ActionType.Action, candidate) < 0)
                    continue;

                if (candidates.Count > 1)
                {
                    log.Information($"[MilkMeter] '{abilityName}' had {candidates.Count} matching rows " +
                        $"in the Action sheet ({string.Join(", ", candidates)}); picked RowId={candidate} as the " +
                        "one with a valid recast group.");
                }

                resolvedActionIds[abilityName] = candidate;
                return candidate;
            }
        }

        log.Warning($"[MilkMeter] None of the {candidates.Count} '{abilityName}' rows " +
            $"({string.Join(", ", candidates)}) returned a valid recast group - falling back to " +
            $"RowId={candidates[0]}, which may not track correctly.");
        resolvedActionIds[abilityName] = candidates[0];
        return candidates[0];
    }
}

public static class JobScale
{
    public const float DefaultBaselineScale = 1.00f;
    public const float DefaultCombatFloorScale = 0.70f;
    public const float DefaultOveruseBonus = 0.15f;
    // Set to match the Milk Meter rename's carried-over settings
    // (0.005/sec) rather than the old "5.0 minutes to grow 1.0 full
    // scale unit" derivation this replaced - see the config file that
    // rename was based on for where 0.005 itself came from.
    public const float DefaultPassiveScaleGenPerSecond = 0.005f;
    public const float DefaultUpperLimitScale = 1.30f;

    /// <summary>
    /// One growth step for the persistent "current scale" value
    /// Plugin.cs owns: pushes currentScale up toward ceilingScale at
    /// growthPerSecond scale-units per second, never overshooting past
    /// the ceiling. A rate of 0 (or below) means NO growth at all - this
    /// is a genuine behavioral distinction from the old growthMinutes
    /// parameter this replaced, where 0 meant "instant" (the two units
    /// are inverses of each other, so the same "invalid input" edge case
    /// means opposite things: zero MINUTES was "as fast as possible",
    /// zero RATE is "as slow as possible", i.e. none). If currentScale is
    /// already AT OR
    /// ABOVE ceilingScale, this does nothing and returns currentScale
    /// unchanged - the ceiling is an upper bound growth can't push past,
    /// not a target it snaps toward. In particular, this does NOT pull
    /// currentScale back down to ceilingScale just because the ceiling
    /// dropped (e.g. leaving combat lowers the ceiling from
    /// JobUpperLimitScale to JobBaselineScale) - if you were already
    /// above the new ceiling, you stay there; only using a tracked
    /// ability (the overuse penalty, applied by Plugin.cs) brings it
    /// back down.
    ///
    /// This is the mirror image of the plugin's original design: using a
    /// tracked ability now SHRINKS scale instead of growing it (clamped
    /// at JobCombatFloorScale, which now acts as a universal floor
    /// regardless of combat state), and simply existing over time now
    /// GROWS scale back up instead of decaying it, toward whichever
    /// ceiling currently applies (JobUpperLimitScale in combat,
    /// JobBaselineScale out of combat).
    /// </summary>
    public static float ApplyGrowth(float currentScale, float ceilingScale, float growthPerSecond, float deltaSeconds)
    {
        if (currentScale >= ceilingScale)
            return currentScale; // already at/above ceiling - growth has nothing to do, doesn't restore downward

        if (growthPerSecond <= 0f)
            return currentScale; // no growth configured - unchanged, NOT snapped to ceiling

        return Math.Min(ceilingScale, currentScale + growthPerSecond * deltaSeconds);
    }

    /// <summary>
    /// Mirror image of ApplyGrowth: pulls currentScale DOWN toward
    /// floorScale at drainPerSecond scale-units per second,
    /// never overshooting past the floor. A rate of 0 (or below) means NO
    /// drain at all - see ApplyGrowth's own doc comment for why this is a
    /// genuine behavioral distinction from the old drainMinutes parameter
    /// this replaced (zero minutes meant "instant"; zero rate means
    /// "none"). If currentScale is already AT
    /// OR BELOW floorScale, this does nothing and returns currentScale
    /// unchanged - same "never restores" philosophy as ApplyGrowth.
    /// Built specifically for the /cackle emote's "empty the gauge"
    /// effect - see Plugin.cs's EmoteLoopTracker.IsCackleActive() usage.
    /// </summary>
    public static float ApplyDrain(float currentScale, float floorScale, float drainPerSecond, float deltaSeconds)
    {
        if (currentScale <= floorScale)
            return currentScale; // already at/below floor - drain has nothing to do, doesn't restore upward

        if (drainPerSecond <= 0f)
            return currentScale; // no drain configured - unchanged, NOT snapped to floor

        return Math.Max(floorScale, currentScale - drainPerSecond * deltaSeconds);
    }

    /// <summary>
    /// The user-configurable overuse-bonus multiplier for a given ability
    /// name - fully in the user's hands via the settings window, not
    /// derived automatically. Defaults match each ability's real cooldown
    /// relative to Provoke's 30s baseline (1x/2x/2x/4x). Any ability name
    /// not one of the four below (shouldn't currently happen, since these
    /// are the only ones in JobBuffTracker's TrackedAbilityNames) falls
    /// back to 1x.
    /// </summary>
    public static float GetOveruseMultiplier(string abilityName, Configuration config) => abilityName switch
    {
        "Provoke" => config.ProvokeMultiplier,
        "Equilibrium" => config.EquilibriumMultiplier,
        "Lucid Dreaming" => config.LucidDreamingMultiplier,
        "Second Wind" => config.SecondWindMultiplier,
        _ => 1f,
    };
}
