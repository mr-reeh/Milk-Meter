using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace MilkMeter;

/// <summary>
/// A full-screen pink hazy vignette - darker and more saturated toward
/// the screen edges, fading toward the center - whose intensity ramps
/// continuously as the applied scale rises from
/// Configuration.ThresholdEffectRampStartScale (0% intensity) to
/// Configuration.ThresholdEffectRampEndScale (100% intensity), rather
/// than snapping on/off at a single threshold. Uses
/// ImGui.GetForegroundDrawList() (the same technique already proven for
/// the milk particle burst), so it renders on top of the actual game
/// frame - but drawing on top is fundamentally different from blurring
/// what's already there. A true blur would need shader/post-processing
/// access to the rendering pipeline itself, which nothing in this
/// project has any established path to - this overlay is the closest
/// achievable approximation with the tools already in use here, not a
/// technical blur.
///
/// The vignette itself is built from AddRectFilledMultiColor - a
/// standard Dear ImGui draw-list function for 4-corner gradient fills,
/// not previously used elsewhere in this project (everything else has
/// used solid-color AddRectFilled) - four edge strips, each fading from
/// the configured tint color at the outer edge to fully transparent at
/// the inner edge. The four corners ALSO use AddRectFilledMultiColor
/// (not a flat AddRectFilled, which an earlier version used and which
/// created a visible solid-square seam against the smoothly-gradiented
/// edges) - each corner square gets its own radial-style fade, fully
/// opaque at the true screen corner and transparent along both inner
/// edges, so it blends continuously into the two adjacent edge strips
/// rather than looking like a distinct flat block.
///
/// On top of the continuous value-based ramp, Configuration.
/// ThresholdEffectFadeSeconds still adds a layer of temporal easing
/// (currentAlpha chasing the ramp fraction over time) rather than the
/// vignette tracking the ramp instantly - this smooths out any rapid
/// flicker if the scale hovers right at the ramp boundary, on top of
/// the ramp's own inherent smoothness as the scale itself changes.
///
/// The heartbeat and moan sounds are each a plain on/off trigger,
/// independent of the vignette's own ramp and independent of each
/// other - the heartbeat starts once the applied scale reaches
/// Configuration.ThresholdEffectHeartbeatSoundThreshold, and the moan
/// sound starts once it reaches Configuration.ThresholdEffectMoanSoundThreshold
/// - two separately configurable values. These used to be a single
/// combined heartbeat.wav file with both sounds mixed together; split
/// into two independently embedded files and players per request, so
/// each can be tuned or disabled on its own. A NAudio-backed version
/// briefly added
/// continuous volume ramping tied to the vignette's ramp fraction, but
/// that requirement was dropped per request in favor of this simpler
/// independent-threshold approach - see HeartbeatSoundPlayer and
/// MoanSoundPlayer, both back to System.Media.SoundPlayer.
///
/// The vignette and both sounds completely stop the instant
/// Configuration.ScalingPaused is true (see Draw()'s own early exit) -
/// an instant hard stop rather than the usual fade, since pausing is
/// meant to freeze everything about scaling immediately, not ease out
/// of it. They resume normally, no special-casing needed, the moment
/// it's unpaused again.
/// </summary>
public sealed class ThresholdEffectOverlay(Configuration configuration, HeartbeatSoundPlayer heartbeatSoundPlayer, MoanSoundPlayer moanSoundPlayer)
{
    private float currentAlpha;
    private double lastUpdateTime = -1d;

