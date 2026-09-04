using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace MilkMeter;

/// <summary>
/// Always-on-screen "boob gauge" HUD - a borderless overlay drawn to
/// look like a baby bottle (pink cap, yellow nipple, clear glass body)
/// that fills with a customizable color ("milk", creamy white by
/// default) from the BOTTOM up as the current applied chest scale rises
/// toward whatever range the active mode uses - i.e. vertically, not the
/// old horizontal left-to-right bar. An optional bold outlined label
/// (blank by default) is drawn OVERLAID on the center of the bottle
/// body (not stacked above it, no space reserved for it) - its own font
/// size is independently configurable via Configuration.HudLabelFontScale.
///
/// The nipple is a two-circle silhouette (a small waist bump on top -
/// with the glossy highlight - and a wider flare
/// at the base where it meets the cap) - matches the reference clipart's
/// shape much better than the plain capsule or round-bulb designs used
/// in earlier versions. The feeding-hole dot an earlier version had was
/// removed per request.
///
/// The whole bottle scales uniformly from a fixed set of base
/// dimensions via Configuration.HudScale, rather than independently
/// adjustable width/height (which doesn't make sense for a shape that
/// needs specific proportions to actually read as a bottle rather than
/// an arbitrary rectangle) - this replaces the old HudWidth/HudHeight
/// properties entirely.
///
/// Toggled, scaled, repositioned, labeled, and recolored entirely from
/// the settings window (Configuration.ShowHudGauge / HudLocked /
/// HudPositionX / HudPositionY / HudScale / HudLabelText /
/// HudLabelFontScale / HudFillColorR/G/B) - while HudLocked is true the
/// bottle is fixed in place; uncheck "Lock HUD Position" in settings to
/// drag it somewhere else, then re-check it to lock the new position in.
/// NOTE: unlike an earlier version, the locked state is no longer fully
/// click-through (NoInputs) - left-clicking the bottle while locked now
/// toggles Configuration.ScalingPaused, freezing every mechanic that
/// would modify or push chest scale (and, per a later request, always
/// resetting scale to exactly 1.0 the instant a click pauses it - see
/// Plugin.cs's ResetScaleToBaselineForPause and the onPausedByClick
/// callback below). The gauge itself and its particle
/// effects keep rendering as before (frozen at whatever scale was
/// applied at the moment of pausing), but the threshold effect
/// (vignette, glow, heartbeat sound) completely stops the instant it's
/// paused, per request - see ThresholdEffectOverlay.Draw() and this
/// class's own glow block below. This is a real trade-off: the window
/// now intercepts clicks meant for the game if anything else happens
/// to sit underneath it, which it never did before. UPDATED: the
/// whole bottle renders at 10% opacity while paused (multiplied
/// directly onto fadeAlpha, so idle-fade/hover-reveal still apply on
/// top of it) - replaces an earlier big red X drawn across the bottle,
/// which was itself a replacement for an even earlier plain "PAUSED"
/// text label; see DrawBottle's own local alpha calculation for
/// specifics. Also, the gauge
/// is no longer forced fully visible for the entire time it's paused -
/// per request, it now follows the same idle-fade rules as any other
/// state, so a forgotten paused gauge doesn't sit on screen forever.
/// Discoverability is instead handled by the hover-forces-visible
/// behavior described below: mousing over the gauge's position brings
/// it back regardless of how faded it's gotten, so it stays reachable
/// without opening settings, just not permanently on-screen.
///
/// NEW: the whole bottle (and its local particle burst) fades out after
/// Configuration.HudFadeIdleSeconds of no genuine "activity" event, and
/// fades back in the instant one occurs - plus an independent "hide
/// entirely while out of combat" toggle (Configuration.HudHideOutOfCombat).
/// Both conditions are combined by taking whichever wants the gauge MORE
/// hidden (Math.Min of their two target alphas), and the fade transition
/// itself eases over Configuration.HudFadeDurationSeconds rather than
/// snapping instantly. This required threading a computed fadeAlpha
/// value through every single color construction in the drawing code
/// below - each drawing method defines its own small local Col(r,g,b,a)
/// helper that multiplies the requested alpha by fadeAlpha, specifically
/// so the multiplication can't accidentally be missed at any one of the
/// many call sites. The particle burst is drawn at full alpha regardless
/// (unaffected by the fade) - it's a momentary event-triggered effect,
/// not a persistent thing that needs to visually match the gauge's
/// current fade state.
///
/// UPDATED: hovering the mouse over the gauge always forces it fully
/// visible (fadeAlpha itself back to 1 - though while paused, the
/// separate 10%-opacity multiplier described further below still
/// applies on top of that, so "fully visible" while paused tops out at
/// 10%, not 100%), overriding the idle/combat fade (though not an
/// instant snap - it still eases in over HudFadeDurationSeconds like
/// any other fade-target change). This is what makes the paused case
/// specifically (see below) actually usable: since
/// Configuration.ScalingPaused no
/// longer forces the gauge visible outright, a paused-and-idled gauge
/// fades out same as any other idle gauge - hovering over its (still
/// perfectly clickable; alpha never affects hit-testing) last known
/// position is what brings it back so you can actually see and click it
/// to unpause. Implemented via an isHoveredLastFrame field rather than
/// querying hover state directly inside UpdateFade() - ImGui can only
/// answer "is this window hovered" AFTER that window's Begin() for the
/// current frame, but the fade alpha needs to already be known BEFORE
/// drawing, so this necessarily reads one frame behind current mouse
/// position. Same one-frame-lag tradeoff the paused-toggle click
/// detection below already lives with.
///
/// "Genuine activity" is deliberately NOT "the applied scale's raw value
/// changed" - an earlier version tracked that directly, but passive
/// growth alone (Passive Scale Gen quietly ticking the value upward while
/// nothing else is happening) would then count as "activity" and could
/// keep the gauge visible indefinitely even while nothing worth
/// attention is actually going on. Instead, the idle timer only resets
/// via an explicit WakeFromIdle() call, which Plugin.cs makes at
/// specific deliberate-action points: any Trigger()ed particle burst
/// (ability-use/damage-taken reductions, the repeating dazed-drain
/// burst), the increase-variant events that don't fire a burst, active
/// shakedrink-boosted growth, and the dedicated /attention emote (which
/// exists specifically to "check the gauge's status" without needing to
/// use an ability or take damage first). Passive growth by itself is
/// deliberately NOT one of these BY DEFAULT - see Plugin.cs for exactly
/// where each WakeFromIdle() call lives. The one opt-in exception is
/// HudShowAboveScaleEnabled: once turned on, the applied scale value
/// itself (regardless of what's driving it, passive growth included)
/// counts as activity for as long as it's at or above
/// HudShowAboveScaleThreshold - a deliberate choice to let the scale
/// value's own magnitude double as a wake condition, off by default
/// same as every other opt-in HUD behavior in this file.
///
/// Drawing note carried over from the original bar version: this reads
/// its own SetNextWindowPos/SetNextWindowSize values directly for all
/// drawing math, rather than querying ImGui.GetWindowPos()/
/// GetWindowSize() back out after Begin() - which can return stale/zero
/// values on the very same frame they were just set, silently collapsing
/// custom-drawn shapes to nothing. GetWindowPos() is still used, but only
/// to capture drag movement for the *next* frame's position.
///
/// One known minor cosmetic limitation carried over: the milk fill's top
/// edge is a flat rectangle (only its bottom corners are rounded to
/// match the body), since it moves up and down as the fraction changes.
/// Barely noticeable except right at 100% full.
/// </summary>
public sealed class HudGaugeWindow(Configuration configuration, Func<float> getAppliedScale, Func<bool> getInCombat, Action onPausedByClick)
{
    // Base dimensions at HudScale = 1.0. All multiplied by
    // Configuration.HudScale when actually drawing. Matches the
    // reference clipart's proportions: cap width equals body width, cap
    // is short/flat relative to its width, nipple has a small waist bump
    // on top flaring wider at the base rather than a plain capsule.
    private const float BaseNippleWidth = 42f;
    private const float BaseNippleHeight = 48f;
    private const float BaseCapWidth = 74f;
    private const float BaseCapHeight = 24f;
    private const float BaseBodyWidth = 74f;
    private const float BaseBodyHeight = 148f;

