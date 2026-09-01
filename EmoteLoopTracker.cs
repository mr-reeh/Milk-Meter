using System;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;

namespace MilkMeter;

/// <summary>
/// Detects whether the local player is currently performing a looping
/// emote (Character.Mode == CharacterModes.EmoteLoop), and optionally
/// narrows that down to one SPECIFIC emote via ModeParam - verified
/// against FFXIVClientStructs' own Character.cs source
/// (github.com/aers/FFXIVClientStructs) rather than guessed: Mode and
/// ModeParam are real fields at confirmed offsets (0x2364/0x2365), and
/// CharacterModes.EmoteLoop (value 3) is documented there as "Param is
/// an EmoteMode entry".
///
/// Originally built (and named) for /shakedrink alone; generalized here
/// once a second, opposite-effect looping emote was added, since both
/// need the exact same underlying detection - just checked against a
/// different configured ModeParam value each. That second emote was
/// briefly swapped to /greentea, then reverted after confirming
/// /greentea isn't actually a looping emote at all (per an official
/// Square Enix forum bug report: it plays a single pass and stops, so
/// Character.Mode never reaches EmoteLoop for it) - it's back to /cackle
/// (ModeParam 98, confirmed working via the process below).
///
/// The specific numeric ModeParam values for /shakedrink (76), /cackle
/// (98), /attention (29), and /guard (58) were NOT guessed - no public
/// verified mapping from FFXIVClientStructs' "EmoteMode" index back to
/// a named emote was found, so rather than guess (which given this
/// project's history with duplicate/misleading IDs elsewhere - see
/// JobScale.cs's ResolveActionId - would likely be wrong), all four
/// were confirmed via /milkmeter emotedebug while actually
/// performing each emote and reading the real value back. All remain
/// fully user-configurable in the settings window in case a game update
/// changes them, or for swapping to yet another (looping) emote later -
/// a value of -1 falls back to matching ANY looping emote rather than
/// one specific one.
///
/// player.Address (the native object's memory address, reinterpreted
/// here as a FFXIVClientStructs Character*) is a new API surface for
/// this project - it's an extremely standard, fundamental property on
/// Dalamud's game object wrappers, so confidence is high, but it hasn't
/// been exercised in this codebase outside this class.
/// </summary>
public sealed class EmoteLoopTracker(IObjectTable objectTable)
{
    public (CharacterModes Mode, byte ModeParam) ReadState()
    {
        var player = objectTable.LocalPlayer;
        if (player is null)
            return (CharacterModes.None, 0);

        unsafe
        {
            var character = (Character*)player.Address;
            if (character is null)
                return (CharacterModes.None, 0);

            return (character->Mode, character->ModeParam);
        }
    }

    /// <summary>True while any looping emote is active, further narrowed to only Configuration.ShakeDrinkEmoteModeParam's specific value once that's been set (non-negative).</summary>
    public bool IsShakeDrinkActive(Configuration configuration) => IsMatchingEmoteActive(configuration.ShakeDrinkEmoteModeParam);

    /// <summary>Mirror of IsShakeDrinkActive() for Configuration.CackleEmoteModeParam.</summary>
    public bool IsCackleActive(Configuration configuration) => IsMatchingEmoteActive(configuration.CackleEmoteModeParam);

    /// <summary>Mirror of IsShakeDrinkActive() for Configuration.AttentionEmoteModeParam - /attention, confirmed ModeParam 29, used to wake the HUD gauge from its idle fade rather than affect the scale itself.</summary>
    public bool IsAttentionActive(Configuration configuration) => IsMatchingEmoteActive(configuration.AttentionEmoteModeParam);

    /// <summary>Mirror of IsShakeDrinkActive() for Configuration.GuardEmoteModeParam - /guard, confirmed ModeParam 58, forced by Plugin.cs's guard-auto-trigger rather than affecting the scale itself.</summary>
    public bool IsGuardActive(Configuration configuration) => IsMatchingEmoteActive(configuration.GuardEmoteModeParam);

    /// <summary>Mirror of IsShakeDrinkActive() for Configuration.CharmedEmoteModeParam - the "Charmed" crowd-control status (ModeParam 33), used by Plugin.cs to suppress the guard auto-trigger while active per request, so it doesn't interrupt it.</summary>
    public bool IsCharmedActive(Configuration configuration) => IsMatchingEmoteActive(configuration.CharmedEmoteModeParam);

