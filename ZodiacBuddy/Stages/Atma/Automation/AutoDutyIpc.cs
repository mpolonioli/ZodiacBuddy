using ECommons.EzIpcManager;
using System;
using System.Linq;

namespace ZodiacBuddy.Stages.Atma.Automation;

/// <summary>
///     IPC wrapper for the AutoDuty plugin, used to run the Dungeons page of a
///     Trial of the Braves book unsynced. Ported from ZodiacBuddyReborn.
/// </summary>
internal sealed class AutoDutyIpc
{
#pragma warning disable SA1310, CS0649
    [EzIPC("ContentHasPath")]
    private readonly Func<uint, bool>? contentHasPath;

    [EzIPC("SetConfig")]
    private readonly Action<string, string>? setConfig;

    [EzIPC("Run")]
    private readonly Action<uint, int, bool>? run;

    [EzIPC("IsStopped")]
    private readonly Func<bool>? isStopped;

    [EzIPC("Stop")]
    private readonly Action? stop;
#pragma warning restore SA1310, CS0649

    /// <summary>
    ///     Initializes a new instance of the <see cref="AutoDutyIpc" /> class.
    /// </summary>
    public AutoDutyIpc()
    {
        EzIPC.Init(this, "AutoDuty");
    }

    /// <summary>
    ///     Gets a value indicating whether the AutoDuty plugin is installed and loaded.
    /// </summary>
    public static bool IsInstalled =>
        Service.Interface.InstalledPlugins.Any(p => p.InternalName == "AutoDuty" && p.IsLoaded);

    /// <summary>
    ///     Check whether AutoDuty has a path for the given duty territory.
    /// </summary>
    /// <param name="territoryId">Territory ID of the duty.</param>
    /// <returns>Whether a path is available.</returns>
    public bool HasPath(uint territoryId)
    {
        try
        {
            return this.contentHasPath?.Invoke(territoryId) ?? false;
        }
        catch (Exception ex)
        {
            Service.PluginLog.Debug(ex, "AutoDuty IPC call failed");
            return false;
        }
    }

    /// <summary>
    ///     Configure AutoDuty for an unsynced run and start it.
    /// </summary>
    /// <param name="territoryId">Territory ID of the duty.</param>
    /// <returns>Whether the run was started.</returns>
    public bool RunUnsynced(uint territoryId)
    {
        if (this.setConfig is null || this.run is null)
        {
            return false;
        }

        try
        {
            this.setConfig("Unsynced", "True");
            this.setConfig("dutyModeEnum", "Regular");
            this.run(territoryId, 1, true);
            return true;
        }
        catch (Exception ex)
        {
            Service.PluginLog.Warning(ex, "AutoDuty IPC call failed");
            return false;
        }
    }

    /// <summary>
    ///     Check whether AutoDuty is currently stopped.
    /// </summary>
    /// <returns>Whether AutoDuty is stopped.</returns>
    public bool IsStopped()
    {
        try
        {
            return this.isStopped?.Invoke() ?? true;
        }
        catch (Exception ex)
        {
            Service.PluginLog.Debug(ex, "AutoDuty IPC call failed");
            return true;
        }
    }

    /// <summary>
    ///     Stop the current AutoDuty run.
    /// </summary>
    public void Stop()
    {
        try
        {
            this.stop?.Invoke();
        }
        catch (Exception ex)
        {
            Service.PluginLog.Debug(ex, "AutoDuty IPC call failed");
        }
    }
}
