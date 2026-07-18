using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using ECommons.GameFunctions;
using ECommons.Throttlers;
using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using ZodiacBuddy.Stages.Atma.Data;

namespace ZodiacBuddy.Stages.Atma.Automation;

/// <summary>
///     Automates the "Levequests" step of the Trial of the Braves books: for every
///     book levequest that the player has already completed in the field, travels
///     to the issuing levemete and hands it in. Accepting and completing the
///     levequests themselves is left to the player, and only the book's own
///     levequests are ever handed in - other completed levequests in the journal
///     are never touched.
/// </summary>
internal sealed class LeveAutomationManager : IDisposable, IBookAutomation
{
    private const float MountDistance = 30f;
    private const uint MountRouletteActionId = 9;
    private const uint DismountActionId = 23;

    private readonly NavmeshIpc navmesh;
    private readonly WrathComboIpc wrath;
    private readonly AdvancedUnstuck unstuck;
    private readonly BookTravelManager bookTravel;
    private System.Action? unstuckRecovery;

    private uint startedBookId;
    private BraveBook currentBook;
    private BraveTarget currentLeve;
    private Vector3 destination;
    private int handedInCount;
    private DateTime? journalClearedAt;

    private List<uint> aetheryteCandidates = [];
    private int aetheryteIndex;
    private Task<List<Vector3>>? probeTask;
    private bool probingFly;
    private List<Vector3>? plannedPath;
    private bool plannedFly;
    private bool flyDisabledForLeg;

    private DateTime stateEnteredAt;
    private bool stateEntered;
    private LeveAutomationState stateAfterAggro;

    private Vector3 lastPosition;
    private DateTime lastMovedAt;
    private DateTime lastTeleportAt;
    private int teleportAttempts;
    private uint teleportStartTerritory;
    private bool sawZoning;
    private int repathAttempts;

    /// <summary>
    ///     Initializes a new instance of the <see cref="LeveAutomationManager" /> class.
    /// </summary>
    /// <param name="unstuck">Shared unstuck helper.</param>
    /// <param name="bookTravel">Book travel manager, cancelled when this automation starts.</param>
    public LeveAutomationManager(AdvancedUnstuck unstuck, BookTravelManager bookTravel)
    {
        this.navmesh = new NavmeshIpc();
        this.wrath = new WrathComboIpc();
        this.unstuck = unstuck;
        this.bookTravel = bookTravel;
        this.wrath.LeaseCancelled += this.OnLeaseCancelled;
        Service.Framework.Update += this.OnUpdate;
    }

    /// <summary>
    ///     Gets the current state of the automation.
    /// </summary>
    public LeveAutomationState State { get; private set; } = LeveAutomationState.Idle;

    /// <summary>
    ///     Gets a human-readable detail about the current state.
    /// </summary>
    public string StatusDetail { get; private set; } = string.Empty;

    /// <summary>
    ///     Gets the last error message, if any.
    /// </summary>
    public string LastError { get; private set; } = string.Empty;

    /// <summary>
    ///     Gets the book levequest slot currently being worked on, or -1.
    /// </summary>
    public int CurrentSlot { get; private set; } = -1;

    /// <summary>
    ///     Gets a value indicating whether the automation is currently running.
    /// </summary>
    public bool IsRunning => this.State is not (LeveAutomationState.Idle or LeveAutomationState.Completed or LeveAutomationState.Errored);

    /// <inheritdoc />
    public bool IsCompleted => this.State == LeveAutomationState.Completed;

    private TimeSpan StateAge => DateTime.UtcNow - this.stateEnteredAt;

    /// <summary>
    ///     Check whether the automation can be started right now.
    /// </summary>
    /// <param name="reason">The reason it cannot be started.</param>
    /// <returns>Whether the automation can be started.</returns>
    public static bool CanStart(out string reason)
        => CanStart(NavmeshIpc.IsInstalled, WrathComboIpc.IsAvailable(), out reason);

