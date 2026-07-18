using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Fates;
using Dalamud.Game.ClientState.Objects.Enums;
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
using LuminaFate = Lumina.Excel.Sheets.Fate;

namespace ZodiacBuddy.Stages.Atma.Automation;

/// <summary>
///     Automates the "FATEs" step of the Trial of the Braves books: teleports to
///     each incomplete FATE's spawn point, waits for it to appear (clearing chained
///     prerequisite FATEs to force-spawn it when possible), starts it by talking to
///     its NPC when needed, level syncs, and fights level-synced with Wrath Combo
///     until the book counts it. Collect FATEs are handed in automatically.
/// </summary>
internal sealed class FateAutomationManager : IDisposable
{
    private const float MountDistance = 30f;
    private const uint MountRouletteActionId = 9;
    private const uint DismountActionId = 23;
    private const byte CollectFateRule = 2;
    private const int HandInBatchSize = 6;

    // Zones cap how many FATEs can be active at once; when the cap is reached the
    // target FATE sits in a hidden queue until other FATEs are cleared. After this
    // long at the spawn point with nothing to do, clear one other FATE to free a
    // slot, then come back and wait again.
    private const int FillerWaitSeconds = 60;

    private readonly NavmeshIpc navmesh;
    private readonly WrathComboIpc wrath;
    private readonly AdvancedUnstuck unstuck;
    private readonly BookTravelManager bookTravel;
    private readonly Dictionary<uint, DateTime> fateBlacklist = [];
    private readonly HashSet<int> scoutedSlots = [];
    private System.Action? unstuckRecovery;

    private uint startedBookId;
    private BraveBook currentBook;
    private BraveTarget currentFate;
    private bool scouting;
    private HashSet<uint> chainedFateIds = [];
    private Vector3 destination;
    private Vector3 travelGoal;
    private bool travelingToFate;
    private bool retriedFateApproach;
    private uint activeFateId;
    private EngagementKind engagementKind;

    private List<uint> aetheryteCandidates = [];
    private int aetheryteIndex;
    private Task<List<Vector3>>? probeTask;
    private bool probingFly;
    private List<Vector3>? plannedPath;
    private bool plannedFly;
    private bool flyDisabledForLeg;

    private DateTime stateEnteredAt;
    private bool stateEntered;
    private FateAutomationState stateAfterAggro;

    private Vector3 lastPosition;
    private DateTime lastMovedAt;
    private DateTime lastTeleportAt;
    private int teleportAttempts;
    private uint teleportStartTerritory;
    private bool sawZoning;
    private int repathAttempts;
    private DateTime lastWaitNoticeAt;

    /// <summary>
    ///     Initializes a new instance of the <see cref="FateAutomationManager" /> class.
    /// </summary>
    /// <param name="unstuck">Shared unstuck helper.</param>
    /// <param name="bookTravel">Book travel manager, cancelled when this automation starts.</param>
    public FateAutomationManager(AdvancedUnstuck unstuck, BookTravelManager bookTravel)
    {
        this.navmesh = new NavmeshIpc();
        this.wrath = new WrathComboIpc();
        this.unstuck = unstuck;
        this.bookTravel = bookTravel;
        this.wrath.LeaseCancelled += this.OnLeaseCancelled;
        Service.Framework.Update += this.OnUpdate;
    }

    private enum EngagementKind
    {
        /// <summary>No FATE is being engaged.</summary>
        None,

        /// <summary>The book's FATE itself.</summary>
        Target,

        /// <summary>A FATE chained to the book's FATE, cleared to force-spawn it.</summary>
        Chain,

        /// <summary>An unrelated FATE, cleared to free a slot in the zone's FATE cap.</summary>
        Filler,
    }

    /// <summary>
    ///     Gets the current state of the automation.
    /// </summary>
    public FateAutomationState State { get; private set; } = FateAutomationState.Idle;

    /// <summary>
    ///     Gets a human-readable detail about the current state.
    /// </summary>
    public string StatusDetail { get; private set; } = string.Empty;

    /// <summary>
    ///     Gets the last error message, if any.
    /// </summary>
    public string LastError { get; private set; } = string.Empty;

    /// <summary>
    ///     Gets the book FATE slot currently being worked on, or -1.
    /// </summary>
    public int CurrentSlot { get; private set; } = -1;

    /// <summary>
    ///     Gets a value indicating whether the automation is currently running.
    /// </summary>
    public bool IsRunning => this.State is not (FateAutomationState.Idle or FateAutomationState.Completed or FateAutomationState.Errored);

    private TimeSpan StateAge => DateTime.UtcNow - this.stateEnteredAt;

