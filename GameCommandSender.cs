using System;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Shell;

namespace MilkMeter;

/// <summary>
/// Programmatically executes a raw game chat/slash command, as if the
/// player had typed it into the chatbox and pressed enter. Built
/// specifically to force "/attention motion" when the Self Sucking
/// Threshold is crossed (see Plugin.cs's cackle-drain branch).
///
/// The exact method here - RaptureShellModule.ExecuteCommandInner(Utf8String*
/// command, UIModule* uiModule) - was confirmed directly from a real
/// compiled FFXIVClientStructs.dll via Visual Studio's own IntelliSense
/// tooltip (not a guess, and not something this session could pin down
/// through web research alone - see the project's earlier attempts).
/// UIModule.Instance() follows the same singleton pattern already
/// proven working for RaptureShellModule.Instance() elsewhere in this
/// file, so confidence here is high.
///
/// Still defensively wrapped in try/catch by the caller (see Plugin.cs)
/// regardless - cheap insurance against any remaining runtime surprise
/// (e.g. calling this at a moment the game doesn't expect), so a
/// failure logs once rather than crashing the plugin or spamming errors
/// every frame.
/// </summary>
public static class GameCommandSender
{
    public static unsafe void SendCommand(string command)
    {
        var utf8Command = Utf8String.FromString(command);
        try
        {
            RaptureShellModule.Instance()->ExecuteCommandInner(utf8Command, UIModule.Instance());
        }
        finally
        {
            utf8Command->Dtor(true);
        }
    }
}
