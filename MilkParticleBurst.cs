using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace MilkMeter;

/// <summary>
/// Reusable "milk droplet" particle-burst physics and rendering. Used
/// by HudGaugeWindow for its local burst around the bottle - originally
/// shared with a second, screen-wide burst (MilkBurstOverlay), which was
/// removed entirely per request, leaving this as HudGaugeWindow's sole
/// consumer. Kept as its own class regardless, since it still cleanly
/// encapsulates the particle physics on its own terms. Owns no drawing
/// surface of its own - always draws via ImGui.GetForegroundDrawList()
/// (never a window's own local draw list), specifically so particles
/// can fly freely across the screen without being clipped to a small
/// window's bounds, which a bottle-sized window's own draw list would do.
/// </summary>
public sealed class MilkParticleBurst
{
    private struct Particle
    {
        public Vector2 Position;
        public Vector2 Velocity;
        public float Life;
        public float MaxLife;
    }

    private readonly List<Particle> particles = [];
    private readonly Random rng = new();
    private double lastUpdateTime = -1d;

    /// <summary>Spawns a fresh burst of particles from the given origin point. Safe to call again before a previous burst finishes - it just adds more particles on top of whatever's still animating.</summary>
    public void Trigger(Vector2 origin, int particleCount, float minSpeed, float maxSpeed, float minLifeSeconds, float maxLifeSeconds)
    {
        for (var i = 0; i < particleCount; i++)
        {
            var angle = (float)(rng.NextDouble() * Math.PI * 2.0);
            var speed = minSpeed + (float)rng.NextDouble() * (maxSpeed - minSpeed);
            var velocity = new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * speed;
            var maxLife = minLifeSeconds + (float)rng.NextDouble() * (maxLifeSeconds - minLifeSeconds);

            particles.Add(new Particle
            {
                Position = origin,
                Velocity = velocity,
                Life = maxLife,
                MaxLife = maxLife,
            });
        }
    }

    /// <summary>Advances and draws all currently-alive particles. Safe to call every frame even with zero active particles. radiusScale multiplies the droplet size, letting a caller scale it to match whatever visual scale their emitter is drawn at.</summary>
    public void Draw(Vector4 baseColor, float gravityPerSecondSquared, float radiusScale = 1f)
    {
        var now = NowSeconds();
        var delta = lastUpdateTime < 0d ? 0f : (float)(now - lastUpdateTime);
        lastUpdateTime = now;

        if (particles.Count == 0)
            return;

        var drawList = ImGui.GetForegroundDrawList();

        for (var i = particles.Count - 1; i >= 0; i--)
        {
            var p = particles[i];
            p.Life -= delta;

            if (p.Life <= 0f)
            {
                particles.RemoveAt(i);
                continue;
            }

            // Slight downward pull over time, so droplets arc rather
            // than flying in perfectly straight lines.
            p.Velocity.Y += gravityPerSecondSquared * delta;
            p.Position += p.Velocity * delta;

            var lifeFraction = Math.Clamp(p.Life / p.MaxLife, 0f, 1f);
            var alpha = lifeFraction * baseColor.W;
            var radius = (3f + 3f * lifeFraction) * radiusScale;
            var color = ImGui.GetColorU32(new Vector4(baseColor.X, baseColor.Y, baseColor.Z, alpha));

            drawList.AddCircleFilled(p.Position, radius, color);

            particles[i] = p;
        }
    }

    private static double NowSeconds() =>
        System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
}
