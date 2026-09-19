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
/// Character.Mode never reaches EmoteLoop for it) - it's back to /dazed
/// (ModeParam 79, confirmed working via the process below).
///
/// The specific numeric ModeParam values below were NOT guessed - no
/// public verified mapping from FFXIVClientStructs' "EmoteMode" index
/// back to a named emote was found, so rather than guess (which given
/// this project's history with duplicate/misleading IDs elsewhere - see
/// JobScale.cs's ResolveActionId - would likely be wrong), each was
/// confirmed via /milkmeter emotedebug while actually performing the
/// emote and reading the real value back.
///
/// These are fixed constants rather than user-configurable settings,
/// per request - they were originally exposed as sliders in the
/// settings window (with -1 meaning "match ANY looping emote") as a
/// hedge against a game update changing them, but in practice that
/// flexibility only created a way to misconfigure the plugin into
/// strange states. If a game update ever does change one,
/// /milkmeter emotedebug still reports the live value, and the
/// constant here is a one-line fix.
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

    // Fixed ModeParam values for every emote this plugin watches, all
    // confirmed via /milkmeter emotedebug (see the class doc comment).
    // Constants rather than config properties, per request.
    public const byte ShakeDrinkModeParam = 76;   // Breast Massage
    public const byte DazedModeParam = 79;        // Self Sucking Drain
    public const byte WaterModeParam = 75;        // Breast Feeding Drain
    public const byte AttentionModeParam = 29;    // wakes the HUD gauge
    public const byte AtEaseModeParam = 30;       // At-Ease auto-trigger
    public const byte CharmedModeParam = 33;      // suppresses the auto-trigger
    public const byte BallDanceModeParam = 6;     // suppresses the auto-trigger

    /// <summary>True while the /shakedrink ("Breast Massage") looping emote is active.</summary>
    public bool IsShakeDrinkActive() => IsMatchingEmoteActive(ShakeDrinkModeParam);

    /// <summary>True while the /dazed ("Self Sucking Drain") looping emote is active.</summary>
    public bool IsDazedActive() => IsMatchingEmoteActive(DazedModeParam);

    /// <summary>True while the /water ("Breast Feeding Drain") looping emote is active - a second independent drain-toward-a-floor mechanic alongside /dazed.</summary>
    public bool IsWaterActive() => IsMatchingEmoteActive(WaterModeParam);

    /// <summary>True while /attention is active - used to wake the HUD gauge from its idle fade rather than affect the scale itself.</summary>
    public bool IsAttentionActive() => IsMatchingEmoteActive(AttentionModeParam);

    /// <summary>True while /atease is active - forced by Plugin.cs's At-Ease auto-trigger rather than affecting the scale itself.</summary>
    public bool IsAtEaseActive() => IsMatchingEmoteActive(AtEaseModeParam);

    /// <summary>True while the "Charmed" crowd-control status is active - used by Plugin.cs to suppress the At-Ease auto-trigger so it doesn't interrupt it.</summary>
    public bool IsCharmedActive() => IsMatchingEmoteActive(CharmedModeParam);

    /// <summary>True while the "Ball Dance" emote/animation is active - used by Plugin.cs to suppress the At-Ease auto-trigger so it doesn't interrupt it.</summary>
    public bool IsBallDanceActive() => IsMatchingEmoteActive(BallDanceModeParam);

    /// <summary>
    /// True while SOME looping emote is active that is NOT one of the
    /// five specifically tracked ones (shakedrink, dazed, water,
    /// attention, atease). Originally used by Plugin.cs's At-Ease
    /// auto-trigger as one of its firing conditions, but that
    /// requirement was removed per request - the trigger no longer
    /// checks this at all. Kept purely as diagnostic info in
    /// GetDebugInfo() below (still genuinely useful to see), not because
    /// anything depends on its result functionally anymore.
    ///
    /// The old "-1 means match ANY looping emote" wildcard handling this
    /// used to need is gone along with the configurable ModeParams -
    /// every value is now a fixed, specific constant, so a plain !=
    /// comparison against each is sufficient.
    /// </summary>
    public bool IsOtherLoopingEmoteActive()
    {
        var (mode, modeParam) = ReadState();
        if (mode != CharacterModes.EmoteLoop)
            return false;

        return modeParam != ShakeDrinkModeParam
            && modeParam != DazedModeParam
            && modeParam != WaterModeParam
            && modeParam != AttentionModeParam
            && modeParam != AtEaseModeParam;
    }

    private bool IsMatchingEmoteActive(int configuredModeParam)
    {
        var (mode, modeParam) = ReadState();
        if (mode != CharacterModes.EmoteLoop)
            return false;

        return modeParam == configuredModeParam;
    }

    public string GetDebugInfo()
    {
        var (mode, modeParam) = ReadState();
        return $"Mode: {mode} ({(byte)mode}), ModeParam: {modeParam}\n" +
            $"ShakeDrink (fixed {ShakeDrinkModeParam}), currently active: {IsShakeDrinkActive()}\n" +
            $"Dazed (fixed {DazedModeParam}), currently active: {IsDazedActive()}\n" +
            $"Water (fixed {WaterModeParam}), currently active: {IsWaterActive()}\n" +
            $"Attention (fixed {AttentionModeParam}), currently active: {IsAttentionActive()}\n" +
            $"At-Ease (fixed {AtEaseModeParam}), currently active: {IsAtEaseActive()}\n" +
            $"Charmed (fixed {CharmedModeParam}), currently active: {IsCharmedActive()} (suppresses the At-Ease auto-trigger while active)\n" +
            $"Ball Dance (fixed {BallDanceModeParam}), currently active: {IsBallDanceActive()} (suppresses the At-Ease auto-trigger while active)\n" +
            $"Some OTHER (untracked) looping emote active: {IsOtherLoopingEmoteActive()}";
    }
}
