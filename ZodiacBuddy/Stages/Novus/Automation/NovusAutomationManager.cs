using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using System;
using ZodiacBuddy.BonusLight;
using ZodiacBuddy.Stages.Atma.Automation;

namespace ZodiacBuddy.Stages.Novus.Automation;

/// <summary>
///     Automates the Novus light grind: runs the configured light duty unsynced
///     through AutoDuty over and over until the equipped relic holds all 2000
///     points of light.
/// </summary>
internal sealed class NovusAutomationManager : IDisposable
{
    /// <summary>
    ///     Light a Novus relic holds once it is fully imbued.
    /// </summary>
    private const uint FullLight = 2000;

    private readonly AutoDutyIpc autoDuty;

    private int relicSlot;
    private uint relicItemId;
    private string relicName = string.Empty;

    private uint dutyTerritoryId;
    private string dutyName = string.Empty;
    private uint lightAtRunStart;
    private int noProgressRuns;

    private DateTime stateEnteredAt;
    private bool stateEntered;

    /// <summary>
    ///     Initializes a new instance of the <see cref="NovusAutomationManager" /> class.
    /// </summary>
    public NovusAutomationManager()
    {
        this.autoDuty = new AutoDutyIpc();
        Service.Framework.Update += this.OnUpdate;
    }

    /// <summary>
    ///     Gets the current state of the automation.
    /// </summary>
    public NovusAutomationState State { get; private set; } = NovusAutomationState.Idle;

    /// <summary>
    ///     Gets a human-readable detail about the current state.
    /// </summary>
    public string StatusDetail { get; private set; } = string.Empty;

    /// <summary>
    ///     Gets the last error message, if any.
    /// </summary>
    public string LastError { get; private set; } = string.Empty;

    /// <summary>
    ///     Gets a value indicating whether the automation is currently running.
    /// </summary>
    public bool IsRunning => this.State is not (NovusAutomationState.Idle or NovusAutomationState.Completed or NovusAutomationState.Errored);

    private static NovusConfiguration Configuration => Service.Configuration.Novus;

    private TimeSpan StateAge => DateTime.UtcNow - this.stateEnteredAt;

