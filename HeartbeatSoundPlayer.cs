using System;
using System.Linq;
using System.Media;
using System.Reflection;
using Dalamud.Plugin.Services;

namespace MilkMeter;

/// <summary>
/// Loads heartbeat.wav (embedded directly into the assembly - see the
/// EmbeddedResource entry in MilkMeter.csproj) and loops it
/// via System.Media.SoundPlayer, a standard .NET BCL class (Windows-only,
/// which is fine - Dalamud plugins only ever run on Windows, even under
/// a compatibility layer). Not a Dalamud-specific or native-memory API
/// at all, so this is lower-risk than most of what's genuinely novel
/// elsewhere in this project.
///
/// A NAudio-backed version briefly replaced this specifically to support
/// continuous volume ramping, but that requirement was dropped per
/// request - reverted back to this simpler SoundPlayer version rather
/// than keeping the extra third-party dependency around unused.
/// SoundPlayer has no volume control at all, which is fine now that
/// nothing needs one - the threshold effect's heartbeat sound is on/off
/// only, gated by its own independent
/// ThresholdEffectHeartbeatSoundThreshold value (see
/// ThresholdEffectOverlay) - a separate, independently configurable
/// moan sound (MoanSoundPlayer) used to be mixed directly into this
/// same file, split out per request into its own file/player/threshold.
///
/// The uploaded WAV files have needed conversion to standard 16-bit PCM
/// before embedding each time - the first was Microsoft ADPCM (a
/// compressed format SoundPlayer is known to be unreliable with), a
/// later replacement was 32-bit PCM (which also risks compatibility
/// issues with SoundPlayer specifically) - 16-bit PCM is the format
/// this class has actually been confirmed to work with.
///
/// The embedded resource name is looked up defensively (by suffix
/// match on "heartbeat.wav") rather than hardcoding the full computed
/// name (RootNamespace + "." + filename) - this avoids a silent
/// mismatch if the file ever moves into a subfolder or the project's
/// root namespace changes, at the cost of assuming there's only one
/// resource ending that way.
/// </summary>
public sealed class HeartbeatSoundPlayer : IDisposable
{
    private readonly SoundPlayer? soundPlayer;
    private bool isLooping;

    public HeartbeatSoundPlayer(IPluginLog log)
    {
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            var resourceName = assembly.GetManifestResourceNames()
                .FirstOrDefault(name => name.EndsWith("heartbeat.wav", StringComparison.OrdinalIgnoreCase));

            if (resourceName is null)
            {
                log.Error("[MilkMeter] Could not find embedded heartbeat.wav resource - the threshold effect's sound will be silently unavailable.");
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
            log.Error(ex, "[MilkMeter] Failed to initialize the heartbeat sound player.");
        }
    }

    public void StartLooping()
    {
        if (soundPlayer is null || isLooping)
            return;

        soundPlayer.PlayLooping();
        isLooping = true;
    }

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
