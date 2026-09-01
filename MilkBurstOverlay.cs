using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace MilkMeter;

/// <summary>
/// A brief burst of "milk droplet" particles flying outward from the
/// center of the screen (shifted slightly upward), fired every time the
/// job scale is genuinely reduced (either a tracked ability's own
/// reduction, or the damage-taken reduction - see the two Trigger()
/// call sites in Plugin.cs, which pass a higher intensity for ability
/// use specifically, making that trigger noticeably more prominent).
/// Meant as a visual payoff for the "empty the gauge" minigame loop.
///
/// Uses ImGui.GetForegroundDrawList() (via the shared MilkParticleBurst
/// helper) rather than a dedicated window - this draws directly on top
/// of the whole game viewport without needing to manage a full-screen
/// window's flags/sizing the way HudGaugeWindow.cs does. This is the
/// first use of this specific API in the project; it's a standard,
/// well-established Dear ImGui function for exactly this kind of
/// full-screen overlay, so confidence is reasonably high, but it hasn't
/// been proven in this codebase the way GetWindowDrawList() has.
///
/// The origin point is the geometric center of the game's main
/// viewport (shifted up), not the character's actual screen-space
/// position (which would need Dalamud's world-to-screen camera
/// projection - a meaningfully bigger, separate piece of unverified API
/// surface). In typical third-person FFXIV camera framing the character
/// sits at or near screen center anyway, so this is a reasonable
/// approximation of "coming from your character" without that added risk.
/// </summary>
public sealed class MilkBurstOverlay(Configuration configuration)
{
    private const int BaseParticleCount = 55;
    private const float MinSpeed = 220f;
    private const float MaxSpeed = 420f;
    private const float MinLifeSeconds = 0.6f;
    private const float MaxLifeSeconds = 1.1f;
    private const float GravityPerSecondSquared = 260f;

    // How far above the exact viewport center the burst originates,
    // as a fraction of viewport height.
    private const float UpwardOffsetFraction = 0.12f;

    private readonly MilkParticleBurst burst = new();

    /// <summary>
    /// Spawns a burst. intensity scales the particle count relative to
    /// BaseParticleCount - Plugin.cs passes a higher value for ability-use
    /// reductions than for damage-taken reductions, making the former
    /// noticeably more prominent.
    /// </summary>
    public void Trigger(float intensity = 1f)
    {
        if (!configuration.ShowMilkBurstEffect)
            return;

        var viewport = ImGui.GetMainViewport();
        var center = viewport.Pos + viewport.Size * 0.5f;
        center.Y -= viewport.Size.Y * UpwardOffsetFraction;

        var particleCount = (int)(BaseParticleCount * intensity);
        burst.Trigger(center, particleCount, MinSpeed, MaxSpeed, MinLifeSeconds, MaxLifeSeconds);
    }

    public void Draw()
    {
        var color = new Vector4(configuration.HudFillColorR, configuration.HudFillColorG, configuration.HudFillColorB, 0.9f);
        burst.Draw(color, GravityPerSecondSquared);
    }
}
