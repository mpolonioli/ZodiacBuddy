using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Fates;
using Dalamud.Plugin.Services;
using ECommons.Throttlers;
using Lumina.Excel.Sheets;
using System;
using System.Linq;
using System.Numerics;
using ZodiacBuddy.Stages.Atma.Data;

namespace ZodiacBuddy.Stages.Atma.Automation;

/// <summary>
///     Travels to a Trial of the Braves book target after its entry was clicked:
///     waits for the teleport to land, mounts up and navigates to the target with
///     vnavmesh. FATE targets are only approached while the FATE is active, using
///     its live position. Ported from ZodiacBuddyReborn.
/// </summary>
internal sealed class BookTravelManager : IDisposable
{
    private const float MountDistance = 30f;
    private const uint JumpActionId = 2;
    private const uint MountRouletteActionId = 9;
    private const uint DismountActionId = 23;

    private readonly NavmeshIpc navmesh;
    private readonly AdvancedUnstuck unstuck;

    private TravelState state = TravelState.Idle;
    private BraveTarget target;
    private Vector3 destination;
    private bool fly;
    private bool sawZoning;
    private uint startTerritory;
    private DateTime stateEnteredAt;
    private bool stateEntered;

    private Vector3 lastPosition;
    private DateTime lastMovedAt;
    private int repathAttempts;
    private System.Action? unstuckRecovery;

    /// <summary>
    ///     Initializes a new instance of the <see cref="BookTravelManager" /> class.
    /// </summary>
    /// <param name="unstuck">Shared unstuck helper.</param>
    public BookTravelManager(AdvancedUnstuck unstuck)
    {
        this.navmesh = new NavmeshIpc();
        this.unstuck = unstuck;
        Service.Framework.Update += this.OnUpdate;
    }

    private enum TravelState
    {
        Idle,
        WaitingForArrival,
        WaitingForNavmesh,
        Starting,
        Traveling,
    }

    /// <summary>
    ///     Gets a value indicating whether a travel is in progress.
    /// </summary>
    public bool IsRunning => this.state is not TravelState.Idle;

    private TimeSpan StateAge => DateTime.UtcNow - this.stateEnteredAt;

    /// <summary>
    ///     Start traveling to a book target after its teleport was issued.
    /// </summary>
    /// <param name="bookTarget">The clicked book target.</param>
    public void Start(BraveTarget bookTarget)
    {
        if (!NavmeshIpc.IsInstalled)
        {
            Service.PluginLog.Warning("[BookTravel] vnavmesh is not installed; cannot travel to the target.");
            return;
        }

        if (this.IsRunning)
        {
            this.Cancel("Superseded by a new book target.");
        }

        this.target = bookTarget;
        this.sawZoning = false;
        this.startTerritory = Service.ClientState.TerritoryType;
        this.TransitionTo(TravelState.WaitingForArrival);
        Log($"Waiting for the teleport to {bookTarget.ZoneName}, then traveling to {bookTarget.Name}.");
    }

    /// <summary>
    ///     Cancel the current travel, if any.
    /// </summary>
    /// <param name="reason">Reason written to the log.</param>
    public void Cancel(string reason)
    {
        if (!this.IsRunning)
        {
            return;
        }

        this.navmesh.Stop();
        this.unstuck.Stop();
        this.unstuckRecovery = null;
        this.state = TravelState.Idle;
        Log($"Travel stopped: {reason}");
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Service.Framework.Update -= this.OnUpdate;
    }

    private static void Log(string message)
    {
        Service.PluginLog.Information($"[BookTravel] {message}");
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
                this.Cancel("Logged out.");
                return;
            }

            this.unstuck.Update();
            if (this.unstuck.IsRunning)
            {
                return;
            }

            if (this.unstuckRecovery is not null)
            {
                var recovery = this.unstuckRecovery;
                this.unstuckRecovery = null;
                recovery();
            }