    public void Draw(float currentValue)
    {
        // Completely stops while Configuration.ScalingPaused is true,
        // per request - an instant hard stop (currentAlpha snapped
        // straight to 0, bypassing the normal fade-out entirely) rather
        // than a gradual fade, and both sounds stop immediately too.
        // Resumes normally the instant it's unpaused - nothing special
        // is needed for that; the usual ramp/fade logic below just
        // takes back over, easing currentAlpha back up from 0 if the
        // current scale still warrants it.
        if (configuration.ScalingPaused)
        {
            heartbeatSoundPlayer.Stop();
            moanSoundPlayer.Stop();
            currentAlpha = 0f;
            lastUpdateTime = NowSeconds();
            return;
        }

        var now = NowSeconds();
        var delta = lastUpdateTime < 0d ? 0f : (float)(now - lastUpdateTime);
        lastUpdateTime = now;

        var rampStart = configuration.ThresholdEffectRampStartScale;
        var rampEnd = configuration.ThresholdEffectRampEndScale;
        var rampRange = rampEnd - rampStart;
        var rampFraction = rampRange > 0f
            ? System.Math.Clamp((currentValue - rampStart) / rampRange, 0f, 1f)
            : (currentValue >= rampStart ? 1f : 0f);

        var targetAlpha = configuration.ThresholdEffectEnabled ? rampFraction : 0f;

        if (configuration.ThresholdEffectFadeSeconds > 0f)
        {
            var maxStep = delta / configuration.ThresholdEffectFadeSeconds;
            currentAlpha = currentAlpha < targetAlpha
                ? System.Math.Min(targetAlpha, currentAlpha + maxStep)
                : System.Math.Max(targetAlpha, currentAlpha - maxStep);
        }
        else
        {
            currentAlpha = targetAlpha;
        }

        // Each sound is a plain on/off trigger, independent of the
        // vignette's own ramp fraction above AND independent of each
        // other - each has its own separately configurable threshold,
        // no volume control (SoundPlayer doesn't have one, and nothing
        // needs one anymore).
        var heartbeatActive = configuration.ThresholdEffectHeartbeatSoundEnabled
            && configuration.ThresholdEffectEnabled
            && currentValue >= configuration.ThresholdEffectHeartbeatSoundThreshold;

        if (heartbeatActive)
            heartbeatSoundPlayer.StartLooping();
        else
            heartbeatSoundPlayer.Stop();

        var moanActive = configuration.ThresholdEffectMoanSoundEnabled
            && configuration.ThresholdEffectEnabled
            && currentValue >= configuration.ThresholdEffectMoanSoundThreshold;

        if (moanActive)
            moanSoundPlayer.StartLooping();
        else
            moanSoundPlayer.Stop();

        if (currentAlpha <= 0f)
            return;

        var viewport = ImGui.GetMainViewport();
        var screenMin = viewport.Pos;
        var screenMax = viewport.Pos + viewport.Size;
        var screenSize = viewport.Size;

        var drawList = ImGui.GetForegroundDrawList();

        var edgeThickness = screenSize.Y * configuration.ThresholdEffectVignetteSize;
        var maxTintAlpha = configuration.ThresholdEffectIntensity * currentAlpha;

        var tint = new Vector4(configuration.ThresholdEffectColorR, configuration.ThresholdEffectColorG, configuration.ThresholdEffectColorB, maxTintAlpha);
        var outerColor = ImGui.GetColorU32(tint);
        var innerColor = ImGui.GetColorU32(new Vector4(tint.X, tint.Y, tint.Z, 0f));

        // Top edge: opaque at the very top, fading to transparent moving down.
        drawList.AddRectFilledMultiColor(
            screenMin,
            new Vector2(screenMax.X, screenMin.Y + edgeThickness),
            outerColor, outerColor, innerColor, innerColor);

        // Bottom edge: opaque at the very bottom, fading to transparent moving up.
        drawList.AddRectFilledMultiColor(
            new Vector2(screenMin.X, screenMax.Y - edgeThickness),
            screenMax,
            innerColor, innerColor, outerColor, outerColor);

        // Left edge: opaque at the very left, fading to transparent moving right.
        drawList.AddRectFilledMultiColor(
            screenMin,
            new Vector2(screenMin.X + edgeThickness, screenMax.Y),
            outerColor, innerColor, innerColor, outerColor);

        // Right edge: opaque at the very right, fading to transparent moving left.
        drawList.AddRectFilledMultiColor(
            new Vector2(screenMax.X - edgeThickness, screenMin.Y),
            screenMax,
            innerColor, outerColor, outerColor, innerColor);

        // Four corners: each gets its own radial-style AddRectFilledMultiColor
        // fade (opaque at the TRUE screen corner, transparent along both
        // inner edges) rather than a flat AddRectFilled block - a flat
        // block there created a visible solid-square seam against the
        // smoothly-gradiented edges, since a hard-edged opaque square
        // sitting directly against a fading gradient strip is an
        // obvious discontinuity.
        var topLeft = new Vector2(screenMin.X + edgeThickness, screenMin.Y + edgeThickness);
        drawList.AddRectFilledMultiColor(screenMin, topLeft, outerColor, innerColor, innerColor, innerColor);

        var topRightMin = new Vector2(screenMax.X - edgeThickness, screenMin.Y);
        var topRightMax = new Vector2(screenMax.X, screenMin.Y + edgeThickness);
        drawList.AddRectFilledMultiColor(topRightMin, topRightMax, innerColor, outerColor, innerColor, innerColor);

        var bottomLeftMin = new Vector2(screenMin.X, screenMax.Y - edgeThickness);
        var bottomLeftMax = new Vector2(screenMin.X + edgeThickness, screenMax.Y);
        drawList.AddRectFilledMultiColor(bottomLeftMin, bottomLeftMax, innerColor, innerColor, innerColor, outerColor);

        var bottomRightMin = screenMax - new Vector2(edgeThickness, edgeThickness);
        drawList.AddRectFilledMultiColor(bottomRightMin, screenMax, innerColor, innerColor, outerColor, innerColor);
    }

    private static double NowSeconds() =>
        System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
}
