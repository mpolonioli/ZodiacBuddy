using System;
using System.Numerics;

namespace ZodiacBuddy.Stages.Atma.Automation;

/// <summary>
///     Physically dislodges the character when navmesh path following gets stuck,
///     by overriding movement input toward a random nearby point for a short
///     moment. Ported from ZodiacBuddyReborn.
/// </summary>
internal sealed class AdvancedUnstuck : IDisposable
{
    private const double DurationSeconds = 1.0;

    private readonly OverrideMovement? movement;
    private readonly Random random = new();
    private DateTime startedAt;

    /// <summary>
    ///     Initializes a new instance of the <see cref="AdvancedUnstuck" /> class.
    /// </summary>
    public AdvancedUnstuck()
    {
        try
        {
            this.movement = new OverrideMovement();
        }
        catch (Exception ex)
        {
            // Signatures can break on game patches; degrade to jump-and-repath
            // stuck recovery instead of failing to load.
            Service.PluginLog.Warning(ex, "Movement override unavailable, unstuck maneuvers are disabled.");
        }
    }

    /// <summary>
    ///     Gets a value indicating whether an unstuck maneuver is currently running.
    /// </summary>
    public bool IsRunning => this.movement?.Enabled ?? false;

    /// <summary>
    ///     Start an unstuck maneuver toward a random nearby point.
    /// </summary>
    /// <returns>Whether the maneuver was started (or already running).</returns>
    public bool Start()
    {
        var player = Service.ObjectTable.LocalPlayer;
        if (this.movement is null || player is null)
        {
            return false;
        }

        if (this.IsRunning)
        {
            return true;
        }

        var angle = this.random.NextDouble() * Math.Tau;
        var direction = new Vector3((float)Math.Cos(angle), 0, (float)Math.Sin(angle));
        this.movement.DesiredPosition = player.Position + (direction * 5f);
        this.movement.Enabled = true;
        this.startedAt = DateTime.UtcNow;
        Service.PluginLog.Debug($"Unstuck maneuver started toward {this.movement.DesiredPosition}.");
        return true;
    }

    /// <summary>
    ///     Advance the maneuver; call once per framework tick.
    /// </summary>
    public void Update()
    {
        if (this.IsRunning && (DateTime.UtcNow - this.startedAt).TotalSeconds > DurationSeconds)
        {
            this.Stop();
        }
    }

    /// <summary>
    ///     Stop the current maneuver, if any.
    /// </summary>
    public void Stop()
    {
        if (this.movement is not null && this.movement.Enabled)
        {
            this.movement.Enabled = false;
            Service.PluginLog.Debug("Unstuck maneuver finished.");
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        this.movement?.Dispose();
    }
}
