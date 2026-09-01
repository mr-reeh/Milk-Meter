using System;
using System.Text.Json;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;

namespace MilkMeter;

/// <summary>
/// Every call this plugin makes into Customize+ lives in this one class.
///
/// Verified against a real, working consumer: LightlessSync's
/// IpcCallerCustomize.cs (a Mare Synchronos fork - Customize+'s IPC is
/// built primarily for Mare-family plugins, per Customize+'s own README).
/// Confirmed method signatures (all target the ObjectIndex, not a name):
///   - GetApiVersion() -> (int major, int minor)
///   - GetActiveProfileIdOnCharacter(ushort objectIndex) -> (int err, Guid? profileId)
///   - GetByUniqueId(Guid profileId) -> (int err, string? profileJson)
///   - SetTemporaryProfileOnCharacter(ushort objectIndex, string profileJson)
///     -> (int err, Guid? tempProfileId)
///   - DeleteTemporaryProfileOnCharacter(ushort objectIndex) -> int err
///
/// BASELINE SCALING: the first version of this plugin wrote an absolute
/// chest scale (0.60-1.00) into the temporary profile. That produces a
/// visibly wrong result if your own permanent Customize+ profile already
/// scales the chest above the game's raw default - "1.00" (meant as "full
/// size") ends up smaller than whatever you've customized as your normal
/// look. Fixed here by reading your active profile's current chest scale
/// once (CaptureBaseline) and treating the food/mana-driven value as a
/// MULTIPLIER on that baseline instead of an absolute value.
///
/// STILL UNVERIFIED: the exact bone names for the chest ("j_mune_l" /
/// "j_mune_r") - the real template file confirmed the surrounding schema
/// (Bones -> boneName -> Translation/Rotation/Scaling, each an X/Y/Z
/// object) but the example only showed leg bones, not chest ones. If
/// scaling still doesn't visibly affect the chest after this fix, wrong
/// bone names are the next thing to check - use /milkmeter dumpprofile
/// (or a template you edit chest scale on directly in Customize+'s UI) to
/// see the real bone name(s) Customize+ uses for the chest on your
/// character's body type.
/// </summary>
public sealed class CustomizePlusIpc : IDisposable
{
    private readonly IPluginLog log;

    private readonly ICallGateSubscriber<(int, int)> apiVersion;
    private readonly ICallGateSubscriber<ushort, (int, Guid?)> getActiveProfile;
    private readonly ICallGateSubscriber<Guid, (int, string?)> getProfileById;
    private readonly ICallGateSubscriber<ushort, string, (int, Guid?)> setTemporaryProfile;
    private readonly ICallGateSubscriber<ushort, int> revertProfile;

    // Name of the skeleton bone this plugin edits. Customize+ profile
    // JSON keys bones by their internal name; "j_mune_l"/"j_mune_r" are
    // the left/right chest bones on the standard human skeleton.
    private static readonly string[] ChestBones = ["j_mune_l", "j_mune_r"];

    private ushort? objectIndex;
    private (float X, float Y, float Z)? baselineChestScale;

    public CustomizePlusIpc(IDalamudPluginInterface pluginInterface, IPluginLog log)
    {
        this.log = log;

        apiVersion = pluginInterface.GetIpcSubscriber<(int, int)>("CustomizePlus.General.GetApiVersion");
        getActiveProfile = pluginInterface.GetIpcSubscriber<ushort, (int, Guid?)>(
            "CustomizePlus.Profile.GetActiveProfileIdOnCharacter");
        getProfileById = pluginInterface.GetIpcSubscriber<Guid, (int, string?)>(
            "CustomizePlus.Profile.GetByUniqueId");
        setTemporaryProfile = pluginInterface.GetIpcSubscriber<ushort, string, (int, Guid?)>(
            "CustomizePlus.Profile.SetTemporaryProfileOnCharacter");
        revertProfile = pluginInterface.GetIpcSubscriber<ushort, int>(
            "CustomizePlus.Profile.DeleteTemporaryProfileOnCharacter");

        try
        {
            var (major, minor) = apiVersion.InvokeFunc();
            log.Information($"[MilkMeter] Customize+ IPC version {major}.{minor}");
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[MilkMeter] Could not reach Customize+ - is it installed and enabled?");
        }
    }

    /// <summary>
    /// Target character, identified by Dalamud object table index (not
    /// name). Captures the baseline chest scale the first time it's
    /// called, or again if the character changes (e.g. switching alts).
    /// </summary>
    public void SetCharacterObjectIndex(ushort index)
    {
        var changed = objectIndex != index;
        objectIndex = index;

        if (changed || baselineChestScale is null)
            CaptureBaseline();
    }

    /// <summary>
    /// Re-reads the chest scale from whatever Customize+ profile is
    /// currently active on the character, and caches it as the baseline
    /// that food/mana scaling multiplies against. Defaults to (1,1,1) if
    /// no active profile, no override on the chest bones, or the call
    /// fails - meaning the plugin falls back to the old absolute
    /// behaviour in that case.
    /// </summary>
    public void CaptureBaseline()
    {
        if (objectIndex is null)
            return;

        try
        {
            var (err, profileId) = getActiveProfile.InvokeFunc(objectIndex.Value);
            if (err != 0 || profileId is null)
            {
                log.Information("[MilkMeter] No active Customize+ profile found - using (1,1,1) baseline chest scale.");
                baselineChestScale = (1f, 1f, 1f);
                return;
            }

            var (err2, json) = getProfileById.InvokeFunc(profileId.Value);
            if (err2 != 0 || string.IsNullOrEmpty(json))
            {
                baselineChestScale = (1f, 1f, 1f);
                return;
            }

            baselineChestScale = ParseChestScale(json) ?? (1f, 1f, 1f);
            log.Information($"[MilkMeter] Captured baseline chest scale: " +
                $"X={baselineChestScale.Value.X:F3} Y={baselineChestScale.Value.Y:F3} Z={baselineChestScale.Value.Z:F3}");
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[MilkMeter] Failed to capture baseline chest scale - defaulting to (1,1,1)");
            baselineChestScale = (1f, 1f, 1f);
        }
    }