            switch (this.state)
            {
                case TravelState.WaitingForArrival:
                    this.HandleWaitingForArrival();
                    break;
                case TravelState.WaitingForNavmesh:
                    this.HandleWaitingForNavmesh();
                    break;
                case TravelState.Starting:
                    this.HandleStarting();
                    break;
                case TravelState.Traveling:
                    this.HandleTraveling();
                    break;
            }
        }
        catch (Exception ex)
        {
            Service.PluginLog.Error(ex, "Exception during book target travel.");
            this.Cancel("Unexpected error, see /xllog for details.");
        }
    }

    private void HandleWaitingForArrival()
    {
        if (Service.Condition[ConditionFlag.BetweenAreas] || Service.Condition[ConditionFlag.BetweenAreas51])
        {
            this.sawZoning = true;
            return;
        }

        if (this.sawZoning
            && Service.ClientState.TerritoryType == this.target.Position.TerritoryType.RowId
            && Service.ObjectTable.LocalPlayer is not null)
        {
            this.TransitionTo(TravelState.WaitingForNavmesh);
            return;
        }

        if (this.StateAge > TimeSpan.FromSeconds(90))
        {
            this.Cancel("The teleport did not land in time.");
        }
    }

    private void HandleWaitingForNavmesh()
    {
        if (this.navmesh.IsReady)
        {
            this.TransitionTo(TravelState.Starting);
            return;
        }

        if (this.StateAge > TimeSpan.FromSeconds(180))
        {
            this.Cancel("The navmesh did not become ready in time.");
        }
    }

    private void HandleStarting()
    {
        var player = Service.ObjectTable.LocalPlayer;
        if (player is null)
        {
            return;
        }

        if (!this.stateEntered)
        {
            if (!this.TryResolveDestination())
            {
                return;
            }

            this.stateEntered = true;
        }

        var configuration = Service.Configuration.AtmaAutomation;
        this.fly = configuration.UseMount
                   && configuration.UseFlight
                   && AtmaAutomationManager.IsFlightAvailable();
        var wantMount = configuration.UseMount
                        && (this.fly || Vector3.Distance(player.Position, this.destination) > MountDistance);

        if (wantMount && !Service.Condition[ConditionFlag.Mounted] && this.StateAge < TimeSpan.FromSeconds(8))
        {
            if (EzThrottler.Throttle("ZodiacBuddy.BookTravel.Mount", 1000))
            {
                AtmaAutomationManager.TryUseGeneralAction(MountRouletteActionId);
            }

            return;
        }

        this.fly = this.fly && Service.Condition[ConditionFlag.Mounted];
        this.navmesh.SetTolerance(0.5f);
        if (!this.navmesh.PathfindAndMoveTo(this.destination, this.fly))
        {
            this.Cancel("vnavmesh could not start pathfinding.");
            return;
        }

        Log($"Traveling to {this.target.Name}... (fly={this.fly})");
        this.repathAttempts = 0;
        this.ResetStuckDetection(player.Position);
        this.TransitionTo(TravelState.Traveling);
    }

    private void HandleTraveling()
    {
        var player = Service.ObjectTable.LocalPlayer;
        if (player is null)
        {
            return;
        }

        if (Service.Condition[ConditionFlag.InCombat])
        {
            this.navmesh.Stop();
            this.Cancel("Combat interrupted the travel.");
            return;
        }

        if (Vector3.Distance(player.Position, this.destination) <= 5f && !this.navmesh.IsPathRunning)
        {
            if (Service.Condition[ConditionFlag.InFlight] || Service.Condition[ConditionFlag.Mounted])
            {
                if (EzThrottler.Throttle("ZodiacBuddy.BookTravel.Dismount", 500))
                {
                    AtmaAutomationManager.TryUseGeneralAction(DismountActionId);
                }

                return;
            }

            this.state = TravelState.Idle;
            Log($"Arrived at {this.target.Name}.");
            return;
        }

        if (this.CheckStuck(player.Position))
        {
            this.Cancel("Stuck while traveling.");
            return;
        }

        if (this.StateAge > TimeSpan.FromSeconds(300))
        {
            this.Cancel($"Could not reach {this.target.Name} in time.");
        }
    }

    private bool TryResolveDestination()
    {
        if (this.target.FateId != 0)
        {
            var fate = Service.Fates.FirstOrDefault(f => f.FateId == this.target.FateId && f.State != FateState.Preparing);
            if (fate is null)
            {
                this.Cancel($"The FATE \"{this.target.Name}\" is not currently active; staying at the aetheryte.");
                return false;
            }

            this.destination = fate.Position;
            return true;
        }

        var mapLink = this.target.Position;
        var map = Service.DataManager.GetExcelSheet<Map>().GetRow(mapLink.Map.RowId);
        var x = AtmaAutomationManager.MapToWorld(mapLink.XCoord, map.SizeFactor, map.OffsetX);
        var z = AtmaAutomationManager.MapToWorld(mapLink.YCoord, map.SizeFactor, map.OffsetY);

        var floor = this.navmesh.FindNavigablePoint(new Vector3(x, 0, z));
        if (floor is null)
        {
            this.Cancel($"No navigable point found near {this.target.Name}.");
            return false;
        }

        this.destination = floor.Value;
        return true;
    }

    private void TransitionTo(TravelState newState)
    {
        this.state = newState;
        this.stateEnteredAt = DateTime.UtcNow;
        this.stateEntered = false;
    }

    private void ResetStuckDetection(Vector3 position)
    {
        this.lastPosition = position;
        this.lastMovedAt = DateTime.UtcNow;
    }

    private bool CheckStuck(Vector3 position)
    {
        if (Vector3.Distance(position, this.lastPosition) > 1f)
        {
            this.ResetStuckDetection(position);
            return false;
        }

        if (this.navmesh.IsPathfindInProgress)
        {
            this.lastMovedAt = DateTime.UtcNow;
            return false;
        }

        if (DateTime.UtcNow - this.lastMovedAt > TimeSpan.FromSeconds(5))
        {
            if (++this.repathAttempts > 3)
            {
                return true;
            }

            this.ResetStuckDetection(position);
            if (this.repathAttempts == 1)
            {
                // A hop first gets us over small obstacles the path clips through.
                AtmaAutomationManager.TryUseGeneralAction(JumpActionId);
                this.Repath();
            }
            else
            {
                // Physically dislodge the character, then repath.
                this.navmesh.Stop();
                if (this.unstuck.Start())
                {
                    this.unstuckRecovery = this.Repath;
                }
                else
                {
                    this.Repath();
                }
            }
        }

        return false;
    }

    private void Repath()
    {
        this.navmesh.Stop();
        this.navmesh.PathfindAndMoveTo(this.destination, this.fly && Service.Condition[ConditionFlag.Mounted]);
    }
}