    // The only remaining milk-particle effect, now that the screen-wide
    // burst has been removed entirely - a small "sparkle around the
    // bottle" using the shared particle physics, drawn via the
    // foreground draw list (not this window's own local one) so
    // particles aren't clipped to the small bottle window's bounds.
    private const int BaseLocalParticleCount = 16;
    private const float LocalMinSpeed = 90f;
    private const float LocalMaxSpeed = 180f;
    private const float LocalMinLifeSeconds = 0.4f;
    private const float LocalMaxLifeSeconds = 0.8f;
    private const float LocalGravityPerSecondSquared = 220f;

    private readonly MilkParticleBurst localBurst = new();

    // Fade-on-idle state. lastValueChangeTime starts at -1 (sentinel);
    // UpdateFade() treats that specifically as "just became active" on
    // the very first call, rather than immediately appearing idle before
    // anything has had a chance to happen.
    private double lastValueChangeTime = -1d;
    private float fadeAlpha = 1f;
    private double lastFadeUpdateTime = -1d;

    // Set at the end of the Begin() block each frame from that frame's
    // real ImGui.IsWindowHovered() result - see the class doc comment
    // for why UpdateFade() has to read last frame's value instead of
    // querying hover directly itself.
    private bool isHoveredLastFrame = false;

    /// <summary>
    /// Spawns the bottle-local burst, recomputing the bottle's current
    /// screen position directly from Configuration (same geometry Draw()
    /// itself uses) rather than caching a position from a previous
    /// Draw() call - avoids any staleness concern entirely, at the cost
    /// of duplicating a few lines of size math. intensity is passed by
    /// Plugin.cs - higher for ability-use reductions than for
    /// damage-taken/dazed-drain ones.
    ///
    /// Origin is the very top tip of the bottle (the top edge of the
    /// nipple region), not the body's center - and speed/gravity/droplet
    /// radius are all scaled by HudScale (see Draw()) so the whole burst
    /// grows or shrinks along with the bottle itself rather than staying
    /// a fixed size regardless of how big the bottle is drawn.
    ///
    /// Always wakes the gauge from its idle fade (see WakeFromIdle()),
    /// even if ShowMilkBurstEffect is off - so the wake-on-activity
    /// behavior doesn't silently stop working just because someone
    /// turned off the visual particles specifically. Only the actual
    /// particle-spawning below is gated behind that toggle.
    /// </summary>
    public void Trigger(float intensity = 1f)
    {
        WakeFromIdle();

        if (!configuration.ShowMilkBurstEffect || !configuration.ShowHudGauge)
            return;

        var scale = configuration.HudScale;
        var bodySize = new Vector2(BaseBodyWidth, BaseBodyHeight) * scale;
        var capWidth = BaseCapWidth * scale;
        var totalWidth = MathF.Max(capWidth, bodySize.X);

        var windowPos = new Vector2(configuration.HudPositionX, configuration.HudPositionY);
        var bottleTopTip = new Vector2(windowPos.X + totalWidth * 0.5f, windowPos.Y);

        var particleCount = (int)(BaseLocalParticleCount * intensity);
        localBurst.Trigger(bottleTopTip, particleCount, LocalMinSpeed * scale, LocalMaxSpeed * scale, LocalMinLifeSeconds, LocalMaxLifeSeconds);
    }

