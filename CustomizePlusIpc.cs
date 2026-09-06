using System;
using System.Text.Json;
using System.Text.Json.Nodes;
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
/// CROSS-PLUGIN CONFLICT (confirmed via a real reported bug, running
/// alongside this author's other Customize+-driven plugin, Hunger
/// Meter, which independently scales the waist bone): the doc comment
/// on SetChestScale below used to claim this call applies "on top of,
/// not replacing" the character's existing profile. That assumption
/// was WRONG. A Customize+ temporary profile, while active, appears to
/// entirely REPLACE the resolved bone set for the character - only the
/// bones actually present in the payload get scaled at all; anything
/// NOT mentioned reverts to unscaled, regardless of what the permanent
/// profile (or another plugin's own separately-pushed temporary
/// profile) had set for it. Since this plugin's payload only ever
/// listed the two chest bones, pushing it would silently blank out
/// whatever Hunger Meter's own temporary profile had most recently set
/// on the waist bone - and, symmetrically, Hunger Meter's own next push
/// would do the same thing back to the chest bones, unless it applies
/// the same fix described here. SetChestScale below now reads whatever
/// bones are part of the CURRENTLY active profile immediately before
/// pushing, and merges its own chest-bone update into that full set
/// rather than replacing it outright - preserving any other bones
/// (Hunger Meter's waist edit included) that happen to currently be
/// part of the active temporary profile.
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
    /// as (your baseline chest scale) * multiplier. Reads whatever bones
    /// are part of the CURRENTLY active profile right before pushing (see
    /// ReadCurrentBonesForMerge) and merges the chest-bone update into
    /// that full set, rather than replacing it outright - see the class
    /// doc comment's CROSS-PLUGIN CONFLICT section for exactly why this
    /// merge step exists and what breaks without it.
    /// </summary>
    public void SetChestScale(float multiplier)
    {
        if (objectIndex is null)
            return;

        var (bx, by, bz) = baselineChestScale ?? (1f, 1f, 1f);

        try
        {
            var bones = ReadCurrentBonesForMerge();

            foreach (var boneName in ChestBones)
            {
                bones[boneName] = new JsonObject
                {
                    ["Translation"] = new JsonObject { ["X"] = 0.0, ["Y"] = 0.0, ["Z"] = 0.0 },
                    ["Rotation"] = new JsonObject { ["X"] = 0.0, ["Y"] = 0.0, ["Z"] = 0.0 },
                    ["Scaling"] = new JsonObject { ["X"] = bx * multiplier, ["Y"] = by * multiplier, ["Z"] = bz * multiplier },
                };
            }

            var payload = new JsonObject { ["Bones"] = bones };
            var profileJson = payload.ToJsonString();

            var (errorCode, _) = setTemporaryProfile.InvokeFunc(objectIndex.Value, profileJson);
            if (errorCode != 0)
                log.Warning($"[MilkMeter] Customize+ SetTemporaryProfileOnCharacter returned error {errorCode}");
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[MilkMeter] Failed to push chest scale to Customize+");
        }
    }

    /// <summary>
    /// Reads the "Bones" object of whatever profile is CURRENTLY active
    /// on the character (which may be this plugin's own previous
    /// temporary push, another plugin's separately-pushed temporary
    /// profile, or the permanent profile if neither has pushed recently)
    /// and returns an independent, mutable copy of it - so SetChestScale
    /// can overwrite just the two chest bone entries while leaving every
    /// other bone byte-for-byte as it currently is. Always returns a
    /// (possibly empty) JsonObject rather than null, so the caller can
    /// unconditionally index into it regardless of whether reading the
    /// current profile actually succeeded.
    ///
    /// Uses JsonNode.Parse on the "Bones" sub-element's raw text rather
    /// than trying to reuse the JsonElement view directly - the
    /// surrounding JsonDocument is disposed the moment this method
    /// returns, and a JsonElement is only valid while its parent
    /// JsonDocument is alive, so holding onto it (or anything backed by
    /// it) past that point would be invalid.
    /// </summary>
    private JsonObject ReadCurrentBonesForMerge()
    {
        if (objectIndex is null)
            return new JsonObject();

        try
        {
            var (err, profileId) = getActiveProfile.InvokeFunc(objectIndex.Value);
            if (err != 0 || profileId is null)
                return new JsonObject();

            var (err2, json) = getProfileById.InvokeFunc(profileId.Value);
            if (err2 != 0 || string.IsNullOrEmpty(json))
                return new JsonObject();

            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("Bones", out var bones))
                return new JsonObject();

            return JsonNode.Parse(bones.GetRawText()) as JsonObject ?? new JsonObject();
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[MilkMeter] Failed to read current profile bones for merge - chest update will proceed without preserving other bones this push");
            return new JsonObject();
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

    public void Dispose()
    {
        // Nothing to unsubscribe - ICallGateSubscriber instances are
        // cleaned up when the DalamudPluginInterface they came from goes
        // away.
    }
}
