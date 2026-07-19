using System;
using System.Numerics;

namespace ZodiacBuddy.Stages.Atma.Automation;

/// <summary>
///     Facade over the supported combat plugins. The automations fight through
///     whichever plugin is selected in the configuration: BossMod (rotation plus
///     AoE dodging, the default) or Wrath Combo (rotation only).
/// </summary>
internal sealed class CombatIpc : IDisposable
{
    private readonly WrathComboIpc wrath;
    private readonly BossModIpc bossMod;
    private CombatPlugin? controlled;

    /// <summary>
    ///     Initializes a new instance of the <see cref="CombatIpc" /> class.
    /// </summary>
    public CombatIpc()
    {
        this.wrath = new WrathComboIpc();
        this.bossMod = new BossModIpc();
        this.wrath.LeaseCancelled += this.OnWrathLeaseCancelled;
    }

    /// <summary>
    ///     Fired when the controlled plugin revokes our control (user revoked it,
    ///     plugin unloaded, ...).
    /// </summary>
    public event Action? ControlLost;

    /// <summary>
    ///     Gets the display name of the currently configured combat plugin.
    /// </summary>
    public static string ConfiguredName => GetName(Service.Configuration.AtmaAutomation.CombatPlugin);

    /// <summary>
    ///     Gets the display name of the plugin currently under control, or the
    ///     configured one when nothing is controlled.
    /// </summary>
    public string ControlledName => this.controlled is { } plugin ? GetName(plugin) : ConfiguredName;

    /// <summary>
    ///     Gets a value indicating whether we currently control a combat plugin.
    /// </summary>
    public bool HasControl => this.controlled switch
    {
        CombatPlugin.WrathCombo => this.wrath.HasLease,
        CombatPlugin.BossMod => this.bossMod.HasControl,
        _ => false,
    };

    /// <summary>
    ///     Get the display name of a combat plugin.
    /// </summary>
    /// <param name="plugin">The combat plugin.</param>
    /// <returns>Its display name.</returns>
    public static string GetName(CombatPlugin plugin)
        => plugin == CombatPlugin.WrathCombo ? "Wrath Combo" : "BossMod";

    /// <summary>
    ///     Check whether the configured combat plugin is installed and ready.
    /// </summary>
    /// <returns>Whether it can be controlled.</returns>
    public static bool IsAvailable()
        => IsAvailable(Service.Configuration.AtmaAutomation.CombatPlugin);

    /// <summary>
    ///     Check whether the given combat plugin is installed and ready.
    /// </summary>
    /// <param name="plugin">The combat plugin.</param>
    /// <returns>Whether it can be controlled.</returns>
    public static bool IsAvailable(CombatPlugin plugin)
        => plugin == CombatPlugin.WrathCombo ? WrathComboIpc.IsAvailable() : BossModIpc.IsAvailable();

    /// <summary>
    ///     Take control of the configured combat plugin.
    /// </summary>
    /// <returns>Whether control was acquired.</returns>
    public bool BeginControl()
    {
        if (this.controlled is not null)
        {
            return true;
        }

        var plugin = Service.Configuration.AtmaAutomation.CombatPlugin;
        var acquired = plugin == CombatPlugin.WrathCombo
            ? this.wrath.BeginControl()
            : this.bossMod.BeginControl();
        if (acquired)
        {
            this.controlled = plugin;
        }

        return acquired;
    }

    /// <summary>
    ///     Tell the combat plugin whether a fight is in progress. Wrath Combo is
    ///     armed for the whole run (it never moves the character), while BossMod's
    ///     preset is only active during fights so its movement cannot interfere
    ///     with travel or teleport casts.
    /// </summary>
    /// <param name="active">Whether a fight is in progress.</param>
    public void SetCombatActive(bool active)
    {
        if (this.controlled == CombatPlugin.BossMod)
        {
            this.bossMod.SetCombatActive(active);
        }
    }

    /// <summary>
    ///     Start generating a BossMod obstacle map for a FATE area, so its dodge
    ///     movement can path around open-world terrain. No-op under Wrath Combo.
    /// </summary>
    /// <param name="fateId">Fate row ID.</param>
    /// <param name="center">A reachable point near the FATE's center.</param>
    /// <param name="radius">Radius to cover.</param>
    public void PrepareFateObstacleMap(uint fateId, Vector3 center, float radius)
    {
        if (this.controlled == CombatPlugin.BossMod)
        {
            this.bossMod.PrepareFateObstacleMap(fateId, center, radius);
        }
    }

    /// <summary>
    ///     Poll a pending obstacle map generation. No-op under Wrath Combo.
    /// </summary>
    public void TickObstacleMap()
    {
        if (this.controlled == CombatPlugin.BossMod)
        {
            this.bossMod.TickObstacleMap();
        }
    }

    /// <summary>
    ///     Release control of the combat plugins, restoring the user's settings.
    /// </summary>
    public void EndControl()
    {
        this.wrath.EndControl();
        this.bossMod.EndControl();
        this.controlled = null;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        this.wrath.LeaseCancelled -= this.OnWrathLeaseCancelled;
        this.wrath.Dispose();
        this.bossMod.EndControl();
    }

    private void OnWrathLeaseCancelled()
    {
        if (this.controlled == CombatPlugin.WrathCombo)
        {
            this.controlled = null;
            this.ControlLost?.Invoke();
        }
    }
}