    /// <summary>
    /// Push a temporary bone-scale override for the chest bones, computed
    /// as (your baseline chest scale) * multiplier. This is applied on
    /// top of (not replacing) the player's existing Customize+ profile,
    /// same as the original ManaMune's "nothing else you scaled is lost"
    /// behaviour.
    /// </summary>
    public void SetChestScale(float multiplier)
    {
        if (objectIndex is null)
            return;

        var (bx, by, bz) = baselineChestScale ?? (1f, 1f, 1f);
        var profileJson = BuildTemplateJson(bx * multiplier, by * multiplier, bz * multiplier);

        try
        {
            var (errorCode, _) = setTemporaryProfile.InvokeFunc(objectIndex.Value, profileJson);
            if (errorCode != 0)
                log.Warning($"[MilkMeter] Customize+ SetTemporaryProfileOnCharacter returned error {errorCode}");
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[MilkMeter] Failed to push chest scale to Customize+");
        }
    }

    public void RevertChestScale()
    {
        if (objectIndex is null)
            return;

        try
        {
            revertProfile.InvokeFunc(objectIndex.Value);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[MilkMeter] Failed to revert Customize+ temporary profile");
        }
    }

    /// <summary>
    /// Fetches and returns the raw JSON of whatever profile is currently
    /// active on the character - for manual inspection via /xllog when
    /// diagnosing the profile JSON schema. Does not throw; returns a
    /// short description string instead on failure.
    /// </summary>
    public string DumpActiveProfileJson()
    {
        if (objectIndex is null)
            return "(no character set yet)";

        try
        {
            var (err, profileId) = getActiveProfile.InvokeFunc(objectIndex.Value);
            if (err != 0 || profileId is null)
                return $"(no active profile, GetActiveProfileIdOnCharacter error {err})";

            var (err2, json) = getProfileById.InvokeFunc(profileId.Value);
            if (err2 != 0 || json is null)
                return $"(GetByUniqueId error {err2})";

            return json;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[MilkMeter] Failed to dump active profile");
            return "(exception - see /xllog warning above)";
        }
    }

    /// <summary>
    /// Parses a Customize+ template JSON string looking for either chest
    /// bone's Scaling. Verified against a real exported template - the
    /// field is "Scaling", not "Scale" (an earlier guess had this wrong),
    /// and each bone entry also has "Translation" and "Rotation" blocks
    /// alongside it.
    /// </summary>
    private static (float, float, float)? ParseChestScale(string profileJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(profileJson);
            if (!doc.RootElement.TryGetProperty("Bones", out var bones))
                return null;

            foreach (var boneName in ChestBones)
            {
                if (!bones.TryGetProperty(boneName, out var bone))
                    continue;
                if (!bone.TryGetProperty("Scaling", out var scaling))
                    continue;

                var x = scaling.GetProperty("X").GetSingle();
                var y = scaling.GetProperty("Y").GetSingle();
                var z = scaling.GetProperty("Z").GetSingle();
                return (x, y, z);
            }
        }
        catch
        {
            // Schema didn't match - caller falls back to (1,1,1).
        }

        return null;
    }

    /// <summary>
    /// Builds a Customize+ template JSON fragment for the chest bones.
    /// Verified against a real exported template file (Version 4/6 seen
    /// in testing): each bone needs "Translation", "Rotation", and
    /// "Scaling" blocks - Translation/Rotation are zeroed here since this
    /// plugin only ever touches scale. Deliberately omits the top-level
    /// Version/UniqueId/CreationDate/ModifiedDate/IsWriteProtected fields
    /// that persisted template files on disk have - those look like
    /// file-storage metadata rather than something the temporary-profile
    /// IPC call needs, matching how LightlessSync forwards a bare
    /// "Bones"-only payload for the same call.
    /// </summary>
    private static string BuildTemplateJson(float x, float y, float z)
    {
        var xStr = x.ToString("F4", System.Globalization.CultureInfo.InvariantCulture);
        var yStr = y.ToString("F4", System.Globalization.CultureInfo.InvariantCulture);
        var zStr = z.ToString("F4", System.Globalization.CultureInfo.InvariantCulture);

        var bones = string.Join(",", Array.ConvertAll(ChestBones, bone =>
            $$"""
            "{{bone}}": {
              "Translation": { "X": 0.0, "Y": 0.0, "Z": 0.0 },
              "Rotation": { "X": 0.0, "Y": 0.0, "Z": 0.0 },
              "Scaling": { "X": {{xStr}}, "Y": {{yStr}}, "Z": {{zStr}} }
            }
            """));

        return $$"""
        {
          "Bones": { {{bones}} }
        }
        """;
    }

    public void Dispose()
    {
        // Nothing to unsubscribe - ICallGateSubscriber instances are
        // cleaned up when the DalamudPluginInterface they came from goes
        // away.
    }
}