    private string TravelGoalName
        => this.travelingToFate
            ? $"\"{this.GetActiveFate()?.Name.ToString() ?? "the FATE"}\""
            : $"{this.currentFate.Name}'s spawn point";

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
    ///     Start working through the FATEs of the current book.
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
            Service.PluginLog.Warning($"[FateAutomation] Cannot start: {reason}");
            return;
        }

        if (!this.wrath.BeginControl())
        {
            this.LastError = "Could not take control of Wrath Combo.";
            Service.PluginLog.Warning($"[FateAutomation] {this.LastError}");
            return;
        }

        this.bookTravel.Cancel("The FATEs automation started.");

        this.startedBookId = AtmaAutomationManager.GetActiveBookId();
        this.currentBook = BraveBook.GetValue(this.startedBookId);
        this.scoutedSlots.Clear();
        this.LastError = string.Empty;
        Log($"Starting FATEs automation for {this.currentBook.Name}.");
        this.TransitionTo(FateAutomationState.SelectNextFate);
    }

    /// <summary>
    ///     Stop the automation and release all controlled plugins.
    /// </summary>
    /// <param name="reason">Reason displayed to the user.</param>
    public void Stop(string reason)
    {
        this.Cleanup();
        this.State = FateAutomationState.Idle;
        this.StatusDetail = string.Empty;
        this.CurrentSlot = -1;
        this.activeFateId = 0;
        this.engagementKind = EngagementKind.None;
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
        Service.PluginLog.Information($"[FateAutomation] {message}");
    }

    private static bool IsCollectFate(uint fateId)
        => Service.DataManager.GetExcelSheet<LuminaFate>().GetRow(fateId).Rule == CollectFateRule;

    private static bool IsInsideFateArea(IFate fate, Vector3 position)
        => Vector3.Distance(position, fate.Position) <= Math.Max(fate.Radius - 5f, 5f);

    /// <summary>
    ///     Collect every FATE connected to the given one through the FATEChain
    ///     column, in either direction, so clearing any of them can move the chain
    ///     toward the target. The column's direction is not documented, so both
    ///     interpretations are followed until a fixpoint is reached.
    /// </summary>
    private static HashSet<uint> ComputeChainedFates(uint fateId)
    {
        var sheet = Service.DataManager.GetExcelSheet<LuminaFate>();
        var related = new HashSet<uint> { fateId };
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var row in sheet)
            {
                if (row.FATEChain == 0)
                {
                    continue;
                }

                if (related.Contains(row.RowId) && related.Add(row.FATEChain))
                {
                    changed = true;
                }

                if (related.Contains(row.FATEChain) && related.Add(row.RowId))
                {
                    changed = true;
                }
            }
        }

        related.Remove(fateId);
        return related;
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

            // The book can count the FATE at any point of the engagement (e.g. it
            // was completed by others right as we arrived); check it everywhere.
            if (this.CurrentSlot >= 0
                && this.engagementKind is EngagementKind.None or EngagementKind.Target
                && this.State is FateAutomationState.ProbingRoute
                    or FateAutomationState.Traveling
                    or FateAutomationState.WaitingForFate
                    or FateAutomationState.TalkingToStartNpc
                    or FateAutomationState.EnteringFate
                    or FateAutomationState.SyncingLevel
                    or FateAutomationState.Fighting
                    or FateAutomationState.HandingIn
                && AtmaAutomationManager.IsFateComplete(this.CurrentSlot))
            {
                this.navmesh.Stop();
                Log($"{this.currentFate.Name} complete!");
                this.OnSlotCredited();
                this.activeFateId = 0;
                this.engagementKind = EngagementKind.None;
                this.stateAfterAggro = FateAutomationState.SelectNextFate;
                this.TransitionTo(FateAutomationState.HandlingAggro);
                return;
            }

            // A chain or filler FATE is only a means to an end: the moment the
            // book's FATE shows up, drop what we're doing and go for it.
            if (this.engagementKind is EngagementKind.Chain or EngagementKind.Filler
                && this.State is FateAutomationState.ProbingRoute
                    or FateAutomationState.Traveling
                    or FateAutomationState.TalkingToStartNpc
                    or FateAutomationState.EnteringFate
                    or FateAutomationState.SyncingLevel
                    or FateAutomationState.Fighting
                    or FateAutomationState.HandingIn
                && EzThrottler.Throttle("ZodiacBuddy.FateAuto.TargetWatch", 2000)
                && this.FindTargetFate() is { } targetFate)
            {
                this.navmesh.Stop();
                Log($"{this.currentFate.Name} appeared; abandoning the current FATE.");
                this.EngageFate(targetFate, EngagementKind.Target);
                return;
            }

            switch (this.State)
            {
                case FateAutomationState.SelectNextFate:
                    this.HandleSelectNextFate();
                    break;
                case FateAutomationState.Teleporting:
                    this.HandleTeleporting();
                    break;
                case FateAutomationState.WaitingForNavmesh:
                    this.HandleWaitingForNavmesh();
                    break;
                case FateAutomationState.ScoutingCheck:
                    this.HandleScoutingCheck();
                    break;
                case FateAutomationState.ProbingRoute:
                    this.HandleProbingRoute();
                    break;
                case FateAutomationState.Traveling:
                    this.HandleTraveling();
                    break;
                case FateAutomationState.WaitingForFate:
                    this.HandleWaitingForFate();
                    break;
                case FateAutomationState.TalkingToStartNpc:
                    this.HandleTalkingToStartNpc();
                    break;
                case FateAutomationState.EnteringFate:
                    this.HandleEnteringFate();
                    break;
                case FateAutomationState.SyncingLevel:
                    this.HandleSyncingLevel();
                    break;
                case FateAutomationState.Fighting:
                    this.HandleFighting();
                    break;
                case FateAutomationState.HandingIn:
                    this.HandleHandingIn();
                    break;
                case FateAutomationState.ResolvingOutcome:
                    this.HandleResolvingOutcome();
                    break;
                case FateAutomationState.HandlingAggro:
                    this.HandleAggro();
                    break;
            }
        }
        catch (Exception ex)
        {
            Service.PluginLog.Error(ex, "Exception during Trial of the Braves FATEs automation.");
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
            return this.State == FateAutomationState.Teleporting;
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

    private void HandleSelectNextFate()
    {
        this.CurrentSlot = -1;
        this.activeFateId = 0;
        this.engagementKind = EngagementKind.None;

        // Initial reconnaissance sweep: FATEs can take up to ~30 minutes to spawn,
        // so before committing to that wait on any single one, visit each incomplete
        // FATE once and complete whichever are already up. Only once every incomplete
        // FATE has been scouted do we settle on the first one still remaining and fall
        // back to the normal wait-for-spawn logic - by then another FATE may well have
        // spawned and been cleared during the sweep.
        var firstIncomplete = -1;
        var nextToScout = -1;
        for (var i = 0; i < this.currentBook.Fates.Length; i++)
        {
            if (AtmaAutomationManager.IsFateComplete(i))
            {
                continue;
            }

            if (firstIncomplete < 0)
            {
                firstIncomplete = i;
            }

            if (nextToScout < 0 && !this.scoutedSlots.Contains(i))
            {
                nextToScout = i;
            }
        }

        if (firstIncomplete < 0)
        {
            this.Cleanup();
            this.State = FateAutomationState.Completed;
            this.StatusDetail = string.Empty;
            Log($"The FATEs page of {this.currentBook.Name} is complete!");
            return;
        }

        this.scouting = nextToScout >= 0;
        this.CurrentSlot = this.scouting ? nextToScout : firstIncomplete;
        this.currentFate = this.currentBook.Fates[this.CurrentSlot];
        this.chainedFateIds = ComputeChainedFates(this.currentFate.FateId);
        if (this.chainedFateIds.Count > 0)
        {
            Log($"{this.currentFate.Name} is part of a FATE chain ({string.Join(", ", this.chainedFateIds)}).");
        }

        var mapLink = this.currentFate.Position;
        var map = Service.DataManager.GetExcelSheet<Map>().GetRow(mapLink.Map.RowId);
        var x = AtmaAutomationManager.MapToWorld(mapLink.XCoord, map.SizeFactor, map.OffsetX);
        var z = AtmaAutomationManager.MapToWorld(mapLink.YCoord, map.SizeFactor, map.OffsetY);
        this.destination = new Vector3(x, 0, z);
        this.travelGoal = this.destination;
        this.travelingToFate = false;

        Log(this.scouting
            ? $"Scouting {this.currentFate.Name} in {this.currentFate.ZoneName}."
            : $"Next FATE: {this.currentFate.Name} in {this.currentFate.ZoneName}.");

        this.flyDisabledForLeg = false;
        this.aetheryteCandidates = AtmaManager.GetAetherytesByDistance(mapLink);

        if (Service.ClientState.TerritoryType == mapLink.TerritoryType.RowId)
        {
            // Already here; if the spawn point turns out unreachable from this spot,
            // the aetheryte fallback starts from the closest one.
            this.aetheryteIndex = -1;
            this.TransitionTo(FateAutomationState.WaitingForNavmesh);
            return;
        }

        if (this.aetheryteCandidates.Count == 0)
        {
            this.Fail($"No aetheryte found in {this.currentFate.ZoneName}.");
            return;
        }

        this.aetheryteIndex = 0;
        this.TransitionTo(FateAutomationState.Teleporting);
    }

    private void HandleTeleporting()
    {
        this.StatusDetail = $"Teleporting to {this.currentFate.ZoneName}...";

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

        if (Service.ClientState.TerritoryType == this.currentFate.Position.TerritoryType.RowId
            && (this.sawZoning || this.teleportStartTerritory != this.currentFate.Position.TerritoryType.RowId))
        {
            this.TransitionTo(FateAutomationState.WaitingForNavmesh);
            return;
        }

        // Something is attacking us; deal with it before we can teleport.
        if (Service.Condition[ConditionFlag.InCombat] && !Service.Condition[ConditionFlag.Casting])
        {
            this.stateAfterAggro = FateAutomationState.Teleporting;
            this.TransitionTo(FateAutomationState.HandlingAggro);
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
                this.Fail($"Teleport to {this.currentFate.ZoneName} failed repeatedly.");
                return;
            }

            var aetheryteId = this.aetheryteCandidates.ElementAtOrDefault(Math.Max(this.aetheryteIndex, 0));
            if (aetheryteId == 0 || !AtmaManager.ExecuteTeleport(aetheryteId))
            {
                this.Fail($"Could not teleport to {this.currentFate.ZoneName}.");
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
            this.TransitionTo(this.scouting
                ? FateAutomationState.ScoutingCheck
                : FateAutomationState.ProbingRoute);
            return;
        }

        if (this.StateAge > TimeSpan.FromSeconds(180))
        {
            this.Fail("The navmesh did not become ready in time.");
        }
    }

    private void HandleScoutingCheck()
    {
        this.StatusDetail = $"Checking whether {this.currentFate.Name} is up...";

        if (Service.Condition[ConditionFlag.InCombat])
        {
            this.navmesh.Stop();
            this.stateAfterAggro = FateAutomationState.ScoutingCheck;
            this.TransitionTo(FateAutomationState.HandlingAggro);
            return;
        }

        // Let the FATE table finish populating after zoning in before deciding.
        if (this.StateAge < TimeSpan.FromSeconds(3))
        {
            return;
        }

        // Already up: commit to it right away, just as the normal flow would. The
        // scouting flag stays set so that if this attempt ends without credit the
        // sweep resumes with the remaining FATEs instead of waiting here.
        if (this.FindTargetFate() is { } fate)
        {
            this.EngageFate(fate, EngagementKind.Target);
            return;
        }

        this.ScoutNext();
    }

    private void OnSlotCredited()
    {
        // A book FATE was just counted. If it finished during the settle phase
        // (the reconnaissance sweep already over), the remaining FATEs may have
        // spawned during the wait, so drop the scouted set to run a fresh sweep
        // before settling again. Completions during the sweep itself keep their
        // progress so the pass finishes checking the FATEs it hasn't reached yet.
        if (!this.scouting)
        {
            this.scoutedSlots.Clear();
        }
    }

    private void ScoutNext()
    {
        // Not up: note it as scouted and move on to the next FATE rather than
        // sinking the usual long wait here. Force-spawning via chained or filler
        // FATEs is left to the settle phase once every FATE has been checked.
        Log($"{this.currentFate.Name} is not up right now; checking the next FATE.");
        this.scoutedSlots.Add(this.CurrentSlot);
        this.TransitionTo(FateAutomationState.SelectNextFate);
    }

    private void HandleProbingRoute()
    {
        this.StatusDetail = $"Checking the route to {this.TravelGoalName}...";
        var player = Service.ObjectTable.LocalPlayer!;

        // The FATE we were heading to can end mid-travel.
        if (this.travelingToFate && this.GetActiveFate() is null)
        {
            this.TransitionTo(FateAutomationState.ResolvingOutcome);
            return;
        }

        if (!this.stateEntered)
        {
            var floor = this.navmesh.FindNavigablePoint(this.travelGoal);
            if (floor is null)
            {
                this.AbandonTravel($"No navigable point found near {this.TravelGoalName}.");
                return;
            }

            this.travelGoal = floor.Value;
            if (!this.travelingToFate)
            {
                // Keep the resolved spawn point; ReturnToSpawn compares against it.
                this.destination = floor.Value;
            }

            this.probeTask = null;
            this.stateEntered = true;
        }

        // Deal with attackers before planning any travel.
        if (Service.Condition[ConditionFlag.InCombat])
        {
            this.stateAfterAggro = FateAutomationState.ProbingRoute;
            this.TransitionTo(FateAutomationState.HandlingAggro);
            return;
        }

        if (this.probeTask is null)
        {
            // Short hops don't need a mount at all.
            var far = Vector3.Distance(player.Position, this.travelGoal) > MountDistance;
            var wantFly = this.WantFly() && far;

            // Flying requires a mount; mount up before probing so the probe mode
            // matches how we will actually travel. Ground travel also mounts for
            // longer legs, purely for speed. The window is generous because the
            // post-zoning action lockout eats into it.
            if ((wantFly || this.ShouldMount(player.Position, this.travelGoal)) && this.StateAge < TimeSpan.FromSeconds(10))
            {
                // The roulette action is a toggle; pressing it while already
                // mounted would dismount instead.
                if (!Service.Condition[ConditionFlag.Mounted]
                    && EzThrottler.Throttle("ZodiacBuddy.FateAuto.Mount", 1000))
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
            this.probeTask = this.navmesh.Pathfind(player.Position, this.travelGoal, this.probingFly);
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
        if (path is null || path.Count == 0 || Vector3.Distance(path[^1], this.travelGoal) > 10f)
        {
            this.OnProbeFailed();
            return;
        }

        this.plannedPath = path;
        this.plannedFly = this.probingFly;
        this.TransitionTo(FateAutomationState.Traveling);
    }

    private void HandleTraveling()
    {
        this.StatusDetail = $"Traveling to {this.TravelGoalName}...";
        var player = Service.ObjectTable.LocalPlayer!;

        // The FATE we were heading to can end mid-travel.
        var travelFate = this.travelingToFate ? this.GetActiveFate() : null;
        if (this.travelingToFate && travelFate is null)
        {
            this.navmesh.Stop();
            this.TransitionTo(FateAutomationState.ResolvingOutcome);
            return;
        }

        if (!this.stateEntered)
        {
            this.navmesh.SetTolerance(0.5f);
            if (this.plannedPath is not null)
            {
                this.navmesh.MoveTo(this.plannedPath, this.plannedFly);
                this.plannedPath = null;
            }
            else if (!this.navmesh.PathfindAndMoveTo(this.travelGoal, this.plannedFly))
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
            this.stateAfterAggro = FateAutomationState.ProbingRoute;
            this.TransitionTo(FateAutomationState.HandlingAggro);
            return;
        }

        // On the way to the spawn point: if the FATE (or a chained one) is already
        // up, cut travel short - but not while airborne, ground pathing can't
        // start from up there.
        if (!this.travelingToFate
            && !Service.Condition[ConditionFlag.InFlight]
            && EzThrottler.Throttle("ZodiacBuddy.FateAuto.EarlyEngage", 1000)
            && this.TryFindEngagableFate(out var fate, out var kind))
        {
            this.navmesh.Stop();
            this.EngageFate(fate, kind);
            return;
        }

        // A flying path ends hovering above the destination; land first so we
        // arrive on foot when the spot allows it. If it does not (a rock or
        // small object), the helper gives up and arrival proceeds airborne -
        // TalkingToStartNpc/EnteringFate/WaitingForFate all descend at the FATE
        // on their own.
        if (AtmaAutomationManager.LandIfHovering(this.navmesh, "ZodiacBuddy.FateAuto.Land"))
        {
            return;
        }

        // Arrival.
        if (this.travelingToFate
            && (IsInsideFateArea(travelFate!, player.Position)
                || (Vector3.Distance(player.Position, this.travelGoal) <= 10f && !this.navmesh.IsPathRunning)))
        {
            this.navmesh.Stop();
            this.TransitionTo(travelFate!.State == FateState.Preparing
                ? FateAutomationState.TalkingToStartNpc
                : FateAutomationState.EnteringFate);
            return;
        }

        if (!this.travelingToFate
            && Vector3.Distance(player.Position, this.travelGoal) <= 5f
            && !this.navmesh.IsPathRunning)
        {
            this.TransitionTo(FateAutomationState.WaitingForFate);
            return;
        }

        if (this.CheckStuck(player.Position, () =>
            {
                this.navmesh.Stop();
                this.navmesh.PathfindAndMoveTo(this.travelGoal, this.plannedFly);
            }))
        {
            if (this.plannedFly)
            {
                // Flying did not work out (e.g. flight not actually available);
                // re-plan this leg on the ground.
                this.flyDisabledForLeg = true;
                this.TransitionTo(FateAutomationState.ProbingRoute);
            }
            else
            {
                this.AbandonTravel("Stuck while navigating.");
            }

            return;
        }

        if (this.StateAge > TimeSpan.FromSeconds(300))
        {
            if (this.travelingToFate)
            {
                this.AbandonTravel($"Could not reach {this.TravelGoalName} in time.");
            }
            else
            {
                this.Fail($"Could not reach {this.TravelGoalName} in time.");
            }
        }
    }

    private void HandleWaitingForFate()
    {
        this.StatusDetail = $"Waiting for {this.currentFate.Name} to appear...";

        if (!this.stateEntered)
        {
            this.lastWaitNoticeAt = DateTime.UtcNow;
            this.stateEntered = true;
        }

        if (Service.Condition[ConditionFlag.InCombat])
        {
            this.navmesh.Stop();
            this.stateAfterAggro = FateAutomationState.WaitingForFate;
            this.TransitionTo(FateAutomationState.HandlingAggro);
            return;
        }

        // Reached during the reconnaissance sweep only when an engaged FATE ended
        // without credit and we walked back to its spawn point. Re-check whether it
        // is up, and if not resume the sweep instead of waiting the full duration.
        if (this.scouting)
        {
            if (this.StateAge < TimeSpan.FromSeconds(3))
            {
                return;
            }

            if (this.FindTargetFate() is { } scoutFate)
            {
                this.EngageFate(scoutFate, EngagementKind.Target);
                return;
            }

            this.ScoutNext();
            return;
        }

        if (EzThrottler.Throttle("ZodiacBuddy.FateAuto.Scan", 1000)
            && this.TryFindEngagableFate(out var fate, out var kind))
        {
            this.EngageFate(fate, kind);
            return;
        }

        // The zone caps how many FATEs can be active at once, and a capped-out
        // target FATE waits in a hidden queue. After a minute of nothing to do,
        // clear one other FATE to free a slot, then come back and wait again.
        if (this.StateAge > TimeSpan.FromSeconds(FillerWaitSeconds)
            && EzThrottler.Throttle("ZodiacBuddy.FateAuto.FillerScan", 2000)
            && this.TryFindFillerFate(out var filler))
        {
            this.EngageFate(filler, EngagementKind.Filler);
            return;
        }

        if (DateTime.UtcNow - this.lastWaitNoticeAt > TimeSpan.FromSeconds(60))
        {
            this.lastWaitNoticeAt = DateTime.UtcNow;
            Log($"Still waiting for {this.currentFate.Name} to spawn... FATEs rotate over time, this can take a while.");
        }
    }

    private void HandleTalkingToStartNpc()
    {
        var fate = this.GetActiveFate();
        if (fate is null)
        {
            this.ReturnToWaiting("The FATE disappeared before it could be started.");
            return;
        }

        if (fate.State != FateState.Preparing)
        {
            this.TransitionTo(FateAutomationState.EnteringFate);
            return;
        }

        this.StatusDetail = $"Starting {fate.Name}...";

        if (Service.Condition[ConditionFlag.InCombat])
        {
            this.navmesh.Stop();
            this.stateAfterAggro = FateAutomationState.TalkingToStartNpc;
            this.TransitionTo(FateAutomationState.HandlingAggro);
            return;
        }

        var npc = FateGameActions.GetMotivationNpc(this.activeFateId);
        if (npc is null)
        {
            // The NPC is not in object table range yet; get closer to the FATE.
            this.StatusDetail = $"Looking for the NPC that starts {fate.Name}...";
            if (!this.navmesh.IsPathRunning && !this.navmesh.IsPathfindInProgress
                && EzThrottler.Throttle("ZodiacBuddy.FateAuto.NpcApproach", 2000))
            {
                this.navmesh.PathfindAndMoveCloseTo(fate.Position, 30f);
            }
        }
        else if (!FateGameActions.IsInInteractRange(npc))
        {
            if (!this.navmesh.IsPathRunning && !this.navmesh.IsPathfindInProgress
                && EzThrottler.Throttle("ZodiacBuddy.FateAuto.NpcApproach", 1000))
            {
                this.navmesh.PathfindAndMoveCloseTo(npc.Position, 2f);
            }
        }
        else
        {
            this.navmesh.Stop();

            if (Service.Condition[ConditionFlag.Mounted])
            {
                if (EzThrottler.Throttle("ZodiacBuddy.FateAuto.Dismount", 500))
                {
                    AtmaAutomationManager.TryUseGeneralAction(DismountActionId);
                }

                return;
            }

            // Click the NPC, then click through its dialogue until the FATE starts.
            if (EzThrottler.Throttle("ZodiacBuddy.FateAuto.Interact", 2000))
            {
                FateGameActions.InteractWith(npc);
            }

            if (EzThrottler.Throttle("ZodiacBuddy.FateAuto.Dialogue", 250))
            {
                FateGameActions.ProgressTalk();
                FateGameActions.ConfirmYesNo();
            }
        }

        if (this.StateAge > TimeSpan.FromSeconds(120))
        {
            this.ReturnToWaiting($"Could not start {fate.Name} by talking to its NPC in time.");
        }
    }

    private void HandleEnteringFate()
    {
        var fate = this.GetActiveFate();
        if (fate is null)
        {
            this.TransitionTo(FateAutomationState.ResolvingOutcome);
            return;
        }

        this.StatusDetail = $"Entering {fate.Name}...";
        var player = Service.ObjectTable.LocalPlayer!;

        // Only count as inside once back on the ground (the landing step below
        // gets us there); dismounting mid-air would drop the character into
        // the FATE.
        if (IsInsideFateArea(fate, player.Position) && !Service.Condition[ConditionFlag.InFlight])
        {
            this.navmesh.Stop();

            // Get off the mount; syncing and fighting are done on foot.
            if (Service.Condition[ConditionFlag.Mounted])
            {
                if (EzThrottler.Throttle("ZodiacBuddy.FateAuto.Dismount", 500))
                {
                    AtmaAutomationManager.TryUseGeneralAction(DismountActionId);
                }

                return;
            }

            this.TransitionTo(FateAutomationState.SyncingLevel);
            return;
        }

        // The long approach is done by the probe/travel states; this state only
        // covers the last stretch, so no mount is needed here.
        if (!this.stateEntered)
        {
            var target = this.navmesh.FindNavigablePoint(fate.Position) ?? fate.Position;
            this.navmesh.SetTolerance(0.5f);
            if (!this.navmesh.PathfindAndMoveTo(target, Service.Condition[ConditionFlag.InFlight]))
            {
                this.Fail("vnavmesh could not start pathfinding to the FATE.");
                return;
            }

            this.repathAttempts = 0;
            this.ResetStuckDetection(player.Position);
            this.stateEntered = true;
            return;
        }

        if (Service.Condition[ConditionFlag.InCombat])
        {
            this.navmesh.Stop();
            this.stateAfterAggro = FateAutomationState.EnteringFate;
            this.TransitionTo(FateAutomationState.HandlingAggro);
            return;
        }

        // A flying approach ends hovering at the FATE's edge; land so the
        // inside-the-FATE check above can pass and the dismount happens on
        // the ground.
        if (AtmaAutomationManager.LandIfHovering(this.navmesh, "ZodiacBuddy.FateAuto.Land"))
        {
            return;
        }

        if (this.CheckStuck(player.Position, () =>
            {
                this.navmesh.Stop();
                var target = this.navmesh.FindNavigablePoint(fate.Position) ?? fate.Position;
                this.navmesh.PathfindAndMoveTo(target, false);
            }))
        {
            // Retry through the full travel machinery (mount, fly, aetheryte or
            // filler-skip fallback) instead of giving up on the spot.
            this.AbandonTravelOrRetry($"Stuck while entering {fate.Name}.");
            return;
        }

        if (this.StateAge > TimeSpan.FromSeconds(300))
        {
            this.AbandonTravel($"Could not reach \"{fate.Name}\" in time.");
        }
    }

    private void HandleSyncingLevel()
    {
        var fate = this.GetActiveFate();
        if (fate is null)
        {
            this.TransitionTo(FateAutomationState.ResolvingOutcome);
            return;
        }

        var player = Service.ObjectTable.LocalPlayer!;
        if (player.Level <= fate.MaxLevel || FateGameActions.IsSyncedTo(this.activeFateId))
        {
            this.TransitionTo(FateAutomationState.Fighting);
            return;
        }

        this.StatusDetail = $"Applying level sync for {fate.Name}...";

        if (Service.Condition[ConditionFlag.Mounted])
        {
            if (EzThrottler.Throttle("ZodiacBuddy.FateAuto.Dismount", 500))
            {
                AtmaAutomationManager.TryUseGeneralAction(DismountActionId);
            }

            return;
        }

        // Syncing only works while the game considers us inside the FATE.
        if (!FateGameActions.IsInsideFate(this.activeFateId))
        {
            this.TransitionTo(FateAutomationState.EnteringFate);
            return;
        }

        if (EzThrottler.Throttle("ZodiacBuddy.FateAuto.LevelSync", 1000))
        {
            FateGameActions.LevelSync();
        }

        if (this.StateAge > TimeSpan.FromSeconds(20))
        {
            Service.PluginLog.Warning("[FateAutomation] Could not apply level sync; fighting anyway.");
            this.TransitionTo(FateAutomationState.Fighting);
        }
    }

    private void HandleFighting()
    {
        var fate = this.GetActiveFate();
        if (fate is null)
        {
            this.navmesh.Stop();
            this.TransitionTo(FateAutomationState.ResolvingOutcome);
            return;
        }

        var player = Service.ObjectTable.LocalPlayer!;
        var isCollect = IsCollectFate(this.activeFateId);
        this.StatusDetail = $"Fighting in {fate.Name} ({fate.Progress}%).";

        // A gather cast (collectables) or other interaction is running; let it finish.
        if (Service.Condition[ConditionFlag.OccupiedInQuestEvent] || Service.Condition[ConditionFlag.Casting])
        {
            return;
        }

        if (Service.Condition[ConditionFlag.Mounted])
        {
            if (EzThrottler.Throttle("ZodiacBuddy.FateAuto.Dismount", 500))
            {
                AtmaAutomationManager.TryUseGeneralAction(DismountActionId);
            }

            return;
        }

        // Combat dragged us out of the FATE; head back in so the sync and the
        // contribution keep counting.
        if (Vector3.Distance(player.Position, fate.Position) > fate.Radius + 10f)
        {
            this.TransitionTo(FateAutomationState.EnteringFate);
            return;
        }

        if (isCollect
            && (fate.HandInCount >= HandInBatchSize || (fate.HandInCount > 0 && fate.Progress >= 100)))
        {
            this.navmesh.Stop();
            this.TransitionTo(FateAutomationState.HandingIn);
            return;
        }

        if (fate.Progress >= 100)
        {
            // Done; hold on until the FATE actually ends and the book updates.
            this.StatusDetail = $"{fate.Name} is complete, waiting for it to end...";
            Service.TargetManager.Target = null;
            this.navmesh.Stop();
            return;
        }

        var mob = this.FindFateMob(player);
        var collectable = isCollect && fate.HandInCount < HandInBatchSize ? this.FindFateCollectable(player) : null;

        // Mobs actively attacking us take priority; otherwise collectables first.
        if (mob is not null && (mob.TargetObjectId == player.GameObjectId || collectable is null))
        {
            this.FightMob(player, mob);
            return;
        }

        if (collectable is not null)
        {
            this.GatherCollectable(collectable);
            return;
        }

        // Nothing to fight or gather right now.
        Service.TargetManager.Target = null;
        if (isCollect && fate.HandInCount > 0)
        {
            this.TransitionTo(FateAutomationState.HandingIn);
            return;
        }

        this.StatusDetail = $"Waiting for enemies in {fate.Name} ({fate.Progress}%)...";
        if (!this.navmesh.IsPathRunning && !this.navmesh.IsPathfindInProgress
            && Vector3.Distance(player.Position, fate.Position) > Math.Max(fate.Radius * 0.5f, 10f)
            && EzThrottler.Throttle("ZodiacBuddy.FateAuto.Recenter", 2000))
        {
            this.navmesh.PathfindAndMoveCloseTo(fate.Position, 5f);
        }
    }

    private void FightMob(Dalamud.Game.ClientState.Objects.SubKinds.IPlayerCharacter player, IBattleNpc mob)
    {
        // Keep the mob hard-targeted so Wrath attacks it.
        if (Service.TargetManager.Target?.GameObjectId != mob.GameObjectId)
        {
            Service.TargetManager.Target = mob;
        }

        var range = AtmaAutomationManager.GetAttackRange(player, mob);
        if (Vector3.Distance(player.Position, mob.Position) <= range)
        {
            if (this.navmesh.IsPathRunning)
            {
                this.navmesh.Stop();
            }

            return;
        }

        if (EzThrottler.Throttle("ZodiacBuddy.FateAuto.Chase", 1000))
        {
            this.navmesh.Stop();
            this.navmesh.PathfindAndMoveCloseTo(mob.Position, Math.Max(range - 0.5f, 1f));
        }
    }

    private void GatherCollectable(IGameObject collectable)
    {
        this.StatusDetail = "Picking up FATE collectables...";
        Service.TargetManager.Target = null;

        if (!FateGameActions.IsInInteractRange(collectable))
        {
            if (!this.navmesh.IsPathRunning && !this.navmesh.IsPathfindInProgress
                && EzThrottler.Throttle("ZodiacBuddy.FateAuto.CollectApproach", 1000))
            {
                this.navmesh.PathfindAndMoveCloseTo(collectable.Position, 2f);
            }

            return;
        }

        this.navmesh.Stop();
        if (EzThrottler.Throttle("ZodiacBuddy.FateAuto.Interact", 2000))
        {
            FateGameActions.InteractWith(collectable);
        }
    }

    private void HandleHandingIn()
    {
        var fate = this.GetActiveFate();
        if (fate is null)
        {
            this.TransitionTo(FateAutomationState.ResolvingOutcome);
            return;
        }

        this.StatusDetail = $"Handing in collectables ({fate.HandInCount} held)...";

        if (Service.Condition[ConditionFlag.InCombat])
        {
            this.stateAfterAggro = FateAutomationState.HandingIn;
            this.TransitionTo(FateAutomationState.HandlingAggro);
            return;
        }

        // The hand-over window is up: fill it and confirm.
        if (FateGameActions.IsAddonReady("Request"))
        {
            if (EzThrottler.Throttle("ZodiacBuddy.FateAuto.TurnIn", 1000))
            {
                FateGameActions.TurnInRequests();
            }

            return;
        }

        if (EzThrottler.Throttle("ZodiacBuddy.FateAuto.Dialogue", 250))
        {
            FateGameActions.ProgressTalk();
            FateGameActions.ConfirmYesNo();
        }

        if (fate.HandInCount == 0)
        {
            this.TransitionTo(FateAutomationState.Fighting);
            return;
        }

        var npc = FateGameActions.GetObjectiveNpc(this.activeFateId) ?? FateGameActions.GetMotivationNpc(this.activeFateId);
        if (npc is null)
        {
            // The collector should be near the FATE's center.
            if (!this.navmesh.IsPathRunning && !this.navmesh.IsPathfindInProgress
                && EzThrottler.Throttle("ZodiacBuddy.FateAuto.NpcApproach", 2000))
            {
                this.navmesh.PathfindAndMoveCloseTo(fate.Position, 10f);
            }
        }
        else if (!FateGameActions.IsInInteractRange(npc))
        {
            if (!this.navmesh.IsPathRunning && !this.navmesh.IsPathfindInProgress
                && EzThrottler.Throttle("ZodiacBuddy.FateAuto.NpcApproach", 1000))
            {
                this.navmesh.PathfindAndMoveCloseTo(npc.Position, 2f);
            }
        }
        else
        {
            this.navmesh.Stop();
            if (EzThrottler.Throttle("ZodiacBuddy.FateAuto.Interact", 2500))
            {
                FateGameActions.InteractWith(npc);
            }
        }

        if (this.StateAge > TimeSpan.FromSeconds(90))
        {
            Service.PluginLog.Warning("[FateAutomation] Hand-in took too long; going back to fighting.");
            this.TransitionTo(FateAutomationState.Fighting);
        }
    }

    private void HandleResolvingOutcome()
    {
        if (!this.stateEntered)
        {
            this.navmesh.Stop();
            Service.TargetManager.Target = null;
            this.stateEntered = true;
        }

        this.StatusDetail = "Waiting for the FATE result...";

        if (Service.Condition[ConditionFlag.InCombat])
        {
            this.stateAfterAggro = FateAutomationState.ResolvingOutcome;
            this.TransitionTo(FateAutomationState.HandlingAggro);
            return;
        }

        // Give the book a moment to register the completion after the FATE ends.
        if (this.StateAge < TimeSpan.FromSeconds(3))
        {
            return;
        }

        var kind = this.engagementKind;
        this.activeFateId = 0;
        this.engagementKind = EngagementKind.None;

        // The target could have been completed by other players even while we
        // were busy with a chain or filler FATE; the book is authoritative.
        if (AtmaAutomationManager.IsFateComplete(this.CurrentSlot))
        {
            Log($"{this.currentFate.Name} complete!");
            this.OnSlotCredited();
            this.TransitionTo(FateAutomationState.SelectNextFate);
            return;
        }

        switch (kind)
        {
            case EngagementKind.Chain:
                Log($"Chained FATE finished; watching for {this.currentFate.Name}.");
                break;
            case EngagementKind.Filler:
                Log($"Filler FATE finished; returning to {this.currentFate.Name}'s spawn point.");
                break;
            default:
                Log($"{this.currentFate.Name} ended without credit; waiting for it to spawn again.");
                break;
        }

        this.ReturnToSpawn();
    }

    private void ReturnToSpawn()
    {
        // Clearing a FATE can have taken us anywhere in the zone; travel back to
        // the target's spawn point before resuming the wait.
        this.travelGoal = this.destination;
        this.travelingToFate = false;

        var player = Service.ObjectTable.LocalPlayer;
        if (player is not null && Vector3.Distance(player.Position, this.destination) > 20f)
        {
            this.flyDisabledForLeg = false;
            this.TransitionTo(FateAutomationState.ProbingRoute);
            return;
        }

        this.TransitionTo(FateAutomationState.WaitingForFate);
    }

    private void HandleAggro()
    {
        this.StatusDetail = "Fighting off attackers...";
        var player = Service.ObjectTable.LocalPlayer!;

        var attacker = AtmaAutomationManager.FindLingeringAttacker(player);

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
                if (EzThrottler.Throttle("ZodiacBuddy.FateAuto.Dismount", 500))
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

    private IFate? FindTargetFate()
    {
        // Preparing means we can (try to) start it, Running means we can join it.
        return Service.Fates.FirstOrDefault(f => f.FateId == this.currentFate.FateId
                                                 && f.State is FateState.Preparing or FateState.Running);
    }

    private bool TryFindEngagableFate(out IFate fate, out EngagementKind kind)
    {
        // The book's FATE always takes priority, in any state.
        var target = this.FindTargetFate();
        if (target is not null)
        {
            fate = target;
            kind = EngagementKind.Target;
            return true;
        }

        if (this.chainedFateIds.Count > 0)
        {
            var chained = Service.Fates
                .Where(f => this.chainedFateIds.Contains(f.FateId)
                            && !this.IsFateBlacklisted(f.FateId)
                            && f.State is FateState.Preparing or FateState.Running)
                .OrderBy(f => f.Progress)
                .FirstOrDefault();
            if (chained is not null)
            {
                fate = chained;
                kind = EngagementKind.Chain;
                return true;
            }
        }

        fate = null!;
        kind = EngagementKind.None;
        return false;
    }

    private bool TryFindFillerFate(out IFate fate)
    {
        var player = Service.ObjectTable.LocalPlayer!;

        // Only join FATEs that are actually running and won't expire before we
        // get there. The most progressed one frees its slot the fastest.
        fate = Service.Fates
            .Where(f => f.FateId != this.currentFate.FateId
                        && !this.chainedFateIds.Contains(f.FateId)
                        && !this.IsFateBlacklisted(f.FateId)
                        && f.State == FateState.Running
                        && f.Progress < 100
                        && f.TimeRemaining > 60)
            .OrderByDescending(f => f.Progress)
            .ThenBy(f => Vector3.DistanceSquared(f.Position, player.Position))
            .FirstOrDefault()!;
        return fate is not null;
    }

    private void EngageFate(IFate fate, EngagementKind kind)
    {
        this.activeFateId = fate.FateId;
        this.engagementKind = kind;
        var label = kind switch
        {
            EngagementKind.Chain => "chained FATE",
            EngagementKind.Filler => "filler FATE (to free a FATE slot in the zone)",
            _ => "FATE",
        };
        Log($"Engaging {label} \"{fate.Name}\" ({fate.State}, {fate.Progress}%).");

        // FATEs can be far apart; go through the full probe/travel machinery
        // (mount, fly, ground fallback) to get there, then start or enter it.
        this.travelGoal = fate.Position;
        this.travelingToFate = true;
        this.retriedFateApproach = false;
        this.flyDisabledForLeg = false;
        this.TransitionTo(FateAutomationState.ProbingRoute);
    }

    private IFate? GetActiveFate()
        => Service.Fates.FirstOrDefault(f => f.FateId == this.activeFateId);

    private IBattleNpc? FindFateMob(Dalamud.Game.ClientState.Objects.SubKinds.IPlayerCharacter player)
    {
        // FATEs like the Tidegate breaches field allied battle NPCs alongside the
        // enemies; only ever pick hostile ones. The nameplate kind is the game's
        // own red-vs-green distinction (StatusFlags.Hostile is not reliable here).
        return Service.ObjectTable.OfType<IBattleNpc>()
            .Where(b => !b.IsDead
                        && b.IsTargetable
                        && b.BattleNpcKind == BattleNpcSubKind.Combatant
                        && b.IsHostile()
                        && FateGameActions.GetObjectFateId(b) == this.activeFateId)
            .OrderByDescending(b => b.TargetObjectId == player.GameObjectId)
            .ThenBy(b => Vector3.DistanceSquared(b.Position, player.Position))
            .FirstOrDefault();
    }

    private IGameObject? FindFateCollectable(Dalamud.Game.ClientState.Objects.SubKinds.IPlayerCharacter player)
    {
        return Service.ObjectTable
            .Where(o => o.ObjectKind == ObjectKind.EventObj
                        && o.IsTargetable
                        && FateGameActions.GetObjectFateId(o) == this.activeFateId)
            .OrderBy(o => Vector3.DistanceSquared(o.Position, player.Position))
            .FirstOrDefault();
    }

    private void ReturnToWaiting(string reason)
    {
        Log(reason);
        this.navmesh.Stop();
        this.activeFateId = 0;
        this.engagementKind = EngagementKind.None;
        this.ReturnToSpawn();
    }

    private void TransitionTo(FateAutomationState state)
    {
        // Wrath is configured to attack our hard target even out of combat, so drop
        // the target when heading anywhere that isn't a fight to avoid unwanted pulls.
        if (state is not (FateAutomationState.Fighting or FateAutomationState.HandlingAggro))
        {
            Service.TargetManager.Target = null;
        }

        this.State = state;
        this.stateEnteredAt = DateTime.UtcNow;
        this.stateEntered = false;
    }

    private bool ShouldMount(Vector3 from, Vector3 to)
    {
        // Deliberately no action-status pre-check: right after zoning, actions
        // are briefly locked and the status would wrongly veto mounting. The
        // mount retry loop deals with transient failures instead.
        return Service.Configuration.AtmaAutomation.UseMount
               && !Service.Condition[ConditionFlag.Mounted]
               && !Service.Condition[ConditionFlag.InCombat]
               && Vector3.Distance(from, to) > MountDistance;
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

        this.AbandonTravel($"{this.TravelGoalName} is not reachable from here.");
    }

    private void AbandonTravel(string reason)
    {
        // The spawn point (and the target FATE, which sits at it) is worth
        // teleporting to other aetherytes for; an unreachable chain or filler
        // FATE is not - skip it for a while and go back to watching the target.
        if (!this.travelingToFate || this.engagementKind == EngagementKind.Target)
        {
            this.NextAetheryteOrFail(reason);
            return;
        }

        this.BlacklistFate(this.activeFateId);
        this.ReturnToWaiting($"{reason} Skipping that FATE for a few minutes.");
    }

    private void AbandonTravelOrRetry(string reason)
    {
        if (!this.retriedFateApproach)
        {
            this.retriedFateApproach = true;
            Log($"{reason} Retrying the approach.");
            this.travelGoal = this.GetActiveFate()?.Position ?? this.travelGoal;
            this.travelingToFate = true;
            this.flyDisabledForLeg = false;
            this.TransitionTo(FateAutomationState.ProbingRoute);
            return;
        }

        this.AbandonTravel(reason);
    }

    private void BlacklistFate(uint fateId)
    {
        if (fateId != 0)
        {
            this.fateBlacklist[fateId] = DateTime.UtcNow + TimeSpan.FromMinutes(5);
        }
    }

    private bool IsFateBlacklisted(uint fateId)
    {
        if (!this.fateBlacklist.TryGetValue(fateId, out var until))
        {
            return false;
        }

        if (until < DateTime.UtcNow)
        {
            this.fateBlacklist.Remove(fateId);
            return false;
        }

        return true;
    }

    private void NextAetheryteOrFail(string reason)
    {
        this.aetheryteIndex++;
        this.flyDisabledForLeg = false;
        if (this.aetheryteIndex >= this.aetheryteCandidates.Count)
        {
            this.Fail($"{reason} No aetheryte in {this.currentFate.ZoneName} can reach it.");
            return;
        }

        Log($"{reason} Trying the next aetheryte.");
        this.TransitionTo(FateAutomationState.Teleporting);
    }

    private void Fail(string reason)
    {
        this.Cleanup();
        this.State = FateAutomationState.Errored;
        this.StatusDetail = string.Empty;
        this.LastError = reason;
        Service.PluginLog.Warning($"[FateAutomation] Stopped: {reason}");
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
