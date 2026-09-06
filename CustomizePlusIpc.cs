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
/// MULTIPLIER on that baseline instead of an absolute value. Same
/// treatment applies to the waist bone below, once that feature merged
/// in.
///
/// CROSS-PLUGIN CONFLICT, AND WHY THIS CLASS NOW HANDLES BOTH CHEST AND
/// WAIST: this plugin used to only touch the chest bones, while a
/// SEPARATE plugin (Hunger Meter, by the same author) independently
/// touched the waist bone via its own Customize+ IPC calls. That setup
/// turned out to be fundamentally broken - a Customize+ temporary
/// profile, while active, appears to entirely REPLACE the resolved bone
/// set for the character, not layer on top of the permanent profile or
/// merge with another plugin's own separately-pushed temporary profile.
/// Since each plugin's payload only listed its own bones, whichever one
/// pushed most recently would silently blank out the other's edit. A
/// same-process read-merge-write fix (reading whatever was currently
/// active and merging in just this plugin's own bones before pushing)
/// narrowed the window but couldn't eliminate it entirely, since the
/// two plugins were still two independent processes racing each other.
/// Hunger Meter has since been merged INTO this plugin specifically to
/// fix this at the root: one process, one combined push per frame
/// (SetScales below) covering both chest and waist bones together,
/// so there's no longer a second independent pusher to race against for
/// these two bones. The read-merge-write behavior is kept regardless
/// (preserving whatever bones this plugin doesn't touch, e.g. from the
/// user's own permanent profile or some other unrelated plugin), just
/// no longer load-bearing for the specific chest/waist conflict that
/// motivated it.
///
/// STILL UNVERIFIED: the exact bone names for the chest ("j_mune_l" /
/// "j_mune_r") - the real template file confirmed the surrounding schema
/// (Bones -> boneName -> Translation/Rotation/Scaling, each an X/Y/Z
/// object) but the example only showed leg bones, not chest ones. The
/// waist bone name ("j_kosi") is an even less certain educated guess
/// (koshi = waist/hip in the FFXIV/Anamnesis skeleton naming convention
/// this game's rig is documented to follow), carried over from Hunger
/// Meter's own equivalent comment, and was NEVER independently confirmed
/// there either. If scaling doesn't visibly affect the chest or waist
/// after this fix, wrong bone name is the first thing to check for
/// whichever one - use /milkmeter dumpprofile (or a template you edit
/// scale on directly in Customize+'s UI) to see the real bone name(s)
/// Customize+ uses for your character's body type.
/// </summary>
public sealed class CustomizePlusIpc : IDisposable
{
    private readonly IPluginLog log;

    private readonly ICallGateSubscriber<(int, int)> apiVersion;
    private readonly ICallGateSubscriber<ushort, (int, Guid?)> getActiveProfile;
    private readonly ICallGateSubscriber<Guid, (int, string?)> getProfileById;
    private readonly ICallGateSubscriber<ushort, string, (int, Guid?)> setTemporaryProfile;
    private readonly ICallGateSubscriber<ushort, int> revertProfile;

    // Names of the skeleton bones this plugin edits. Customize+ profile
    // JSON keys bones by their internal name; "j_mune_l"/"j_mune_r" are
    // the left/right chest bones, "j_kosi" the waist, on the standard
    // human skeleton.
    private static readonly string[] ChestBones = ["j_mune_l", "j_mune_r"];
    private const string WaistBoneName = "j_kosi";

    private ushort? objectIndex;
    private (float X, float Y, float Z)? baselineChestScale;
    private (float X, float Y, float Z)? baselineWaistScale;

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
    /// name). Captures both baseline scales (chest and waist) the first
    /// time it's called, or again if the character changes (e.g.
    /// switching alts).
    /// </summary>
    public void SetCharacterObjectIndex(ushort index)
    {
        var changed = objectIndex != index;
        objectIndex = index;

        if (changed || baselineChestScale is null || baselineWaistScale is null)
            CaptureBaseline();
    }

