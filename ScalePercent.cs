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

    /// <summary>
    /// Exact inverse of ComputeTwoSegmentPercent - given a target
    /// percentage, returns the actual scale value that would produce
    /// it. Added for the Self Sucking Milk-to-Food transfer (see
    /// Plugin.cs's dazedDrainActive branch): rather than converting a
    /// drained scale amount into food scale units directly (which would
    /// need its own segment-crossing-aware logic, duplicating what this
    /// class already does), the transfer works entirely in percent-space
    /// - compute Milk's percent-point loss via ComputeTwoSegmentPercent,
    /// add those points onto Food's own current percent (via that same
    /// function), then convert the resulting target percent back into
    /// an actual waist scale value via this inverse. Correctly handles a
    /// transfer that crosses the 100% boundary on either meter, since
    /// each conversion step independently picks the right segment for
    /// whatever percent it's given.
    ///
    /// percent is clamped to [0, 200] (unlike ComputeTwoSegmentPercent's
    /// own scale parameter, which is intentionally left unclamped) since
    /// a percent this method is asked to convert is always a deliberate
    /// target value from the caller, not a raw scale reading that might
    /// legitimately fall outside the usual range.
    /// </summary>
    public static float PercentToScale(float percent, float minScale, float midScale, float maxScale)
    {
        var clampedMid = System.Math.Clamp(midScale, minScale, maxScale);
        var clampedPercent = System.Math.Clamp(percent, 0f, 200f);

        if (clampedPercent <= 100f)
        {
            var span = clampedMid - minScale;
            return minScale + span * (clampedPercent / 100f);
        }
        else
        {
            var span = maxScale - clampedMid;
            return clampedMid + span * ((clampedPercent - 100f) / 100f);
        }
    }
}