    /// <summary>
    ///     Check whether the automation can be started right now, using already known
    ///     dependency statuses to avoid IPC calls.
    /// </summary>
    /// <param name="navmeshInstalled">Whether vnavmesh is installed.</param>
    /// <param name="wrathAvailable">Whether Wrath Combo is available.</param>
    /// <param name="reason">The reason it cannot be started.</param>
    /// <returns>Whether the automation can be started.</returns>
    internal static bool CanStart(bool navmeshInstalled, bool wrathAvailable, out string reason)
        => AtmaAutomationManager.CanStart(navmeshInstalled, wrathAvailable, out reason);

    /// <summary>
    ///     Get the combined status of a book levequest slot.
    /// </summary>
    /// <param name="leve">The book levequest.</param>
    /// <param name="slot">Levequest slot within the book.</param>
    /// <returns>The status of the slot.</returns>
    internal static LeveSlotStatus GetSlotStatus(in BraveTarget leve, int slot)
    {
        if (AtmaAutomationManager.IsLeveComplete(slot))
        {
            return LeveSlotStatus.HandedIn;
        }

        return LeveGameActions.GetLeveSequence(leve.LeveId) switch
        {
            null => LeveSlotStatus.NotAccepted,
            LeveGameActions.ReadyToHandInSequence => LeveSlotStatus.ReadyToHandIn,
            LeveGameActions.FailedSequence => LeveSlotStatus.Failed,
            _ => LeveSlotStatus.InProgress,
        };
    }