    /// <summary>
    /// Re-reads BOTH the chest and waist scale from whatever Customize+
    /// profile is currently active on the character, in a single read,
    /// and caches each as the baseline its own meter's multiplier
    /// applies against. Defaults either one to (1,1,1) if no active
    /// profile, no override on that particular bone, or the call fails
    /// - meaning that meter falls back to old absolute-value behavior in
    /// that case, independent of whether the OTHER bone's baseline
    /// capture succeeded.
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
                log.Information("[MilkMeter] No active Customize+ profile found - using (1,1,1) baseline chest/waist scale.");
                baselineChestScale = (1f, 1f, 1f);
                baselineWaistScale = (1f, 1f, 1f);
                return;
            }

            var (err2, json) = getProfileById.InvokeFunc(profileId.Value);
            if (err2 != 0 || string.IsNullOrEmpty(json))
            {
                baselineChestScale = (1f, 1f, 1f);
                baselineWaistScale = (1f, 1f, 1f);
                return;
            }

            baselineChestScale = ParseBoneScale(json, ChestBones) ?? (1f, 1f, 1f);
            baselineWaistScale = ParseBoneScale(json, [WaistBoneName]) ?? (1f, 1f, 1f);
            log.Information($"[MilkMeter] Captured baseline chest scale: " +
                $"X={baselineChestScale.Value.X:F3} Y={baselineChestScale.Value.Y:F3} Z={baselineChestScale.Value.Z:F3}; " +
                $"baseline waist scale: X={baselineWaistScale.Value.X:F3} Y={baselineWaistScale.Value.Y:F3} Z={baselineWaistScale.Value.Z:F3}");
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[MilkMeter] Failed to capture baseline chest/waist scale - defaulting both to (1,1,1)");
            baselineChestScale = (1f, 1f, 1f);
            baselineWaistScale = (1f, 1f, 1f);
        }
    }

    /// <summary>
    /// Push a single combined temporary profile covering both chest
    /// (chestMultiplier * baseline chest scale) and waist
    /// (waistMultiplier * baseline waist scale) in one call - this is
    /// the fix for the cross-plugin conflict described in the class
    /// doc comment: one process, one push, both bones, every time,
    /// rather than two independent plugins each pushing their own bone
    /// and racing each other. Still reads whatever bones are part of
    /// the CURRENTLY active profile right before pushing (see
    /// ReadCurrentBonesForMerge) and merges both updates into that full
    /// set, so any OTHER bone (from the user's own permanent profile,
    /// or some unrelated plugin) stays untouched.
    /// </summary>
    public void SetScales(float chestMultiplier, float waistMultiplier)
    {
        if (objectIndex is null)
            return;

        var (cx, cy, cz) = baselineChestScale ?? (1f, 1f, 1f);
        var (wx, wy, wz) = baselineWaistScale ?? (1f, 1f, 1f);

        try
        {
            var bones = ReadCurrentBonesForMerge();

            foreach (var boneName in ChestBones)
            {
                bones[boneName] = new JsonObject
                {
                    ["Translation"] = new JsonObject { ["X"] = 0.0, ["Y"] = 0.0, ["Z"] = 0.0 },
                    ["Rotation"] = new JsonObject { ["X"] = 0.0, ["Y"] = 0.0, ["Z"] = 0.0 },
                    ["Scaling"] = new JsonObject { ["X"] = cx * chestMultiplier, ["Y"] = cy * chestMultiplier, ["Z"] = cz * chestMultiplier },
                };
            }

            bones[WaistBoneName] = new JsonObject
            {
                ["Translation"] = new JsonObject { ["X"] = 0.0, ["Y"] = 0.0, ["Z"] = 0.0 },
                ["Rotation"] = new JsonObject { ["X"] = 0.0, ["Y"] = 0.0, ["Z"] = 0.0 },
                ["Scaling"] = new JsonObject { ["X"] = wx * waistMultiplier, ["Y"] = wy * waistMultiplier, ["Z"] = wz * waistMultiplier },
            };

            var payload = new JsonObject { ["Bones"] = bones };
            var profileJson = payload.ToJsonString();

            var (errorCode, _) = setTemporaryProfile.InvokeFunc(objectIndex.Value, profileJson);
            if (errorCode != 0)
                log.Warning($"[MilkMeter] Customize+ SetTemporaryProfileOnCharacter returned error {errorCode}");
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[MilkMeter] Failed to push chest/waist scale to Customize+");
        }
    }

    /// <summary>
    /// Reads the "Bones" object of whatever profile is CURRENTLY active
    /// on the character (which may be this plugin's own previous
    /// combined push, or the permanent profile if nothing has pushed
    /// recently) and returns an independent, mutable copy of it - so
    /// SetScales can overwrite just the chest/waist bone entries while
    /// leaving every other bone byte-for-byte as it currently is.
    /// Always returns a (possibly empty) JsonObject rather than null,
    /// so the caller can unconditionally index into it regardless of
    /// whether reading the current profile actually succeeded.
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
            log.Warning(ex, "[MilkMeter] Failed to read current profile bones for merge - this push will proceed without preserving other bones");
            return new JsonObject();
        }
    }

    /// <summary>
    /// Removes the ENTIRE temporary profile - affects both chest and
    /// waist together (there's only ever one combined temporary profile
    /// now, not one per bone/meter), reverting the character fully back
    /// to their permanent Customize+ profile.
    /// </summary>
    public void RevertScales()
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
    /// Parses a Customize+ template JSON string looking for the first
    /// matching bone (out of boneNames) with a "Scaling" block - shared
    /// by both the chest baseline capture (passing ChestBones, either
    /// left or right suffices since this plugin always scales them
    /// identically) and the waist baseline capture (passing a
    /// single-element array). Verified against a real exported template
    /// for the chest bones - the field is "Scaling", not "Scale" (an
    /// earlier guess had this wrong), and each bone entry also has
    /// "Translation" and "Rotation" blocks alongside it. The waist bone
    /// name itself is NOT independently verified the same way - see the
    /// class doc comment.
    /// </summary>
    private static (float, float, float)? ParseBoneScale(string profileJson, string[] boneNames)
    {
        try
        {
            using var doc = JsonDocument.Parse(profileJson);
            if (!doc.RootElement.TryGetProperty("Bones", out var bones))
                return null;

            foreach (var boneName in boneNames)
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