    /// <summary>
    /// Resets the idle timer, so the gauge either stays visible or
    /// starts fading back in on the very next Draw() call. Called by
    /// Trigger() itself, and separately by Plugin.cs for events that
    /// should count as "activity" without necessarily firing a particle
    /// burst (the increase-variant events, active shakedrink-boosted
    /// growth, the dedicated /attention/guard emotes, and - if
    /// HudShowAboveScaleEnabled is on - the applied scale being at or
    /// above HudShowAboveScaleThreshold).
    /// </summary>
    public void WakeFromIdle() => lastValueChangeTime = NowSeconds();

    public void Draw()
    {
        UpdateFade();

        if (configuration.ShowHudGauge)
        {
            var flags = ImGuiWindowFlags.NoTitleBar
                | ImGuiWindowFlags.NoResize
                | ImGuiWindowFlags.NoScrollbar
                | ImGuiWindowFlags.NoCollapse
                | ImGuiWindowFlags.NoSavedSettings
                | ImGuiWindowFlags.NoBackground;

            if (configuration.HudLocked)
                flags |= ImGuiWindowFlags.NoMove;

            var scale = configuration.HudScale;
            var nippleSize = new Vector2(BaseNippleWidth, BaseNippleHeight) * scale;
            var capSize = new Vector2(BaseCapWidth, BaseCapHeight) * scale;
            var bodySize = new Vector2(BaseBodyWidth, BaseBodyHeight) * scale;

            var totalWidth = MathF.Max(capSize.X, bodySize.X);
            var totalHeight = nippleSize.Y + capSize.Y + bodySize.Y;

            var windowPos = new Vector2(configuration.HudPositionX, configuration.HudPositionY);
            var windowSize = new Vector2(totalWidth, totalHeight);

            ImGui.SetNextWindowPos(windowPos, configuration.HudLocked ? ImGuiCond.Always : ImGuiCond.FirstUseEver);
            ImGui.SetNextWindowSize(windowSize, ImGuiCond.Always);

            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
            // Explicit transparent override as a second guarantee alongside
            // NoBackground, in case NoBackground alone doesn't fully suppress
            // the default window frame in every theme.
            ImGui.PushStyleColor(ImGuiCol.WindowBg, new Vector4(0f, 0f, 0f, 0f));

            if (ImGui.Begin("##MilkMeterHudGauge", flags))
            {
                // While unlocked (repositioning mode), the window is
                // draggable - read its position back so whatever spot you
                // drop it at gets used for NEXT frame's SetNextWindowPos
                // (and saved once you re-lock it from the settings window).
                // This is deliberately NOT used for THIS frame's drawing
                // below - see the class-level note on why.
                if (!configuration.HudLocked)
                {
                    var draggedPos = ImGui.GetWindowPos();
                    configuration.HudPositionX = draggedPos.X;
                    configuration.HudPositionY = draggedPos.Y;
                }

                // Left-click toggles Configuration.ScalingPaused - only
                // while locked, not while actively repositioning
                // (unlocked), so a click that's really the start of a
                // drag doesn't get misread as a pause-toggle. This is
                // the reason NoInputs was removed from the flags above
                // for the locked case - a real trade-off: the window
                // now intercepts clicks meant for the game if anything
                // else happens to sit underneath it, which the old
                // click-through behavior never did.
                //
                // hovered is also stashed into isHoveredLastFrame here,
                // for UpdateFade() to consume next frame - see the
                // class doc comment and that field's own comment for
                // why it can't just be queried directly over there.
                var hovered = ImGui.IsWindowHovered();
                isHoveredLastFrame = hovered;

                if (configuration.HudLocked && hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                {
                    configuration.ScalingPaused = !configuration.ScalingPaused;
                    configuration.Save();

                    // Only when the click just turned pausing ON (not
                    // when it turned pausing back off) - per request,
                    // pausing via the gauge always snaps scale to 1.0
                    // rather than freezing wherever it happened to be.
                    // Plugin.cs owns what "scale" actually means (which
                    // internal field(s) to reset, and pushing the
                    // change to Customize+ immediately despite the
                    // per-frame update loop itself being skipped while
                    // paused) - this window has no access to any of
                    // that, so it's entirely delegated via this
                    // callback rather than reaching into Plugin.cs
                    // internals from here.
                    if (configuration.ScalingPaused)
                        onPausedByClick();
                }

                // Scales this window's font for the rest of the frame's text
                // draws (both ImGui.CalcTextSize and drawList.AddText pick up
                // the current font scale) - independent of HudScale, per the
                // dedicated "Text Size" slider in settings. No matching "pop"
                // call is needed; it resets automatically on the next Begin().
                ImGui.SetWindowFontScale(configuration.HudLabelFontScale);

                DrawBottle(windowPos, totalWidth, nippleSize, capSize, bodySize);
            }

            ImGui.End();
            ImGui.PopStyleColor();
            ImGui.PopStyleVar(2);
        }

        // Drawn AFTER the bottle window above (not before it, as in an
        // earlier version) so the particle layer sits on top of the
        // bottle and both gauges regardless of any draw-list-timing
        // subtlety - ImGui.GetForegroundDrawList() content should
        // already composite above all regular windows regardless of
        // call order, but drawing it last removes any doubt rather than
        // relying on that alone. Drawn at full alpha regardless of the
        // gauge's own fade state - see the class doc comment for why.
        // Gravity and droplet radius both scale with HudScale (speed
        // already does, at Trigger() time) so the whole burst grows or
        // shrinks along with the bottle rather than staying fixed size.
        if (configuration.ShowMilkBurstEffect)
        {
            var burstScale = configuration.HudScale;
            var localColor = new Vector4(configuration.HudFillColorR, configuration.HudFillColorG, configuration.HudFillColorB, 0.9f);
            localBurst.Draw(localColor, LocalGravityPerSecondSquared * burstScale, burstScale);
        }
    }

    /// <summary>
    /// Recomputes fadeAlpha for this frame. Two independent conditions
    /// can each want the gauge hidden - idling too long (see the class
    /// doc comment for exactly what counts as "not idle"), or configured
    /// to hide out of combat while actually out of combat - and
    /// Math.Min of their two target alphas means EITHER one hides it;
    /// both need to want it visible for it to show. The idle timer
    /// itself is advanced entirely by external WakeFromIdle() calls, not
    /// anything computed in here - on the very first call ever
    /// (lastValueChangeTime still at its -1 sentinel), it's treated as
    /// "just became active" so the gauge starts visible rather than
    /// already idle before anything has had a chance to happen.
    ///
    /// UPDATED: Configuration.ScalingPaused no longer overrides the two
    /// hide conditions above - per request, a paused gauge now fades on
    /// idle same as any other state, rather than being forced visible
    /// for the entire time it's paused. What DOES still override both
    /// conditions is isHoveredLastFrame - hovering the gauge (whether
    /// paused or not) always forces it fully visible, which is what
    /// keeps a faded, paused gauge reachable: mouse over its position
    /// and it reappears so you can see and click it to unpause.
    /// </summary>
    private void UpdateFade()
    {
        var now = NowSeconds();

        if (lastValueChangeTime < 0d)
            lastValueChangeTime = now;

        var idleSeconds = now - lastValueChangeTime;
        var idleFadeTarget = (configuration.HudFadeOnIdleEnabled && idleSeconds >= configuration.HudFadeIdleSeconds) ? 0f : 1f;
        var combatHideTarget = (configuration.HudHideOutOfCombat && !getInCombat()) ? 0f : 1f;
        var targetAlpha = isHoveredLastFrame ? 1f : Math.Min(idleFadeTarget, combatHideTarget);

        var fadeDelta = lastFadeUpdateTime < 0d ? 0f : (float)(now - lastFadeUpdateTime);
        lastFadeUpdateTime = now;

        if (configuration.HudFadeDurationSeconds > 0f)
        {
            var maxStep = fadeDelta / configuration.HudFadeDurationSeconds;
            fadeAlpha = fadeAlpha < targetAlpha
                ? Math.Min(targetAlpha, fadeAlpha + maxStep)
                : Math.Max(targetAlpha, fadeAlpha - maxStep);
        }
        else
        {
            fadeAlpha = targetAlpha;
        }
    }

    private void DrawBottle(Vector2 windowPos, float totalWidth, Vector2 nippleSize, Vector2 capSize, Vector2 bodySize)
    {
        var drawList = ImGui.GetWindowDrawList();
        // Used specifically for the bottle's own outline strokes below -
        // root cause found and confirmed: BaseCapWidth and BaseBodyWidth
        // are both exactly 74, so totalWidth (the window's own width) is
        // sized EXACTLY equal to the body's width, zero horizontal
        // margin. Since bodyMin.X/bodyMax.X sit exactly at the window's
        // own left/right edges, and a stroke is centered on its given
        // coordinate (half its width extends outward each side), half
        // of every side stroke was being clipped off by the window's
        // own bounds when drawn via the regular window drawList - this
        // is the exact same "content needs to extend beyond this small
        // window" problem the milk particle burst and radiating glow
        // effect elsewhere in this file already solved the same way.
        var foregroundDrawList = ImGui.GetForegroundDrawList();
        // While paused, the whole bottle (outline, fill, ticks, label -
        // everything drawn via the local Col() below) renders at 10%
        // opacity instead of its usual fadeAlpha-only value - replaces
        // the earlier big red X paused indicator per request, for
        // something less visually loud. Multiplied directly onto
        // fadeAlpha rather than replacing it outright, so the idle-fade
        // and hover-reveal behavior described in the class doc comment
        // still apply on top of this - a paused-and-hovered gauge still
        // reaches full 10% (not the deeper idle-faded alpha it'd
        // otherwise have), and a paused-and-idled gauge fades toward 0
        // starting from that same 10% ceiling rather than from 100%.
        var alpha = fadeAlpha * (configuration.ScalingPaused ? 0.10f : 1f);
        uint Col(float r, float g, float b, float a) => ImGui.GetColorU32(new Vector4(r, g, b, a * alpha));

        // Outline color for the whole bottle shape (nipple, cap, body) -
        // black per request (was light gray briefly, dark red/maroon
        // before that).
        var outline = Col(0f, 0f, 0f, 1.0f);
        // Outline thickness, scaled by HudScale so it stays
        // proportionate at any configured bottle size, same approach
        // already used for the paused-indicator X further below. 4f at
        // HudScale 1.0 - a clear, substantial increase from the 1.5f/2f
        // this replaced, per request ("very thick").
        var outlineThickness = configuration.HudScale * 4f;
        var centerX = windowPos.X + totalWidth * 0.5f;

        // --- Radiating glow behind the bottle, active while the
        // threshold effect's ramp fraction is nonzero - drawn FIRST
        // (before any bottle shapes) so it appears to originate from
        // behind the bottle. Uses ImGui.GetForegroundDrawList()
        // (deliberately NOT the window's own drawList used for
        // everything below), the same lesson MilkParticleBurst already
        // established for this exact window - an earlier version used
        // the window's own draw list, which clips to the small bottle
        // window's rectangular bounds, so the glow's circles (larger
        // than the window) were getting cut off flat at the window
        // edges, looking like solid squares rather than a radiating
        // glow. Uses the same tint color as the vignette
        // (ThresholdEffectOverlay) for thematic consistency, and
        // independently recomputes the same ramp-fraction math that
        // class uses internally rather than requiring a direct
        // reference between the two classes - keeps them decoupled at
        // the cost of duplicating a small amount of math. "Flashing" is
        // approximated as a smooth, rhythmic sine-wave brightness pulse
        // rather than a literal on/off strobe, which could be visually
        // harsh or a photosensitivity concern. Completely skipped while
        // Configuration.ScalingPaused is true, per request - the glow
        // would otherwise keep evaluating against whatever frozen
        // scale value was applied at the moment of pausing. ---
        if (!configuration.ScalingPaused && configuration.ThresholdEffectEnabled && configuration.ThresholdEffectGlowEnabled)
        {
            var rampStart = configuration.ThresholdEffectRampStartScale;
            var rampEnd = configuration.ThresholdEffectRampEndScale;
            var rampRange = rampEnd - rampStart;
            var currentValue = getAppliedScale();
            var glowFraction = rampRange > 0f
                ? Math.Clamp((currentValue - rampStart) / rampRange, 0f, 1f)
                : (currentValue >= rampStart ? 1f : 0f);

            if (glowFraction > 0f)
            {
                var totalHeight = nippleSize.Y + capSize.Y + bodySize.Y;
                var glowCenter = new Vector2(centerX, windowPos.Y + totalHeight * 0.5f);

                var pulseNow = (float)NowSeconds();
                var pulse = MathF.Sin(pulseNow * configuration.ThresholdEffectGlowPulseSpeed * MathF.PI * 2f) * 0.5f + 0.5f;
                var maxGlowAlpha = configuration.ThresholdEffectGlowIntensity * glowFraction * (0.5f + pulse * 0.5f) * alpha;

                // More rings than before (8, not 4) for a visibly
                // smoother gradient with less banding, now that the
                // glow is actually large enough for banding to be
                // noticeable.
                const int ringCount = 8;
                var maxRadius = totalWidth * configuration.ThresholdEffectGlowSize;
                for (var i = ringCount; i >= 1; i--)
                {
                    var ringFraction = i / (float)ringCount;
                    var radius = maxRadius * ringFraction;
                    var ringAlpha = maxGlowAlpha * (1f - ringFraction);
                    var ringColor = ImGui.GetColorU32(new Vector4(configuration.ThresholdEffectColorR, configuration.ThresholdEffectColorG, configuration.ThresholdEffectColorB, ringAlpha));
                    foregroundDrawList.AddCircleFilled(glowCenter, radius, ringColor);
                }
            }
        }

        // --- Nipple: a two-circle silhouette (small waist bump on top,
        // wider flare at the base where it meets the cap) - the dome
        // circle from an earlier version was removed per request. ---
        var nippleColor = Col(0.94f, 0.72f, 0.42f, 1f);
        var highlightColor = Col(1f, 0.92f, 0.72f, 0.85f);

        var nippleBottom = windowPos.Y + nippleSize.Y;

        var waistRadius = nippleSize.X * 0.22f;
        var flareRadius = nippleSize.X * 0.46f;

        var waistCenter = new Vector2(centerX, windowPos.Y + nippleSize.Y * 0.30f);
        var flareCenter = new Vector2(centerX, nippleBottom - nippleSize.Y * 0.20f);

        // Drawn WAIST first, THEN flare, per request to fix a real bug:
        // an earlier version drew flare first / waist second and colored
        // the waist's own outline to match the fill so the overlap seam
        // would blend in - but since the waist's circumference extends
        // well beyond the flare's own radius (confirmed by the actual
        // geometry: distance between centers is noticeably larger than
        // the flare's radius), that also erased the DISTINCT black
        // border from the whole exposed top portion of the nipple, not
        // just the overlap seam - visible in testing as a missing
        // outline on roughly the top half of the nipple.
        //
        // This ordering fixes it without needing to compute the two
        // circles' exact intersection points and draw partial arcs (a
        // more precise fix, but with real risk of getting the arc-angle
        // math subtly wrong without being able to visually verify it
        // directly): draw the waist FIRST with its own full BLACK
        // outline (so its entire circumference, including the exposed
        // top, gets a real border) - then draw the flare's OPAQUE fill
        // on top SECOND, which naturally covers/erases whatever portion
        // of the waist's outline falls within the flare's own circular
        // area (since that part is now "inside" the combined silhouette,
        // not on its outer edge) - then the flare's own outline is drawn
        // last, giving it a clean border too. One small residual case
        // this doesn't fully resolve: right at the two points where the
        // circles' boundaries actually cross, the flare's own outline
        // (drawn last, nothing after it to cover it) still traces a
        // short arc through the small sliver of its own circumference
        // that dips into the waist's circle - worth checking after this
        // change to see if that's visually noticeable or negligible.
        //
        // IMPORTANT: the fills below are ALSO drawn on foregroundDrawList
        // now, not the regular window drawList - this was a real bug
        // introduced when the outlines moved to the foreground list to
        // fix the earlier clipping issue. The foreground list always
        // renders on top of ALL window content regardless of call order,
        // so once only the OUTLINES moved there, a later-drawn fill on
        // the regular drawList could no longer cover an earlier-drawn
        // foreground-list outline at all - breaking this exact
        // "later element covers earlier element" layering the seam fix
        // depends on. Keeping fills and outlines together on the same
        // list, in the same waist -> flare -> cap order, restores
        // correct, order-dependent layering throughout.
        foregroundDrawList.AddCircleFilled(waistCenter, waistRadius, nippleColor);
        foregroundDrawList.AddCircle(waistCenter, waistRadius, outline, 0, outlineThickness);

        foregroundDrawList.AddCircleFilled(flareCenter, flareRadius, nippleColor);
        foregroundDrawList.AddCircle(flareCenter, flareRadius, outline, 0, outlineThickness);

        // Small glossy highlight on the waist.
        var highlightCenter = waistCenter + new Vector2(-waistRadius * 0.35f, -waistRadius * 0.3f);
        foregroundDrawList.AddCircleFilled(highlightCenter, waistRadius * 0.3f, highlightColor);

        // --- Cap: pink band between the nipple and the bottle body.
        // Only the TOP corners are rounded (matching a real cap's flat
        // bottom where it meets the bottle neck) - rounding all four
        // corners left the bottom two tapering inward, exposing the
        // body's pale glass-fill color peeking through the gap. An
        // earlier version had a subtle divider line and two diagonal
        // white shine streaks approximating a glossy-plastic look, but
        // these were removed per request in favor of a plain solid
        // pink cap. Drawn AFTER the nipple circles above (same
        // foregroundDrawList, per request) so the cap's pink correctly
        // overlaps/covers both orange circles and their outlines
        // wherever they overlap, rather than the nipple's outlines
        // always winning regardless of draw order. ---
        var capMin = new Vector2(centerX - capSize.X * 0.5f, nippleBottom - capSize.Y * 0.22f);
        var capMax = capMin + capSize;
        var capRounding = capSize.Y * 0.35f;
        foregroundDrawList.AddRectFilled(capMin, capMax, Col(0.98f, 0.62f, 0.72f, 1f), capRounding, ImDrawFlags.RoundCornersTop);

        foregroundDrawList.AddRect(capMin, capMax, outline, capRounding, ImDrawFlags.RoundCornersTop, outlineThickness);

        // --- Body: clear "glass" bottle, fills with milk from the bottom up.
        // Top corners kept square (only bottom rounded, matching the
        // bottle resting on a surface) - this is drawn AFTER the cap, so
        // rounded top corners here would let this semi-transparent glass
        // fill bleed a faint tint over the cap's bottom edge instead of
        // sitting flush against it. ---
        var bodyMin = new Vector2(centerX - bodySize.X * 0.5f, capMax.Y - capSize.Y * 0.1f);
        var bodyMax = bodyMin + bodySize;
        var bodyRounding = bodySize.X * 0.18f;

        // Empty-glass look: pale, cool blue-gray tint matching the
        // reference image's frosted-glass color - alpha dropped from an
        // earlier 0.35 to near-invisible per request, while still faintly
        // present so the bottle's own outline doesn't vanish when empty.
        drawList.AddRectFilled(bodyMin, bodyMax, Col(0.80f, 0.86f, 0.92f, 0.08f), bodyRounding, ImDrawFlags.RoundCornersBottom);

        if (configuration.Mode == ScaleMode.Job)
            DrawJobThreeZoneFill(bodyMin, bodyMax, bodySize, bodyRounding, alpha);
        else
            DrawSingleZoneFill(bodyMin, bodyMax, bodySize, bodyRounding, alpha);

        // Measurement tick marks on the right side, matching the
        // reference image - drawn after the fill so they stay visible
        // regardless of the current fill level.
        var tickColor = Col(0.4f, 0.15f, 0.18f, 0.65f);
        var tickX = bodyMax.X - bodySize.X * 0.16f;
        var tickLength = bodySize.X * 0.14f;
        const int tickCount = 7;
        for (var i = 0; i < tickCount; i++)
        {
            var t = i / (float)(tickCount - 1);
            var y = bodyMin.Y + bodySize.Y * (0.32f + 0.58f * t);
            drawList.AddLine(new Vector2(tickX, y), new Vector2(tickX + tickLength, y), tickColor, 1.2f);
        }

        foregroundDrawList.AddRect(bodyMin, bodyMax, outline, bodyRounding, ImDrawFlags.RoundCornersBottom, outlineThickness);

        // Explicit reinforcement for the two straight vertical sides,
        // layered on top - added because the rendered result showed the
        // sides noticeably thinner than the top/bottom edges despite
        // using the same outlineThickness in the single AddRect call
        // above, for a reason not confidently identified (possibly a
        // stroke-generation difference AddRect applies between the
        // straight portions and the rounded-bottom-only mixed corners of
        // this specific rectangle - not verified, since this couldn't be
        // tested by rendering it directly). AddLine is used here instead
        // since it's already proven elsewhere in this file (tick marks,
        // earlier cap shine streaks) to render with precise, predictable
        // thickness, giving a direct guarantee independent of whatever
        // AddRect is doing internally. Stops short of the bottom
        // rounding so it doesn't clash with AddRect's own handling of
        // the actual rounded corners there - only reinforces the
        // straight portion of each side.
        var sideBottom = bodyMax.Y - bodyRounding;
        foregroundDrawList.AddLine(new Vector2(bodyMin.X, bodyMin.Y), new Vector2(bodyMin.X, sideBottom), outline, outlineThickness);
        foregroundDrawList.AddLine(new Vector2(bodyMax.X, bodyMin.Y), new Vector2(bodyMax.X, sideBottom), outline, outlineThickness);

        // --- Label: overlaid on the center of the bottle body (both
        // axes), not stacked above the whole bottle - drawn last so it
        // renders on top of the glass/milk. White with a black outline,
        // matching the game's own bold outlined HUD text style. There's
        // no built-in "outlined text" draw call, so this approximates it
        // the standard immediate-mode-GUI way: draw the same text
        // several times at small pixel offsets in black first, then once
        // more in white exactly on-target on top. ---
        if (!string.IsNullOrEmpty(configuration.HudLabelText))
        {
            var label = configuration.HudLabelText;
            var labelSize = ImGui.CalcTextSize(label);
            var labelPos = bodyMin + (bodySize - labelSize) * 0.5f;

            var black = Col(0f, 0f, 0f, 1f);
            var white = Col(1f, 1f, 1f, 1f);

            Span<Vector2> outlineOffsets =
            [
                new(-1, -1), new(0, -1), new(1, -1),
                new(-1, 0), new(1, 0),
                new(-1, 1), new(0, 1), new(1, 1),
            ];

            foreach (var offset in outlineOffsets)
                drawList.AddText(labelPos + offset, black, label);

            drawList.AddText(labelPos, white, label);
        }

    }

    /// <summary>
    /// Food/Mana mode fill: a single continuous color across the whole
    /// body, unchanged from the original design - these modes don't have
    /// an analogous "baseline within range" concept the way Job mode
    /// does, so there's nothing to split into two zones for.
    /// </summary>
    private void DrawSingleZoneFill(Vector2 bodyMin, Vector2 bodyMax, Vector2 bodySize, float bodyRounding, float alpha)
    {
        var drawList = ImGui.GetWindowDrawList();
        var (min, max) = GetRangeForCurrentMode();
        var value = getAppliedScale();
        var fraction = max > min ? Math.Clamp((value - min) / (max - min), 0f, 1f) : 0f;

        if (fraction <= 0f)
            return;

        var fillColor = ImGui.GetColorU32(new Vector4(configuration.HudFillColorR, configuration.HudFillColorG, configuration.HudFillColorB, 0.95f * alpha));
        var fillHeight = bodySize.Y * fraction;
        var fillMin = new Vector2(bodyMin.X, bodyMax.Y - fillHeight);
        // Only the bottom corners are rounded to match the body's
        // rounded bottom - see the class-level note on why the top edge
        // stays flat.
        drawList.AddRectFilled(fillMin, bodyMax, fillColor, bodyRounding, ImDrawFlags.RoundCornersBottom);
    }

    /// <summary>
    /// Job mode fill: three gauges overlaid directly on top of each
    /// other, each independently spanning the FULL bottle height (not
    /// split into separate stacked zones) - like a Kingdom Hearts boss
    /// health bar where each successive bar grows over the ones before
    /// it as its own threshold is crossed, rather than gauges sharing a
    /// boundary line partway up.
    ///
    /// The six boundary points - HudGauge1/2/3 Start/End Scale - are all
    /// fully independent of each other and of JobCombatFloorScale/
    /// JobBaselineScale/JobUpperLimitScale (Maximum Scaling floor/Out/In
    /// of Combat) - purely visual thresholds for this gauge display, set
    /// however makes sense for the HUD regardless of what the actual
    /// growth/ceiling mechanics are configured to. They can overlap,
    /// leave a gap, or even run in either direction; each gauge falls
    /// back to fully filled once value reaches or exceeds its own start
    /// if its own range is degenerate (start >= end).
    ///
    /// Gauge 1 (HudFillColorR/G/B, "the white one") maps
    /// HudGauge1StartScale -> HudGauge1EndScale across the whole body
    /// height. It reaches 100% (fully filled) once value reaches its
    /// end, and stays fully filled beyond that - it never shrinks on its
    /// own.
    ///
    /// Gauge 2 (HudFillColor2R/G/B) maps HudGauge2StartScale ->
    /// HudGauge2EndScale, ALSO across the whole body height, and is
    /// drawn AFTER gauge 1 - so wherever it's filled, it visually covers
    /// gauge 1 underneath. It stays at 0 (nothing drawn, gauge 1 fully
    /// visible) until value actually reaches its own start, then grows
    /// from the bottom up, progressively covering more of gauge 1 as
    /// value approaches its own end, where it covers the entire body.
    ///
    /// Gauge 3 (HudFillColor3R/G/B) maps HudGauge3StartScale ->
    /// HudGauge3EndScale, ALSO across the whole body height, and is
    /// drawn AFTER gauge 2 - so wherever it's filled, it visually covers
    /// BOTH gauge 1 and gauge 2 underneath. UNLIKE gauges 1 and 2,
    /// per request gauge 3 is INVERTED (fills as value goes DOWN toward
    /// its start, rather than up toward its end - fully filled at or
    /// below HudGauge3StartScale, empty at or above HudGauge3EndScale)
    /// AND fills from the TOP of the body downward, rather than from the
    /// bottom up the way gauges 1 and 2 do.
    /// </summary>
    private void DrawJobThreeZoneFill(Vector2 bodyMin, Vector2 bodyMax, Vector2 bodySize, float bodyRounding, float alpha)
    {
        var drawList = ImGui.GetWindowDrawList();
        var value = getAppliedScale();

        // Gauge 1: HudGauge1StartScale -> HudGauge1EndScale, full body height.
        var start1 = configuration.HudGauge1StartScale;
        var end1 = configuration.HudGauge1EndScale;
        var range1 = end1 - start1;
        var fraction1 = range1 > 0f ? Math.Clamp((value - start1) / range1, 0f, 1f) : (value >= start1 ? 1f : 0f);

        if (fraction1 > 0f)
        {
            var color1 = ImGui.GetColorU32(new Vector4(configuration.HudFillColorR, configuration.HudFillColorG, configuration.HudFillColorB, 0.95f * alpha));
            var fillHeight1 = bodySize.Y * fraction1;
            var fillMin1 = new Vector2(bodyMin.X, bodyMax.Y - fillHeight1);
            // Only rounds the bottom corners, matching the body's
            // rounded bottom - same reasoning as the single-zone fill.
            drawList.AddRectFilled(fillMin1, bodyMax, color1, bodyRounding, ImDrawFlags.RoundCornersBottom);
        }

        // Gauge 2: HudGauge2StartScale -> HudGauge2EndScale, ALSO full
        // body height - drawn on top, covering gauge 1 wherever it's
        // filled. Same fallback logic as gauge 1 above (unified now that
        // all three are independent, symmetric ranges rather than one
        // being chained off another's boundary).
        var start2 = configuration.HudGauge2StartScale;
        var end2 = configuration.HudGauge2EndScale;
        var range2 = end2 - start2;
        var fraction2 = range2 > 0f ? Math.Clamp((value - start2) / range2, 0f, 1f) : (value >= start2 ? 1f : 0f);

        if (fraction2 > 0f)
        {
            var color2 = ImGui.GetColorU32(new Vector4(configuration.HudFillColor2R, configuration.HudFillColor2G, configuration.HudFillColor2B, 0.95f * alpha));
            var fillHeight2 = bodySize.Y * fraction2;
            var fillMin2 = new Vector2(bodyMin.X, bodyMax.Y - fillHeight2);
            drawList.AddRectFilled(fillMin2, bodyMax, color2, bodyRounding, ImDrawFlags.RoundCornersBottom);
        }

        // Gauge 3: HudGauge3StartScale -> HudGauge3EndScale, ALSO full
        // body height, drawn on top of BOTH gauge 1 and gauge 2 - but
        // INVERTED (1 minus the normal fraction, per request) and
        // anchored at the TOP of the body rather than the bottom, so it
        // fills downward as value drops toward its start instead of
        // upward as value rises toward its end. Rounding is only applied
        // to the bottom corners once this reaches full coverage (its
        // fill actually reaches the body's true bottom edge) - for any
        // partial fill, its bottom edge sits somewhere mid-body and
        // shouldn't be rounded (nothing there to match), same reasoning
        // as why gauges 1/2's TOP edge is never rounded regardless of
        // their own fraction.
        var start3 = configuration.HudGauge3StartScale;
        var end3 = configuration.HudGauge3EndScale;
        var range3 = end3 - start3;
        var fraction3 = range3 > 0f ? Math.Clamp((value - start3) / range3, 0f, 1f) : (value >= start3 ? 1f : 0f);
        var invertedFraction3 = 1f - fraction3;

        if (invertedFraction3 > 0f)
        {
            var color3 = ImGui.GetColorU32(new Vector4(configuration.HudFillColor3R, configuration.HudFillColor3G, configuration.HudFillColor3B, 0.95f * alpha));
            var fillHeight3 = bodySize.Y * invertedFraction3;
            var fillMax3 = new Vector2(bodyMax.X, bodyMin.Y + fillHeight3);
            var roundingFlags3 = invertedFraction3 >= 1f ? ImDrawFlags.RoundCornersBottom : ImDrawFlags.RoundCornersNone;
            drawList.AddRectFilled(bodyMin, fillMax3, color3, bodyRounding, roundingFlags3);
        }
    }

    /// <summary>
    /// The practical min/max the current mode's scale tends to sit
    /// within, purely for normalizing the bottle's fill fraction - not a
    /// hard clamp on the underlying value itself. Only called from
    /// DrawSingleZoneFill, which Job mode never uses (it always goes
    /// through DrawJobThreeZoneFill instead, with its own six independent
    /// HudGauge1/2/3 Start/End thresholds) - so there's no
    /// Job case here; if Mode is ever Job when this runs, it falls
    /// through to the generic default rather than a dedicated branch
    /// that would never actually execute.
    /// </summary>
    private (float Min, float Max) GetRangeForCurrentMode() => configuration.Mode switch
    {
        ScaleMode.Food => (configuration.FoodMinScale, configuration.FoodMaxScale),
        ScaleMode.Mana => (0.60f, 1.00f), // ManaScale's fixed range
        _ => (0.5f, 1.5f),
    };

    private static double NowSeconds() =>
        System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
}
