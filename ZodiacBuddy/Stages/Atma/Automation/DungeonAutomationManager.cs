using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using ZodiacBuddy.Stages.Atma.Data;
using RelicNote = FFXIVClientStructs.FFXIV.Client.Game.UI.RelicNote;

namespace ZodiacBuddy.Stages.Atma.Automation;

/// <summary>
///     Automates the "Dungeons" step of the Trial of the Braves books: cycles
///     through each incomplete dungeon and runs it unsynced through AutoDuty,
///     advancing to the next one when AutoDuty finishes.
/// </summary>
internal sealed class DungeonAutomationManager : IDisposable
{
    private readonly AutoDutyIpc autoDuty;
    private readonly BookTravelManager bookTravel;
    private readonly HashSet<int> skipped = [];

    private uint startedBookId;
    private BraveBook currentBook;
    private BraveTarget currentDungeon;
    private uint currentTerritoryId;

    private DateTime stateEnteredAt;
    private bool stateEntered;

    /// <summary>
    ///     Initializes a new instance of the <see cref="DungeonAutomationManager" /> class.
    /// </summary>
    /// <param name="bookTravel">Book travel manager, cancelled when this automation starts.</param>
    public DungeonAutomationManager(BookTravelManager bookTravel)
    {
        this.autoDuty = new AutoDutyIpc();
        this.bookTravel = bookTravel;
        Service.Framework.Update += this.OnUpdate;
    }

    /// <summary>
    ///     Gets the current state of the automation.
    /// </summary>
    public DungeonAutomationState State { get; private set; } = DungeonAutomationState.Idle;

    /// <summary>
    ///     Gets a human-readable detail about the current state.
    /// </summary>
    public string StatusDetail { get; private set; } = string.Empty;

    /// <summary>
    ///     Gets the last error message, if any.
    /// </summary>
    public string LastError { get; private set; } = string.Empty;

    /// <summary>
    ///     Gets the book dungeon slot currently being worked on, or -1.
    /// </summary>
    public int CurrentSlot { get; private set; } = -1;

    /// <summary>
    ///     Gets a value indicating whether the automation is currently running.
    /// </summary>
    public bool IsRunning => this.State is not (DungeonAutomationState.Idle or DungeonAutomationState.Completed or DungeonAutomationState.Errored);

    private TimeSpan StateAge => DateTime.UtcNow - this.stateEnteredAt;