    /// <summary>
    ///     Start handing in the completed levequests of the current book.
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
            Service.PluginLog.Warning($"[LeveAutomation] Cannot start: {reason}");
            return;
        }

        if (!this.wrath.BeginControl())
        {
            this.LastError = "Could not take control of Wrath Combo.";
            Service.PluginLog.Warning($"[LeveAutomation] {this.LastError}");
            return;
        }

        this.bookTravel.Cancel("The levequests automation started.");

        this.startedBookId = AtmaAutomationManager.GetActiveBookId();
        this.currentBook = BraveBook.GetValue(this.startedBookId);
        this.LastError = string.Empty;
        this.handedInCount = 0;
        Log($"Starting levequests automation for {this.currentBook.Name}.");
        this.TransitionTo(LeveAutomationState.SelectNextLeve);
    }

    /// <summary>
    ///     Stop the automation and release all controlled plugins.
    /// </summary>
    /// <param name="reason">Reason displayed to the user.</param>
    public void Stop(string reason)
    {
        this.Cleanup();
        this.State = LeveAutomationState.Idle;
        this.StatusDetail = string.Empty;
        this.CurrentSlot = -1;
        Log($"Automation stopped: {reason}");
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Service.Framework.Update -= this.OnUpdate;
        this.wrath.LeaseCancelled -= this.OnLeaseCancelled;
        this.Cleanup();
        this.wrath.Dispose();
    }

    private static void Log(string message)
    {
        Service.PluginLog.Information($"[LeveAutomation] {message}");
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

            // While an unstuck maneuver overrides movement, pause the state machine;
            // once it finishes, resume path following where we left off.
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

            // The hand-in is done the moment the book counts the levequest.
            if (this.CurrentSlot >= 0
                && this.State is LeveAutomationState.Teleporting
                    or LeveAutomationState.WaitingForNavmesh
                    or LeveAutomationState.ProbingRoute
                    or LeveAutomationState.Traveling
                    or LeveAutomationState.HandingIn
                && AtmaAutomationManager.IsLeveComplete(this.CurrentSlot))
            {
                this.navmesh.Stop();
                this.handedInCount++;
                Log($"{this.currentLeve.Name} handed in!");
                this.TransitionTo(LeveAutomationState.SelectNextLeve);
                return;
            }

            switch (this.State)
            {
                case LeveAutomationState.SelectNextLeve:
                    this.HandleSelectNextLeve();
                    break;
                case LeveAutomationState.Teleporting:
                    this.HandleTeleporting();
                    break;
                case LeveAutomationState.WaitingForNavmesh:
                    this.HandleWaitingForNavmesh();
                    break;
                case LeveAutomationState.ProbingRoute:
                    this.HandleProbingRoute();
                    break;
                case LeveAutomationState.Traveling:
                    this.HandleTraveling();
                    break;
                case LeveAutomationState.HandingIn:
                    this.HandleHandingIn();
                    break;
                case LeveAutomationState.HandlingAggro:
                    this.HandleAggro();
                    break;
            }
        }
        catch (Exception ex)
        {
            Service.PluginLog.Error(ex, "Exception during Trial of the Braves levequests automation.");
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

        // While zoning, the player object and the RelicNote instance are unavailable;
        // don't misread that as being logged out or the book being gone. Only the
        // Teleporting state expects to cross zones, everything else just waits.
        if (Service.Condition[ConditionFlag.BetweenAreas] || Service.Condition[ConditionFlag.BetweenAreas51])
        {
            return this.State == LeveAutomationState.Teleporting;
        }

        var player = Service.ObjectTable.LocalPlayer;
        if (player is null)
        {
            // Transient loading frames; the per-state timeouts cover the case where
            // the player never comes back.
            return false;
        }

        if (player.IsDead || Service.Condition[ConditionFlag.Unconscious])
        {
            this.Fail("You died. Automation stopped.");
            return false;
        }

        if (!this.wrath.HasLease)
        {
            this.Fail("The Wrath Combo lease was revoked.");
            return false;
        }

        if (AtmaAutomationManager.GetActiveBookId() != this.startedBookId)
        {
            this.Fail("The Trial of the Braves book is no longer active. Keep your Zodiac weapon equipped.");
            return false;
        }

        return true;
    }

    private void HandleSelectNextLeve()
    {
        // A hand-in can leave trailing dialogue, the levemete menu or another
        // reward window open; get rid of it before moving on, movement is blocked
        // while a dialog holds the character.
        if (Service.Condition[ConditionFlag.OccupiedInQuestEvent])
        {
            if (this.HandleRewardWindow())
            {
                return;
            }

            if (EzThrottler.Throttle("ZodiacBuddy.LeveAuto.Dialogue", 250))
            {
                FateGameActions.ProgressTalk();
                FateGameActions.ConfirmYesNo();
                LeveGameActions.CloseMenu();
            }

            return;
        }

        this.CurrentSlot = -1;
        for (var i = 0; i < this.currentBook.Leves.Length; i++)
        {
            if (!AtmaAutomationManager.IsLeveComplete(i)
                && LeveGameActions.GetLeveSequence(this.currentBook.Leves[i].LeveId) == LeveGameActions.ReadyToHandInSequence)
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

        this.currentLeve = this.currentBook.Leves[this.CurrentSlot];

        var mapLink = this.currentLeve.Position;
        var map = Service.DataManager.GetExcelSheet<Map>().GetRow(mapLink.Map.RowId);
        var x = AtmaAutomationManager.MapToWorld(mapLink.XCoord, map.SizeFactor, map.OffsetX);
        var z = AtmaAutomationManager.MapToWorld(mapLink.YCoord, map.SizeFactor, map.OffsetY);
        this.destination = new Vector3(x, 0, z);

        Log($"Next hand-in: {this.currentLeve.Name} at {this.currentLeve.Issuer} in {this.currentLeve.ZoneName}.");

        this.flyDisabledForLeg = false;
        this.aetheryteCandidates = AtmaManager.GetAetherytesByDistance(mapLink);

        if (Service.ClientState.TerritoryType == mapLink.TerritoryType.RowId)
        {
            // Already here; if the levemete turns out unreachable from this spot,
            // the aetheryte fallback starts from the closest one.
            this.aetheryteIndex = -1;
            this.TransitionTo(LeveAutomationState.WaitingForNavmesh);
            return;
        }

        if (this.aetheryteCandidates.Count == 0)
        {
            this.Fail($"No aetheryte found in {this.currentLeve.ZoneName}.");
            return;
        }

        this.aetheryteIndex = 0;
        this.TransitionTo(LeveAutomationState.Teleporting);
    }

    private void Finish()
    {
        var remaining = 0;
        for (var i = 0; i < this.currentBook.Leves.Length; i++)
        {
            if (!AtmaAutomationManager.IsLeveComplete(i))
            {
                remaining++;
            }
        }

        this.Cleanup();
        this.State = LeveAutomationState.Completed;
        this.CurrentSlot = -1;
        var message = remaining == 0
            ? $"The levequests page of {this.currentBook.Name} is complete!"
            : this.handedInCount > 0
                ? $"Handed in {this.handedInCount} levequest(s); {remaining} more must be accepted and completed in the field first."
                : $"Nothing to hand in; {remaining} levequest(s) must be accepted and completed in the field first.";
        this.StatusDetail = message;
        Log(message);
    }

    private void HandleTeleporting()
    {
        this.StatusDetail = $"Teleporting to {this.currentLeve.ZoneName}...";

        if (!this.stateEntered)
        {
            // Any residual movement would cancel the teleport cast.
            this.navmesh.Stop();
            this.teleportAttempts = 0;
            this.lastTeleportAt = DateTime.MinValue;
            this.teleportStartTerritory = Service.ClientState.TerritoryType;
            this.sawZoning = false;
            this.stateEntered = true;
        }

        // While the cast bar is up or we are zoning, just wait. Observing the zoning
        // is also what tells intra-zone teleports (to another aetheryte of the same
        // territory) apart from not having teleported at all.
        if (Service.Condition[ConditionFlag.BetweenAreas]
            || Service.Condition[ConditionFlag.BetweenAreas51])
        {
            this.sawZoning = true;
            return;
        }

        if (Service.ClientState.TerritoryType == this.currentLeve.Position.TerritoryType.RowId
            && (this.sawZoning || this.teleportStartTerritory != this.currentLeve.Position.TerritoryType.RowId))
        {
            this.TransitionTo(LeveAutomationState.WaitingForNavmesh);
            return;
        }

        // Something is attacking us; deal with it before we can teleport.
        if (Service.Condition[ConditionFlag.InCombat] && !Service.Condition[ConditionFlag.Casting])
        {
            this.stateAfterAggro = LeveAutomationState.Teleporting;
            this.TransitionTo(LeveAutomationState.HandlingAggro);
            return;
        }

        if (Service.Condition[ConditionFlag.Casting])
        {
            return;
        }

        // Issue the teleport; if we still haven't landed (or started casting/zoning)
        // after a while, the cast silently fizzled - retry it.
        if (DateTime.UtcNow - this.lastTeleportAt > TimeSpan.FromSeconds(15))
        {
            if (++this.teleportAttempts > 5)
            {
                this.Fail($"Teleport to {this.currentLeve.ZoneName} failed repeatedly.");
                return;
            }

            var aetheryteId = this.aetheryteCandidates.ElementAtOrDefault(Math.Max(this.aetheryteIndex, 0));
            if (aetheryteId == 0 || !AtmaManager.ExecuteTeleport(aetheryteId))
            {
                this.Fail($"Could not teleport to {this.currentLeve.ZoneName}.");
                return;
            }

            this.lastTeleportAt = DateTime.UtcNow;
        }
    }

    private void HandleWaitingForNavmesh()
    {
        this.StatusDetail = $"Waiting for navmesh... {this.navmesh.BuildProgressPercent}%";

        if (this.navmesh.IsReady)
        {
            this.TransitionTo(LeveAutomationState.ProbingRoute);
            return;
        }

        if (this.StateAge > TimeSpan.FromSeconds(180))
        {
            this.Fail("The navmesh did not become ready in time.");
        }
    }

    private void HandleProbingRoute()
    {
        this.StatusDetail = $"Checking the route to {this.currentLeve.Issuer}...";
        var player = Service.ObjectTable.LocalPlayer!;

        if (!this.stateEntered)
        {
            var floor = this.navmesh.FindNavigablePoint(this.destination);
            if (floor is null)
            {
                this.NextAetheryteOrFail($"No navigable point found near {this.currentLeve.Issuer}.");
                return;
            }

            this.destination = floor.Value;
            this.probeTask = null;
            this.stateEntered = true;
        }

        // Deal with attackers before planning any travel.
        if (Service.Condition[ConditionFlag.InCombat])
        {
            this.stateAfterAggro = LeveAutomationState.ProbingRoute;
            this.TransitionTo(LeveAutomationState.HandlingAggro);
            return;
        }

        if (this.probeTask is null)
        {
            // Short hops don't need a mount at all.
            var far = Vector3.Distance(player.Position, this.destination) > MountDistance;
            var wantFly = this.WantFly() && far;

            // Flying requires a mount; mount up before probing so the probe mode
            // matches how we will actually travel. Ground travel also mounts for
            // longer legs, purely for speed. The window is generous because the
            // post-zoning action lockout eats into it.
            if ((wantFly || this.ShouldMount(player.Position)) && this.StateAge < TimeSpan.FromSeconds(10))
            {
                // The roulette action is a toggle; pressing it while already
                // mounted would dismount instead.
                if (!Service.Condition[ConditionFlag.Mounted]
                    && EzThrottler.Throttle("ZodiacBuddy.LeveAuto.Mount", 1000))
                {
                    AtmaAutomationManager.TryUseGeneralAction(MountRouletteActionId);
                }

                if (!Service.Condition[ConditionFlag.Mounted])
                {
                    return;
                }
            }

            // Re-evaluate after mounting: the game reports flight as available
            // only in situations that can change with the mount.
            this.probingFly = wantFly && Service.Condition[ConditionFlag.Mounted];
            this.probeTask = this.navmesh.Pathfind(player.Position, this.destination, this.probingFly);
            if (this.probeTask is null)
            {
                this.OnProbeFailed();
            }

            return;
        }

        if (!this.probeTask.IsCompleted)
        {
            if (this.StateAge > TimeSpan.FromSeconds(60))
            {
                this.probeTask = null;
                this.OnProbeFailed();
            }

            return;
        }

        var path = this.probeTask.IsCompletedSuccessfully ? this.probeTask.Result : null;
        this.probeTask = null;

        // vnavmesh returns a partial path to the closest reachable point when the
        // destination is on a disconnected part of the mesh (e.g. across water).
        if (path is null || path.Count == 0 || Vector3.Distance(path[^1], this.destination) > 10f)
        {
            this.OnProbeFailed();
            return;
        }

        this.plannedPath = path;
        this.plannedFly = this.probingFly;
        this.TransitionTo(LeveAutomationState.Traveling);
    }

    private void HandleTraveling()
    {
        this.StatusDetail = $"Traveling to {this.currentLeve.Issuer}...";
        var player = Service.ObjectTable.LocalPlayer!;

        if (!this.stateEntered)
        {
            this.navmesh.SetTolerance(0.5f);
            if (this.plannedPath is not null)
            {
                this.navmesh.MoveTo(this.plannedPath, this.plannedFly);
                this.plannedPath = null;
            }
            else if (!this.navmesh.PathfindAndMoveTo(this.destination, this.plannedFly))
            {
                this.Fail("vnavmesh could not start pathfinding.");
                return;
            }

            this.repathAttempts = 0;
            this.ResetStuckDetection(player.Position);
            this.stateEntered = true;
            return;
        }

        // Fight back anything that aggroed us on the way.
        if (Service.Condition[ConditionFlag.InCombat])
        {
            this.navmesh.Stop();
            this.stateAfterAggro = LeveAutomationState.ProbingRoute;
            this.TransitionTo(LeveAutomationState.HandlingAggro);
            return;
        }

        // A flying path ends hovering above the destination; land first so we
        // arrive on foot when the spot allows it. If it does not, the helper
        // gives up and arrival proceeds airborne - HandingIn walks to the
        // levemete on the ground and dismounts there.
        if (AtmaAutomationManager.LandIfHovering(this.navmesh, "ZodiacBuddy.LeveAuto.Land"))
        {
            return;
        }

        if (Vector3.Distance(player.Position, this.destination) <= 5f
            && !this.navmesh.IsPathRunning)
        {
            this.TransitionTo(LeveAutomationState.HandingIn);
            return;
        }

        if (this.CheckStuck(player.Position, () =>
            {
                this.navmesh.Stop();
                this.navmesh.PathfindAndMoveTo(this.destination, this.plannedFly);
            }))
        {
            if (this.plannedFly)
            {
                // Flying did not work out (e.g. flight not actually available);
                // re-plan this leg on the ground.
                this.flyDisabledForLeg = true;
                this.TransitionTo(LeveAutomationState.ProbingRoute);
            }
            else
            {
                this.NextAetheryteOrFail("Stuck while navigating.");
            }

            return;
        }

        if (this.StateAge > TimeSpan.FromSeconds(300))
        {
            this.Fail($"Could not reach {this.currentLeve.Issuer} in time.");
        }
    }

    private void HandleHandingIn()
    {
        this.StatusDetail = $"Handing in {this.currentLeve.Name}...";

        if (!this.stateEntered)
        {
            this.journalClearedAt = null;
            this.stateEntered = true;
        }

        if (Service.Condition[ConditionFlag.InCombat])
        {
            this.navmesh.Stop();
            this.stateAfterAggro = LeveAutomationState.HandingIn;
            this.TransitionTo(LeveAutomationState.HandlingAggro);
            return;
        }

        // The reward window concludes a hand-in; "Collect Reward." walks through
        // every completed levequest one by one, so this claims the ones the book
        // needs and declines all others.
        if (this.HandleRewardWindow())
        {
            return;
        }

        // Once the reward is claimed the journal entry disappears; normally the
        // book counts it right away (handled globally), this covers the case
        // where it somehow does not.
        if (LeveGameActions.GetLeveSequence(this.currentLeve.LeveId) is null)
        {
            this.journalClearedAt ??= DateTime.UtcNow;
            if (EzThrottler.Throttle("ZodiacBuddy.LeveAuto.Dialogue", 250))
            {
                FateGameActions.ProgressTalk();
            }

            if (DateTime.UtcNow - this.journalClearedAt > TimeSpan.FromSeconds(5))
            {
                Service.PluginLog.Warning(
                    $"[LeveAutomation] {this.currentLeve.Name} left the journal but the book did not count it.");
                this.TransitionTo(LeveAutomationState.SelectNextLeve);
            }

            return;
        }

        // The levemete's menu is up: go for "Collect Reward." (or the levequest
        // itself when listed directly), nothing else.
        if (FateGameActions.IsAddonReady("SelectString"))
        {
            if (EzThrottler.Throttle("ZodiacBuddy.LeveAuto.Menu", 1000)
                && !LeveGameActions.SelectMenuEntry(this.currentLeve.Name))
            {
                this.Fail($"{this.currentLeve.Issuer} offered no reward to collect for \"{this.currentLeve.Name}\"; " +
                          "stopped to avoid selecting the wrong option.");
            }

            return;
        }

        // The hand-in list is up: pick exactly the book's levequest, nothing else.
        if (FateGameActions.IsAddonReady("SelectIconString"))
        {
            if (EzThrottler.Throttle("ZodiacBuddy.LeveAuto.Select", 1000)
                && !LeveGameActions.SelectHandInEntry(this.currentLeve.Name))
            {
                this.Fail($"{this.currentLeve.Issuer} does not offer \"{this.currentLeve.Name}\" for completion; " +
                          "stopped to avoid handing in the wrong levequest.");
            }

            return;
        }

        if (EzThrottler.Throttle("ZodiacBuddy.LeveAuto.Dialogue", 250))
        {
            FateGameActions.ProgressTalk();
            FateGameActions.ConfirmYesNo();
        }

        // The offer window means the levemete has nothing to receive from us;
        // close it so the interaction can be retried (or time out).
        if (LeveGameActions.CloseLeveOfferWindow())
        {
            Service.PluginLog.Warning("[LeveAutomation] The levemete opened the offer window instead of the hand-in list.");
            return;
        }

        var npc = LeveGameActions.GetIssuerNpc(this.currentLeve.IssuerId);
        if (npc is null)
        {
            // The NPC is not in object table range yet; get closer to its position.
            this.StatusDetail = $"Looking for {this.currentLeve.Issuer}...";
            if (!this.navmesh.IsPathRunning && !this.navmesh.IsPathfindInProgress
                && EzThrottler.Throttle("ZodiacBuddy.LeveAuto.NpcApproach", 2000))
            {
                this.navmesh.PathfindAndMoveCloseTo(this.destination, 5f);
            }
        }
        else if (!FateGameActions.IsInInteractRange(npc))
        {
            if (!this.navmesh.IsPathRunning && !this.navmesh.IsPathfindInProgress
                && EzThrottler.Throttle("ZodiacBuddy.LeveAuto.NpcApproach", 1000))
            {
                this.navmesh.PathfindAndMoveCloseTo(npc.Position, 2f);
            }
        }
        else
        {
            this.navmesh.Stop();

            if (Service.Condition[ConditionFlag.Mounted])
            {
                if (EzThrottler.Throttle("ZodiacBuddy.LeveAuto.Dismount", 500))
                {
                    AtmaAutomationManager.TryUseGeneralAction(DismountActionId);
                }

                return;
            }

            if (!Service.Condition[ConditionFlag.OccupiedInQuestEvent]
                && EzThrottler.Throttle("ZodiacBuddy.LeveAuto.Interact", 2000))
            {
                FateGameActions.InteractWith(npc);
            }
        }

        if (this.StateAge > TimeSpan.FromSeconds(120))
        {
            this.Fail($"Could not hand {this.currentLeve.Name} in at {this.currentLeve.Issuer} in time.");
        }
    }

    /// <summary>
    ///     Handle an open reward (JournalResult) window: claim it when it belongs
    ///     to a book levequest the book still needs, decline it otherwise - a
    ///     declined levequest stays completed in the journal and collectable later.
    /// </summary>
    /// <returns>Whether a reward window was open.</returns>
    private bool HandleRewardWindow()
    {
        if (!FateGameActions.IsAddonReady("JournalResult"))
        {
            return false;
        }

        if (!EzThrottler.Throttle("ZodiacBuddy.LeveAuto.TurnIn", 1000))
        {
            return true;
        }

        var text = LeveGameActions.GetJournalResultText();
        var match = text is null ? null : this.FindReadyBookLeve(text);
        if (match is not null)
        {
            Log($"Collecting the reward of {match}.");
            LeveGameActions.CompleteJournalResult();
        }
        else
        {
            Log("Declining the reward window of an unrelated levequest.");
            LeveGameActions.DeclineJournalResult();
        }

        return true;
    }

    private string? FindReadyBookLeve(string text)
    {
        for (var i = 0; i < this.currentBook.Leves.Length; i++)
        {
            var leve = this.currentBook.Leves[i];
            if (!AtmaAutomationManager.IsLeveComplete(i)
                && text.Contains(leve.Name, StringComparison.OrdinalIgnoreCase))
            {
                return leve.Name;
            }
        }

        return null;
    }

    private void HandleAggro()
    {
        this.StatusDetail = "Fighting off attackers...";
        var player = Service.ObjectTable.LocalPlayer!;

        // Friendly NPCs can also "target" the player; only hostiles count as attackers.
        var attacker = Service.ObjectTable.OfType<IBattleNpc>()
            .Where(b => !b.IsDead
                        && b.IsTargetable
                        && b.IsHostile()
                        && b.TargetObjectId == player.GameObjectId)
            .OrderBy(b => Vector3.DistanceSquared(b.Position, player.Position))
            .FirstOrDefault();

        if (attacker is null && !Service.Condition[ConditionFlag.InCombat])
        {
            this.TransitionTo(this.stateAfterAggro);
            return;
        }

        if (attacker is not null)
        {
            // Combat can't be done mounted.
            if (Service.Condition[ConditionFlag.Mounted])
            {
                if (EzThrottler.Throttle("ZodiacBuddy.LeveAuto.Dismount", 500))
                {
                    AtmaAutomationManager.TryUseGeneralAction(DismountActionId);
                }

                return;
            }

            if (Service.TargetManager.Target?.GameObjectId != attacker.GameObjectId)
            {
                Service.TargetManager.Target = attacker;
            }

            var range = AtmaAutomationManager.GetAttackRange(player, attacker);
            if (Vector3.Distance(player.Position, attacker.Position) > range
                && !this.navmesh.IsPathRunning
                && !this.navmesh.IsPathfindInProgress)
            {
                this.navmesh.PathfindAndMoveCloseTo(attacker.Position, Math.Max(range - 0.5f, 1f));
            }
        }

        if (this.StateAge > TimeSpan.FromSeconds(180))
        {
            this.Fail("Could not fight off attackers in time.");
        }
    }

    private void TransitionTo(LeveAutomationState state)
    {
        // Wrath is configured to attack our hard target even out of combat, so drop
        // the target when heading anywhere that isn't a fight to avoid unwanted pulls.
        if (state is not LeveAutomationState.HandlingAggro)
        {
            Service.TargetManager.Target = null;
        }

        this.State = state;
        this.stateEnteredAt = DateTime.UtcNow;
        this.stateEntered = false;
    }

    private bool ShouldMount(Vector3 from)
    {
        // Deliberately no action-status pre-check: right after zoning, actions
        // are briefly locked and the status would wrongly veto mounting. The
        // mount retry loop deals with transient failures instead.
        return Service.Configuration.AtmaAutomation.UseMount
               && !Service.Condition[ConditionFlag.Mounted]
               && !Service.Condition[ConditionFlag.InCombat]
               && Vector3.Distance(from, this.destination) > MountDistance;
    }

    private bool WantFly()
    {
        var configuration = Service.Configuration.AtmaAutomation;
        return configuration.UseMount
               && configuration.UseFlight
               && !this.flyDisabledForLeg
               && AtmaAutomationManager.IsFlightAvailable();
    }

    private void OnProbeFailed()
    {
        if (this.probingFly)
        {
            // The air route failed; try the ground mesh before changing aetheryte.
            this.flyDisabledForLeg = true;
            return;
        }

        this.NextAetheryteOrFail($"{this.currentLeve.Issuer} is not reachable from here.");
    }

    private void NextAetheryteOrFail(string reason)
    {
        this.aetheryteIndex++;
        this.flyDisabledForLeg = false;
        if (this.aetheryteIndex >= this.aetheryteCandidates.Count)
        {
            this.Fail($"{reason} No aetheryte in {this.currentLeve.ZoneName} can reach it.");
            return;
        }

        Log($"{reason} Trying the next aetheryte.");
        this.TransitionTo(LeveAutomationState.Teleporting);
    }

    private void Fail(string reason)
    {
        this.Cleanup();
        this.State = LeveAutomationState.Errored;
        this.StatusDetail = string.Empty;
        this.LastError = reason;
        Service.PluginLog.Warning($"[LeveAutomation] Stopped: {reason}");
    }

    private void Cleanup()
    {
        this.navmesh.Stop();
        this.unstuck.Stop();
        this.unstuckRecovery = null;
        this.wrath.EndControl();
    }

    private void OnLeaseCancelled()
    {
        if (this.IsRunning)
        {
            this.Fail("The Wrath Combo lease was revoked.");
        }
    }

    private void ResetStuckDetection(Vector3 position)
    {
        this.lastPosition = position;
        this.lastMovedAt = DateTime.UtcNow;
    }

    private bool CheckStuck(Vector3 position, System.Action recover)
    {
        if (Vector3.Distance(position, this.lastPosition) > 1f)
        {
            this.ResetStuckDetection(position);
            return false;
        }

        if (this.navmesh.IsPathfindInProgress)
        {
            // Pathfinding takes a moment; don't count it as being stuck.
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
                AtmaAutomationManager.TryUseGeneralAction(2);
                recover();
            }
            else
            {
                // Physically dislodge the character with a movement override
                // before repathing.
                this.navmesh.Stop();
                if (this.unstuck.Start())
                {
                    this.unstuckRecovery = recover;
                }
                else
                {
                    AtmaAutomationManager.TryUseGeneralAction(2);
                    recover();
                }
            }
        }

        return false;
    }
}
