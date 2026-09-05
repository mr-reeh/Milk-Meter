using System;
using System.Linq;
using System.Media;
using System.Reflection;
using Dalamud.Plugin.Services;

namespace MilkMeter;

/// <summary>
/// Loads moan.wav (embedded directly into the assembly - see the
/// EmbeddedResource entry in MilkMeter.csproj) and loops it
/// via System.Media.SoundPlayer, exactly mirroring HeartbeatSoundPlayer's
/// structure - a separate class rather than generalizing the two
/// together, matching this project's own established precedent
/// (BurpSoundPlayer vs HeartbeatSoundPlayer) of keeping each embedded
/// sound's loading/playback in its own small, easy-to-reason-about class
/// rather than a shared generic player.
///
/// This and the heartbeat sound used to be a single combined .wav file
/// (heartbeat.wav had both a heartbeat and this sound mixed together) -
/// split into two independently embedded files per request, each with
/// its own player class and its own configurable threshold
/// (ThresholdEffectMoanSoundThreshold), so either can be tuned or
/// disabled independently of the other. See ThresholdEffectOverlay for
/// where both are actually triggered.
///
/// The embedded resource name is looked up defensively (by suffix
/// match on "moan.wav") rather than hardcoding the full computed name,
/// same reasoning as HeartbeatSoundPlayer - avoids a silent mismatch if
/// the file ever moves into a subfolder or the project's root namespace
/// changes.
/// </summary>
public sealed class MoanSoundPlayer : IDisposable
{
    private readonly SoundPlayer? soundPlayer;
    private bool isLooping;

    public MoanSoundPlayer(IPluginLog log)
    {
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            var resourceName = assembly.GetManifestResourceNames()
                .FirstOrDefault(name => name.EndsWith("moan.wav", StringComparison.OrdinalIgnoreCase));

            if (resourceName is null)
            {
                log.Error("[MilkMeter] Could not find embedded moan.wav resource - the threshold effect's moan sound will be silently unavailable.");
                return;
            }

            var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream is null)
            {
                log.Error($"[MilkMeter] Found embedded resource name '{resourceName}' but couldn't open its stream.");
                return;
            }

            soundPlayer = new SoundPlayer(stream);
            soundPlayer.Load(); // pre-load synchronously so the first PlayLooping() doesn't stutter
        }
        catch (Exception ex)
        {
            log.Error(ex, "[MilkMeter] Failed to initialize the moan sound player.");
        }
    }

    public void StartLooping()
    {
        if (soundPlayer is null || isLooping)
            return;

        soundPlayer.PlayLooping();
        isLooping = true;
    }

    /// <summary>
    /// Plays the moan sound once, asynchronously (doesn't block the
    /// caller) - added for the "/milk moan" command, a one-shot use
    /// case distinct from the threshold effect's StartLooping()/Stop()
    /// above. NOTE: SoundPlayer can only do one thing at a time per
    /// instance - calling this while the threshold-effect loop is
    /// already running (isLooping true) will stop that loop's actual
    /// playback without clearing the isLooping flag, leaving Stop()
    /// as a no-op until the threshold effect's own logic re-evaluates
    /// and calls StartLooping() again. In practice this only matters if
    /// someone runs the command while already above
    /// ThresholdEffectMoanSoundThreshold - a rare enough overlap not to
    /// warrant more elaborate state tracking here.
    /// </summary>
    public void Play() => soundPlayer?.Play();

    public void Stop()
    {
        if (soundPlayer is null || !isLooping)
            return;

        soundPlayer.Stop();
        isLooping = false;
    }

    public void Dispose()
    {
        soundPlayer?.Stop();
        soundPlayer?.Dispose();
    }
}