    /// <summary>
    ///     Check whether the automation can be started right now.
    /// </summary>
    /// <param name="reason">The reason it cannot be started.</param>
    /// <returns>Whether the automation can be started.</returns>
    public static bool CanStart(out string reason)
    {
        if (!Service.ClientState.IsLoggedIn
            || Service.ObjectTable.LocalPlayer is null
            || Service.Condition[ConditionFlag.BetweenAreas]
            || Service.Condition[ConditionFlag.BetweenAreas51])
        {
            reason = "Not logged in.";
            return false;
        }

        var slot = TryGetRelicSlot(out _);
        if (slot is null)
        {
            reason = "No Novus relic is equipped.";
            return false;
        }

        if (Util.GetEquippedItem(slot.Value).SpiritbondOrCollectability >= FullLight)
        {
            reason = "The relic already holds all 2000 light.";
            return false;
        }

        if (!AutoDutyIpc.IsInstalled)
        {
            reason = "The AutoDuty plugin is not installed.";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    /// <summary>
    ///     Get the light duty the automation is configured to run.
    /// </summary>
    /// <param name="territoryId">Territory ID of the duty.</param>
    /// <returns>The duty, or null when the configured one is unknown.</returns>
    public static BonusLightDuty? GetConfiguredDuty(out uint territoryId)
    {
        territoryId = Service.Configuration.Novus.AutomationTerritoryId;
        return BonusLightDuty.TryGetValue(territoryId, out var duty) ? duty : null;
    }

    /// <summary>
    ///     Start running the configured light duty until the relic is full.
    /// </summary>
    public void Start()
    {
        if (this.IsRunning)
        {
            return;
        }

        if (!CanStart(out var reason))
        {
            this.LastError = reason;
            Service.PluginLog.Warning($"[NovusAutomation] Cannot start: {reason}");
            return;
        }

        var duty = GetConfiguredDuty(out var territoryId);
        if (duty is null)
        {
            this.LastError = "The configured light duty is unknown. Pick one in the settings.";
            Service.PluginLog.Warning($"[NovusAutomation] {this.LastError}");
            return;
        }

        var slot = TryGetRelicSlot(out var itemId)!.Value;
        this.relicSlot = slot;
        this.relicItemId = itemId;
        this.relicName = NovusRelic.Items[itemId];
        this.dutyTerritoryId = territoryId;
        this.dutyName = duty.DutyName;
        this.LastError = string.Empty;
        this.noProgressRuns = 0;
        Log($"Starting the light automation for {this.relicName} on {this.dutyName} ({this.ReadLight()}/{FullLight}).");
        this.TransitionTo(NovusAutomationState.StartingDuty);
    }

    /// <summary>
    ///     Stop the automation and the current AutoDuty run.
    /// </summary>
    /// <param name="reason">Reason written to the log.</param>
    public void Stop(string reason)
    {
        this.autoDuty.Stop();
        this.State = NovusAutomationState.Idle;
        this.StatusDetail = string.Empty;
        Log($"Automation stopped: {reason}");
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Service.Framework.Update -= this.OnUpdate;
    }

    private static void Log(string message)
    {
        Service.PluginLog.Information($"[NovusAutomation] {message}");
    }

    /// <summary>
    ///     Find the equipment slot holding a Novus relic.
    /// </summary>
    /// <param name="itemId">Item ID of the found relic.</param>
    /// <returns>The slot, or null when no relic is equipped.</returns>
    private static int? TryGetRelicSlot(out uint itemId)
    {
        foreach (var slot in new[] { 0, 1 })
        {
            var item = Util.GetEquippedItem(slot);
            if (NovusRelic.Items.ContainsKey(item.ItemId))
            {
                itemId = item.ItemId;
                return slot;
            }
        }

        itemId = 0;
        return null;
    }

    private void OnUpdate(IFramework framework)
    {
        try
        {
            if (!this.IsRunning)
            {
                return;
            }

            if (!this.CheckGlobalGuards())
            {
                return;
            }

            switch (this.State)
            {
                case NovusAutomationState.StartingDuty:
                    this.HandleStartingDuty();
                    break;
                case NovusAutomationState.RunningDuty:
                    this.HandleRunningDuty();
                    break;
            }
        }
        catch (Exception ex)
        {
            Service.PluginLog.Error(ex, "Exception during Novus light automation.");
            this.Fail("Unexpected error, see /xllog for details.");
        }
    }

    private bool CheckGlobalGuards()
    {
        if (!Service.ClientState.IsLoggedIn)
        {
            this.Fail("Logged out.");
            return false;
        }

        // Zoning in and out of the duty is expected; the inventory is
        // unavailable while it happens.
        if (Service.Condition[ConditionFlag.BetweenAreas] || Service.Condition[ConditionFlag.BetweenAreas51])
        {
            return false;
        }

        if (Service.ObjectTable.LocalPlayer is null)
        {
            return false;
        }

        // The light only accrues to the equipped relic, and AutoDuty recovers
        // from deaths inside the duty on its own.
        if (Util.GetEquippedItem(this.relicSlot).ItemId != this.relicItemId)
        {
            this.Fail($"{this.relicName} is no longer equipped. Keep the relic equipped.");
            return false;
        }

        return true;
    }

    private uint ReadLight()
        => Util.GetEquippedItem(this.relicSlot).SpiritbondOrCollectability;

    /// <summary>
    ///     Decide which duty the next run uses: the richest duty currently
    ///     carrying a light bonus when that is preferred and one is available,
    ///     otherwise the configured duty. Both settings are read fresh so a
    ///     change takes effect on the next run.
    /// </summary>
    private void ResolveDuty()
    {
        uint territoryId;
        string name;

        var bonus = Configuration.PreferBonusLightDuty
            ? LightDutyPicker.FindBonusDuty(this.autoDuty.HasPath)
            : null;

        if (bonus is not null)
        {
            (territoryId, name) = bonus.Value;
        }
        else
        {
            var duty = GetConfiguredDuty(out territoryId);
            if (duty is null)
            {
                // Keep running whatever was resolved last rather than stopping.
                return;
            }

            name = duty.DutyName;
        }

        if (territoryId == this.dutyTerritoryId)
        {
            return;
        }

        Log(bonus is not null
            ? $"Switching to {name}, which currently carries a light bonus."
            : $"Switching to the configured duty {name}.");
        this.dutyTerritoryId = territoryId;
        this.dutyName = name;
    }

    private void HandleStartingDuty()
    {
        var light = this.ReadLight();
        this.StatusDetail = $"Starting {this.dutyName} ({light}/{FullLight})...";

        if (!this.stateEntered)
        {
            // AutoDuty juggles its own config for a moment after a run ends;
            // probing HasPath during that window can wrongly report no path.
            if (!this.autoDuty.IsStopped() || this.StateAge < TimeSpan.FromSeconds(2))
            {
                return;
            }

            // Bonus light windows rotate every two hours, so which duty pays best
            // is decided per run rather than once when the automation started.
            this.ResolveDuty();

            if (!this.autoDuty.HasPath(this.dutyTerritoryId))
            {
                if (this.StateAge > TimeSpan.FromSeconds(12))
                {
                    this.Fail($"AutoDuty has no path for {this.dutyName}. Pick another light duty in the settings.");
                }

                return;
            }

            if (!this.autoDuty.RunUnsynced(this.dutyTerritoryId))
            {
                this.Fail($"Could not start an AutoDuty run of {this.dutyName}.");
                return;
            }

            Log($"Running {this.dutyName} unsynced through AutoDuty ({light}/{FullLight}).");
            this.lightAtRunStart = light;
            this.stateEntered = true;
            return;
        }

        // Once AutoDuty reports it is busy, the run has begun.
        if (!this.autoDuty.IsStopped())
        {
            this.TransitionTo(NovusAutomationState.RunningDuty);
            return;
        }

        if (this.StateAge > TimeSpan.FromSeconds(60))
        {
            this.Fail($"AutoDuty did not start {this.dutyName} in time.");
        }
    }

    private void HandleRunningDuty()
    {
        var light = this.ReadLight();
        this.StatusDetail = $"Running {this.dutyName} ({light}/{FullLight})...";

        // Only move on once AutoDuty has fully stopped, so we never issue the
        // next run while it is still exiting the duty.
        if (!this.autoDuty.IsStopped())
        {
            if (this.StateAge > TimeSpan.FromMinutes(60))
            {
                this.Fail($"The {this.dutyName} run did not finish in time.");
            }

            return;
        }

        if (light >= FullLight)
        {
            this.State = NovusAutomationState.Completed;
            this.StatusDetail = $"{this.relicName} holds all {FullLight} light! " +
                                "Speak with Jalzahn to continue the relic.";
            Log($"{this.relicName} is fully imbued with light!");
            return;
        }

        if (light <= this.lightAtRunStart)
        {
            if (++this.noProgressRuns >= 2)
            {
                this.Fail($"Runs of {this.dutyName} grant no light. Complete the duty once manually to check.");
                return;
            }

            Log($"The run granted no light; retrying ({this.noProgressRuns}/2).");
        }
        else
        {
            this.noProgressRuns = 0;
            Log($"Light is now {light}/{FullLight} (+{light - this.lightAtRunStart}).");
        }

        this.TransitionTo(NovusAutomationState.StartingDuty);
    }

    private void TransitionTo(NovusAutomationState state)
    {
        this.State = state;
        this.stateEnteredAt = DateTime.UtcNow;
        this.stateEntered = false;
    }

    private void Fail(string reason)
    {
        this.autoDuty.Stop();
        this.State = NovusAutomationState.Errored;
        this.StatusDetail = string.Empty;
        this.LastError = reason;
        Service.PluginLog.Warning($"[NovusAutomation] Stopped: {reason}");
    }
}
