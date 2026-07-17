using Dalamud.Plugin.Ipc;
using System;
using WrathCombo.API;
using WrathCombo.API.Enum;

namespace ZodiacBuddy.Stages.Atma.Automation;

/// <summary>
///     IPC wrapper for the Wrath Combo plugin, based on its lease system.
/// </summary>
internal sealed class WrathComboIpc : IDisposable
{
    private const string CallbackPrefix = "ZodiacBuddy$Wrath";

    private readonly ICallGateProvider<int, string, object> callback;

    private Guid? lease;

    /// <summary>
    ///     Initializes a new instance of the <see cref="WrathComboIpc" /> class.
    /// </summary>
    public WrathComboIpc()
    {
        this.callback = Service.Interface.GetIpcProvider<int, string, object>($"{CallbackPrefix}.WrathComboCallback");
        this.callback.RegisterAction(this.OnLeaseCancelled);
    }

    /// <summary>
    ///     Fired when Wrath Combo cancels our lease (user revoked it, plugin unloaded, ...).
    /// </summary>
    public event Action? LeaseCancelled;

    /// <summary>
    ///     Gets a value indicating whether we currently hold a lease on Wrath Combo.
    /// </summary>
    public bool HasLease => this.lease.HasValue;

    /// <summary>
    ///     Check whether Wrath Combo is installed and its IPC is ready.
    /// </summary>
    /// <returns>Whether Wrath Combo can be controlled.</returns>
    public static bool IsAvailable()
    {
        try
        {
            WrathIPCWrapper.Test();
            return WrathIPCWrapper.IPCReady();
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    ///     Acquire a lease on Wrath Combo and configure its auto-rotation to fight our hard target.
    /// </summary>
    /// <returns>Whether control was acquired.</returns>
    public bool BeginControl()
    {
        if (this.lease.HasValue)
        {
            return true;
        }

        try
        {
            this.lease = WrathIPCWrapper.RegisterForLeaseWithCallback("ZodiacBuddy", "ZodiacBuddy", CallbackPrefix);
            if (!this.lease.HasValue)
            {
                Service.PluginLog.Error("Could not lease control of Wrath Combo.");
                return false;
            }

            var results = new[]
            {
                WrathIPCWrapper.SetAutoRotationState(this.lease.Value),
                WrathIPCWrapper.SetCurrentJobAutoRotationReady(this.lease.Value),

                // Only attack our hard target, to avoid pulling unrelated mobs.
                WrathIPCWrapper.SetAutoRotationConfigState(this.lease.Value, AutoRotationConfigOption.DPSRotationMode, DPSRotationMode.Manual),

                // Open the pull ourselves instead of waiting for the enemy to hit us.
                WrathIPCWrapper.SetAutoRotationConfigState(this.lease.Value, AutoRotationConfigOption.InCombatOnly, false),
                WrathIPCWrapper.SetAutoRotationConfigState(this.lease.Value, AutoRotationConfigOption.OnlyAttackInCombat, false),
                WrathIPCWrapper.SetAutoRotationConfigState(this.lease.Value, AutoRotationConfigOption.IncludeNPCs, true),
                WrathIPCWrapper.SetAutoRotationConfigState(this.lease.Value, AutoRotationConfigOption.DPSAoETargets, 3),
            };

            foreach (var result in results)
            {
                if (result is not (SetResult.Okay or SetResult.OkayWorking))
                {
                    Service.PluginLog.Error($"Could not configure Wrath Combo auto-rotation: {result}.");
                    this.EndControl();
                    return false;
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            Service.PluginLog.Error(ex, "Problem while setting up Wrath Combo.");
            this.EndControl();
            return false;
        }
    }

    /// <summary>
    ///     Release our lease on Wrath Combo, restoring the user's own settings.
    /// </summary>
    public void EndControl()
    {
        try
        {
            if (this.lease.HasValue)
            {
                WrathIPCWrapper.ReleaseControl(this.lease.Value);
            }
        }
        catch (Exception ex)
        {
            Service.PluginLog.Debug(ex, "Problem while releasing Wrath Combo control.");
        }
        finally
        {
            this.lease = null;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        this.EndControl();
        this.callback.UnregisterAction();
    }

    private void OnLeaseCancelled(int reason, string additionalInfo)
    {
        Service.PluginLog.Warning($"Wrath Combo lease cancelled: {(CancellationReason)reason} ({additionalInfo}).");
        this.lease = null;
        this.LeaseCancelled?.Invoke();
    }
}
