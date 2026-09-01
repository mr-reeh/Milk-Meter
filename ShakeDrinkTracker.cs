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
/// What ISN'T verified: the specific numeric ModeParam value that
/// corresponds to /shakedrink specifically. FFXIVClientStructs' own
/// source only says ModeParam indexes into a separate "EmoteMode" table
/// for this mode - no public, verified mapping from that index back to
/// a named emote was found. Rather than guess a number - which given
/// this project's history with duplicate/misleading IDs elsewhere (see
/// JobScale.cs's ResolveActionId, built specifically because a guessed
/// "first match" turned out wrong) would very likely also be wrong -
/// Configuration.ShakeDrinkEmoteModeParam is left user-supplied. At its
/// default (-1, "unset"), IsActive() matches ANY looping emote, not
/// just /shakedrink. Use /milkmeter emotedebug while actually
/// performing /shakedrink to read your own client's real ModeParam
/// value and set it in the settings window for precise matching.
///
/// player.Address (the native object's memory address, reinterpreted
/// here as a FFXIVClientStructs Character*) is a new API surface for
/// this project - it's an extremely standard, fundamental property on
/// Dalamud's game object wrappers, so confidence is high, but it hasn't
/// been exercised in this codebase before now.
/// </summary>
public sealed class ShakeDrinkTracker(IObjectTable objectTable)
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

    /// <summary>
    /// True while any looping emote is active, further narrowed to only
    /// Configuration.ShakeDrinkEmoteModeParam's specific value once that's
    /// been set (non-negative) - see the class doc comment for why it
    /// isn't hardcoded.
    /// </summary>
    public bool IsActive(Configuration configuration)
    {
        var (mode, modeParam) = ReadState();
        if (mode != CharacterModes.EmoteLoop)
            return false;

        return configuration.ShakeDrinkEmoteModeParam < 0
            || modeParam == configuration.ShakeDrinkEmoteModeParam;
    }

    public string GetDebugInfo(Configuration configuration)
    {
        var (mode, modeParam) = ReadState();
        return $"Mode: {mode} ({(byte)mode}), ModeParam: {modeParam}\n" +
            $"Configured ShakeDrinkEmoteModeParam: {configuration.ShakeDrinkEmoteModeParam} " +
            $"({(configuration.ShakeDrinkEmoteModeParam < 0 ? "unset - matches ANY looping emote" : "set - matches only this specific value")})\n" +
            $"IsActive (per current config): {IsActive(configuration)}";
    }
}
