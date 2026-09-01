using System;
using System.Linq;
using System.Media;
using System.Reflection;
using Dalamud.Plugin.Services;

namespace MilkMeter;

/// <summary>
/// Loads burp.wav (embedded directly into the assembly - see the
/// EmbeddedResource entry in MilkMeter.csproj) and plays it
/// once via System.Media.SoundPlayer, the same approach
/// HeartbeatSoundPlayer uses - a separate class rather than generalizing
/// the two together, since this one is meaningfully simpler (one-shot
/// Play(), no looping/Stop() state to track).
///
/// Triggered from Plugin.cs at the exact moment the Self Sucking
/// Threshold fires "/attention motion" (see GameCommandSender) - a
/// one-shot sound effect for that specific animation change, not a
/// looping/ambient one like the heartbeat.
///
/// The embedded resource name is looked up defensively (by suffix
/// match on "burp.wav") rather than hardcoding the full computed name,
/// same reasoning as HeartbeatSoundPlayer.
/// </summary>
public sealed class BurpSoundPlayer : IDisposable
{
    private readonly SoundPlayer? soundPlayer;

    public BurpSoundPlayer(IPluginLog log)
    {
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            var resourceName = assembly.GetManifestResourceNames()
                .FirstOrDefault(name => name.EndsWith("burp.wav", StringComparison.OrdinalIgnoreCase));

            if (resourceName is null)
            {
                log.Error("[MilkMeter] Could not find embedded burp.wav resource - the Self Sucking Threshold's burp sound will be silently unavailable.");
                return;
            }

            var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream is null)
            {
                log.Error($"[MilkMeter] Found embedded resource name '{resourceName}' but couldn't open its stream.");
                return;
            }

            soundPlayer = new SoundPlayer(stream);
            soundPlayer.Load();
        }
        catch (Exception ex)
        {
            log.Error(ex, "[MilkMeter] Failed to initialize the burp sound player.");
        }
    }

    /// <summary>Plays the burp sound once, asynchronously (doesn't block the caller). Safe to call repeatedly - each call restarts playback from the beginning.</summary>
    public void Play() => soundPlayer?.Play();

    public void Dispose()
    {
        soundPlayer?.Stop();
        soundPlayer?.Dispose();
    }
}
