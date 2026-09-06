namespace MilkMeter;

/// <summary>
/// Shared two-segment percent-mapping math for the DTR (server info
/// bar) display - used identically by both meters this plugin tracks
/// (breast scale against Minimum/Maximum-Out-of-Combat/Maximum-In-Combat,
/// waist scale against Minimum/Baseline/Maximum), so it lives here as a
/// domain-neutral utility rather than inside either JobScale.cs or
/// WaistScale.cs specifically. Originally written as JobScale.ComputePercent
/// with Job-specific "OutOfCombat"/"InCombat" parameter names, back
/// before the waist/hunger feature was merged into this plugin -
/// renamed and relocated here once it needed to serve both meters, to
/// avoid the misleading combat-state terminology when called for waist.
/// </summary>
public static class ScalePercent
{
    /// <summary>
    /// Maps scale to a percentage: minScale to midScale maps onto
    /// 0%-100%, and midScale to maxScale maps onto 100%-200% - two
    /// separate linear segments rather than one formula across the
    /// whole range, since midScale isn't necessarily the midpoint of
    /// minScale/maxScale (all three are independently configurable
    /// sliders with no relative clamping enforced between them, for
    /// either meter).
    ///
    /// midScale is defensively clamped into [minScale, maxScale] for
    /// purposes of this calculation only (not mutating any actual
    /// config value) - neither meter's settings window enforces its own
    /// three sliders to stay relatively ordered, so a pathological
    /// config could otherwise give one of the two segments a
    /// zero-or-negative span; that case returns a flat 100% for
    /// whichever segment it affects, rather than a nonsensical or
    /// divide-by-zero result. Not clamped to [0, 200] overall - a scale
    /// genuinely outside [minScale, maxScale] (which can legitimately
    /// happen for breast scale in Food/Mana modes, whose own scale
    /// ranges are completely independent of the Job sliders reused for
    /// this calculation) extrapolates naturally outside 0%-200% rather
    /// than being forced back into range.
    /// </summary>
    public static float ComputeTwoSegmentPercent(float scale, float minScale, float midScale, float maxScale)
    {
        var clampedMid = System.Math.Clamp(midScale, minScale, maxScale);

        if (scale <= clampedMid)
        {
            var span = clampedMid - minScale;
            if (span <= 0f)
                return 100f;

            var fraction = (scale - minScale) / span;
            return fraction * 100f;
        }
        else
        {
            var span = maxScale - clampedMid;
            if (span <= 0f)
                return 100f;

            var fraction = (scale - clampedMid) / span;
            return 100f + fraction * 100f;
        }
    }
}