    /// <summary>Mirror of IsShakeDrinkActive() for Configuration.BallDanceEmoteModeParam - the "Ball Dance" emote/animation (ModeParam 6), used by Plugin.cs to suppress the guard auto-trigger while active per request, so it doesn't interrupt it.</summary>
    public bool IsBallDanceActive(Configuration configuration) => IsMatchingEmoteActive(configuration.BallDanceEmoteModeParam);

    /// <summary>
    /// True while SOME looping emote is active that is NOT one of the
    /// four specifically tracked ones (shakedrink, cackle, attention,
    /// guard). Originally used by Plugin.cs's guard-auto-trigger as one of
    /// its firing conditions, but that requirement was removed per
    /// request - the guard trigger no longer checks this at all. Kept
    /// around purely as diagnostic info in GetDebugInfo() below (still
    /// genuinely useful to see), not because anything depends on its
    /// result functionally anymore.
    ///
    /// Correctly honors the "-1 = matches ANY looping emote" wildcard
    /// convention the other four properties already use - if any one of
    /// them were set to -1, that would mean "every looping emote counts
    /// as this one," so nothing could ever be genuinely "other" while
    /// that's configured; a plain numeric != comparison against -1
    /// would have missed this (a byte ModeParam is never actually -1).
    /// </summary>
    public bool IsOtherLoopingEmoteActive(Configuration configuration)
    {
        var (mode, modeParam) = ReadState();
        if (mode != CharacterModes.EmoteLoop)
            return false;

        if (configuration.ShakeDrinkEmoteModeParam < 0
            || configuration.CackleEmoteModeParam < 0
            || configuration.AttentionEmoteModeParam < 0
            || configuration.GuardEmoteModeParam < 0)
            return false;

        return modeParam != configuration.ShakeDrinkEmoteModeParam
            && modeParam != configuration.CackleEmoteModeParam
            && modeParam != configuration.AttentionEmoteModeParam
            && modeParam != configuration.GuardEmoteModeParam;
    }

    private bool IsMatchingEmoteActive(int configuredModeParam)
    {
        var (mode, modeParam) = ReadState();
        if (mode != CharacterModes.EmoteLoop)
            return false;

        return configuredModeParam < 0 || modeParam == configuredModeParam;
    }

    public string GetDebugInfo(Configuration configuration)
    {
        var (mode, modeParam) = ReadState();
        return $"Mode: {mode} ({(byte)mode}), ModeParam: {modeParam}\n" +
            $"Configured ShakeDrinkEmoteModeParam: {configuration.ShakeDrinkEmoteModeParam} " +
            $"({(configuration.ShakeDrinkEmoteModeParam < 0 ? "unset - matches ANY looping emote" : "set - matches only this specific value")}), " +
            $"currently active: {IsShakeDrinkActive(configuration)}\n" +
            $"Configured CackleEmoteModeParam: {configuration.CackleEmoteModeParam} " +
            $"({(configuration.CackleEmoteModeParam < 0 ? "unset - matches ANY looping emote" : "set - matches only this specific value")}), " +
            $"currently active: {IsCackleActive(configuration)}\n" +
            $"Configured AttentionEmoteModeParam: {configuration.AttentionEmoteModeParam} " +
            $"({(configuration.AttentionEmoteModeParam < 0 ? "unset - matches ANY looping emote" : "set - matches only this specific value")}), " +
            $"currently active: {IsAttentionActive(configuration)}\n" +
            $"Configured GuardEmoteModeParam: {configuration.GuardEmoteModeParam} " +
            $"({(configuration.GuardEmoteModeParam < 0 ? "unset - matches ANY looping emote" : "set - matches only this specific value")}), " +
            $"currently active: {IsGuardActive(configuration)}\n" +
            $"Configured CharmedEmoteModeParam: {configuration.CharmedEmoteModeParam} " +
            $"({(configuration.CharmedEmoteModeParam < 0 ? "unset - matches ANY looping emote" : "set - matches only this specific value")}), " +
            $"currently active: {IsCharmedActive(configuration)} (suppresses the guard auto-trigger while active)\n" +
            $"Configured BallDanceEmoteModeParam: {configuration.BallDanceEmoteModeParam} " +
            $"({(configuration.BallDanceEmoteModeParam < 0 ? "unset - matches ANY looping emote" : "set - matches only this specific value")}), " +
            $"currently active: {IsBallDanceActive(configuration)} (suppresses the guard auto-trigger while active)\n" +
            $"Some OTHER (untracked) looping emote active: {IsOtherLoopingEmoteActive(configuration)}";
    }
}