    /// <summary>
    ///     Check whether the automation can be started right now.
    /// </summary>
    /// <param name="reason">The reason it cannot be started.</param>
    /// <returns>Whether the automation can be started.</returns>
    public static unsafe bool CanStart(out string reason)
    {
        if (!Service.ClientState.IsLoggedIn)
        {
            reason = "Not logged in.";
            return false;
        }

        var relicNote = RelicNote.Instance();
        if (relicNote == null || relicNote->RelicNoteId == 0)
        {
            reason = "No Trial of the Braves book is active. Equip your Zodiac weapon.";
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
    ///     Start working through the dungeons of the current book.
    /// </summary>
    public unsafe void Start()
    {
        if (this.IsRunning)
        {
            return;
        }

        if (!CanStart(out var reason))
        {
            this.LastError = reason;
            Service.PluginLog.Warning($"[DungeonAutomation] Cannot start: {reason}");
            return;
        }

        this.bookTravel.Cancel("The dungeons automation started.");

        this.startedBookId = RelicNote.Instance()->RelicNoteId;
        this.currentBook = BraveBook.GetValue(this.startedBookId);
        this.LastError = string.Empty;
        this.skipped.Clear();
        Log($"Starting dungeons automation for {this.currentBook.Name}.");
        this.TransitionTo(DungeonAutomationState.SelectNextDungeon);
    }

    /// <summary>
    ///     Stop the automation and the current AutoDuty run.
    /// </summary>
    /// <param name="reason">Reason displayed to the user.</param>
    public void Stop(string reason)
    {
        this.autoDuty.Stop();
        this.State = DungeonAutomationState.Idle;
        this.StatusDetail = string.Empty;
        this.CurrentSlot = -1;
        Log($"Automation stopped: {reason}");
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Service.Framework.Update -= this.OnUpdate;
    }

    private static void Log(string message)
    {
        Service.PluginLog.Information($"[DungeonAutomation] {message}");
    }

    private void OnUpdate(IFramework framework)
    {
        try
        {
            if (!this.IsRunning)
            {
                return;
            }

            if (!Service.ClientState.IsLoggedIn)
            {
                this.Fail("Logged out.");
                return;
            }

            // The RelicNote module is unavailable while zoning, which is expected
            // during an AutoDuty run; only treat a book change as fatal when the
            // book is actually readable.
            if (!Service.Condition[ConditionFlag.BetweenAreas]
                && !Service.Condition[ConditionFlag.BetweenAreas51]
                && AtmaAutomationManager.GetActiveBookId() is var bookId
                && bookId != 0
                && bookId != this.startedBookId)
            {
                this.Fail("The Trial of the Braves book is no longer active. Keep your Zodiac weapon equipped.");
                return;
            }

            switch (this.State)
            {
                case DungeonAutomationState.SelectNextDungeon:
                    this.HandleSelectNextDungeon();
                    break;
                case DungeonAutomationState.StartingDuty:
                    this.HandleStartingDuty();
                    break;
                case DungeonAutomationState.RunningDuty:
                    this.HandleRunningDuty();
                    break;
            }
        }
        catch (Exception ex)
        {
            Service.PluginLog.Error(ex, "Exception during Trial of the Braves dungeons automation.");
            this.Fail("Unexpected error, see /xllog for details.");
        }
    }

    private void HandleSelectNextDungeon()
    {
        this.CurrentSlot = -1;
        for (var i = 0; i < this.currentBook.Dungeons.Length; i++)
        {
            if (!AtmaAutomationManager.IsDungeonComplete(i) && !this.skipped.Contains(i))
            {
                this.CurrentSlot = i;
                break;
            }
        }

        if (this.CurrentSlot < 0)
        {
            this.Finish();
            return;
        }

        this.currentDungeon = this.currentBook.Dungeons[this.CurrentSlot];
        this.currentTerritoryId = this.currentDungeon.DutyTerritoryId != 0
            ? this.currentDungeon.DutyTerritoryId
            : this.currentDungeon.Position.TerritoryType.RowId;

        if (!AutoDutyIpc.IsInstalled)
        {
            this.Fail("AutoDuty is not installed.");
            return;
        }

        // AutoDuty juggles Wrath leases and its own config for a moment after a run
        // ends; probing HasPath during that window can wrongly report no path. Wait
        // until it has been stopped for a beat, then keep retrying the probe for a
        // while so a transient false never skips a dungeon that AutoDuty can run.
        if (!this.autoDuty.IsStopped() || this.StateAge < TimeSpan.FromSeconds(2))
        {
            this.StatusDetail = "Preparing the next dungeon...";
            return;
        }

        if (!this.autoDuty.HasPath(this.currentTerritoryId))
        {
            this.StatusDetail = $"Checking AutoDuty's route to {this.currentDungeon.Name}...";
            if (this.StateAge > TimeSpan.FromSeconds(12))
            {
                this.Skip($"AutoDuty has no path for {this.currentDungeon.Name} ({this.currentDungeon.ZoneName}).");
            }

            return;
        }

        Log($"Next dungeon: {this.currentDungeon.Name} in {this.currentDungeon.ZoneName}.");
        this.TransitionTo(DungeonAutomationState.StartingDuty);
    }

    private void Finish()
    {
        this.State = DungeonAutomationState.Completed;

        if (this.skipped.Count == 0)
        {
            this.StatusDetail = string.Empty;
            Log($"The dungeons page of {this.currentBook.Name} is complete!");
            return;
        }

        var names = string.Join(", ", this.skipped.OrderBy(i => i).Select(i => this.currentBook.Dungeons[i].Name));
        this.StatusDetail = $"Finished. Could not run: {names}. Complete them manually.";
        Log($"Dungeons automation finished, but AutoDuty could not run: {names}.");
    }

    private void HandleStartingDuty()
    {
        this.StatusDetail = $"Starting {this.currentDungeon.Name}...";

        if (!this.stateEntered)
        {
            if (!this.autoDuty.RunUnsynced(this.currentTerritoryId))
            {
                this.Fail($"Could not start an AutoDuty run of {this.currentDungeon.Name}.");
                return;
            }

            Log($"Running {this.currentDungeon.Name} unsynced through AutoDuty.");
            this.stateEntered = true;
            return;
        }

        // Once AutoDuty reports it is busy, the run has begun.
        if (!this.autoDuty.IsStopped())
        {
            this.TransitionTo(DungeonAutomationState.RunningDuty);
            return;
        }

        // AutoDuty could have already cleared it (it was previously complete).
        if (AtmaAutomationManager.IsDungeonComplete(this.CurrentSlot))
        {
            this.TransitionTo(DungeonAutomationState.SelectNextDungeon);
            return;
        }

        if (this.StateAge > TimeSpan.FromSeconds(30))
        {
            this.Skip($"AutoDuty did not start {this.currentDungeon.Name} in time.");
        }
    }

    private void HandleRunningDuty()
    {
        var complete = AtmaAutomationManager.IsDungeonComplete(this.CurrentSlot);
        this.StatusDetail = complete
            ? $"{this.currentDungeon.Name} cleared, waiting for AutoDuty to finish..."
            : $"Running {this.currentDungeon.Name}...";

        // Only advance once AutoDuty has fully stopped, so we never issue the next
        // run while it is still exiting the current duty.
        if (!this.autoDuty.IsStopped())
        {
            return;
        }

        if (complete)
        {
            Log($"{this.currentDungeon.Name} complete!");
            this.TransitionTo(DungeonAutomationState.SelectNextDungeon);
            return;
        }

        this.Skip($"AutoDuty stopped before {this.currentDungeon.Name} was complete.");
    }

    private void Skip(string reason)
    {
        // One dungeon AutoDuty cannot handle should not abort the whole page;
        // remember it, keep going, and report it when finished.
        Service.PluginLog.Warning($"[DungeonAutomation] Skipping {this.currentDungeon.Name}: {reason}");
        this.autoDuty.Stop();
        this.skipped.Add(this.CurrentSlot);
        this.TransitionTo(DungeonAutomationState.SelectNextDungeon);
    }

    private void TransitionTo(DungeonAutomationState state)
    {
        this.State = state;
        this.stateEnteredAt = DateTime.UtcNow;
        this.stateEntered = false;
    }

    private void Fail(string reason)
    {
        this.autoDuty.Stop();
        this.State = DungeonAutomationState.Errored;
        this.StatusDetail = string.Empty;
        this.LastError = reason;
        Service.PluginLog.Warning($"[DungeonAutomation] Stopped: {reason}");
    }
}
