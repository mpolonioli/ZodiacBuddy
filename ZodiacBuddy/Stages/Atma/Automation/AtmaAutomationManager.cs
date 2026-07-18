using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.SubKinds;
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
using CSGame = FFXIVClientStructs.FFXIV.Client.Game;
using RelicNote = FFXIVClientStructs.FFXIV.Client.Game.UI.RelicNote;

namespace ZodiacBuddy.Stages.Atma.Automation;

/// <summary>
///     Automates the "Enemies" step of the Trial of the Braves books: teleports to
///     each incomplete enemy, navigates to its area with vnavmesh, scans for it and
///     lets Wrath Combo kill it until the book page is complete.
/// </summary>
internal sealed class AtmaAutomationManager : IDisposable, IBookAutomation
{
    private const float EarlyTargetRange = 40f;
    private const float RoamRange = 30f;
    private const float ExtraMeleeRange = 2.5f;
    private const float RangedAttackBonus = 8f;
    private const float MountDistance = 30f;

    // While mopping up after a fight, only enemies within this range of the
    // player count as attackers worth chasing, so we don't run off across the
    // zone toward mobs that are merely in combat with someone else.
    private const float AggroSearchRange = 40f;

    private const uint JumpActionId = 2;
    private const uint MountRouletteActionId = 9;
    private const uint DismountActionId = 23;

    // When each hover-landing attempt began, keyed by the caller's throttle key,
    // so the shared helper can abandon an unlandable spot after a few seconds.
    private static readonly Dictionary<string, DateTime> HoverLandingStartedAt = [];

    private readonly NavmeshIpc navmesh;
    private readonly WrathComboIpc wrath;
    private readonly AdvancedUnstuck unstuck;
    private readonly BookTravelManager bookTravel;
    private readonly Dictionary<ulong, DateTime> blacklist = [];
    private readonly Random random = new();
    private System.Action? unstuckRecovery;

    private uint startedBookId;
    private BraveBook currentBook;
    private BraveTarget currentEnemy;
    private Vector3 destination;
    private ulong currentMobId;

    private List<uint> aetheryteCandidates = [];
    private int aetheryteIndex;
    private Task<List<Vector3>>? probeTask;
    private bool probingFly;
    private List<Vector3>? plannedPath;
    private bool plannedFly;
    private bool flyDisabledForLeg;

    private DateTime stateEnteredAt;
    private bool stateEntered;
    private AutomationState stateAfterAggro;

    private Vector3 lastPosition;
    private Vector3 lastMobPosition;
    private DateTime lastMovedAt;
    private DateTime lastTeleportAt;
    private int teleportAttempts;
    private uint teleportStartTerritory;
    private bool sawZoning;
    private int repathAttempts;
    private bool reapproachedOnce;
    private int lastProgress;
    private DateTime lastSpawnNoticeAt;

    /// <summary>
    ///     Initializes a new instance of the <see cref="AtmaAutomationManager" /> class.
    /// </summary>
    /// <param name="unstuck">Shared unstuck helper.</param>
    /// <param name="bookTravel">Book travel manager, cancelled when this automation starts.</param>
    public AtmaAutomationManager(AdvancedUnstuck unstuck, BookTravelManager bookTravel)
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
    public AutomationState State { get; private set; } = AutomationState.Idle;

    /// <summary>
    ///     Gets a human-readable detail about the current state.
    /// </summary>
    public string StatusDetail { get; private set; } = string.Empty;

    /// <summary>
    ///     Gets the last error message, if any.
    /// </summary>
    public string LastError { get; private set; } = string.Empty;

    /// <summary>
    ///     Gets the book enemy slot currently being worked on, or -1.
    /// </summary>
    public int CurrentSlot { get; private set; } = -1;

    /// <summary>
    ///     Gets a value indicating whether the automation is currently running.
    /// </summary>
    public bool IsRunning => this.State is not (AutomationState.Idle or AutomationState.Completed or AutomationState.Errored);

    /// <inheritdoc />
    public bool IsCompleted => this.State == AutomationState.Completed;

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
    internal static unsafe bool CanStart(bool navmeshInstalled, bool wrathAvailable, out string reason)
    {
        if (!Service.ClientState.IsLoggedIn)
        {
            reason = "Not logged in.";
            return false;
        }

        var player = Service.ObjectTable.LocalPlayer;
        if (player is null
            || Service.Condition[ConditionFlag.BetweenAreas]
            || Service.Condition[ConditionFlag.BetweenAreas51])
        {
            reason = "Loading...";
            return false;
        }

        if (player.IsDead)
        {
            reason = "You are dead.";
            return false;
        }

        var relicNote = RelicNote.Instance();
        if (relicNote == null || relicNote->RelicNoteId == 0)
        {
            reason = "No Trial of the Braves book is active. Equip your Zodiac weapon.";
            return false;
        }

        if (!navmeshInstalled)
        {
            reason = "The vnavmesh plugin is not installed.";
            return false;
        }

        if (!wrathAvailable)
        {
            reason = "The Wrath Combo plugin is not installed or not ready.";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    /// <summary>
    ///     Start working through the enemies of the current book.
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
            Service.PluginLog.Warning($"[Automation] Cannot start: {reason}");
            return;
        }

        if (!this.wrath.BeginControl())
        {
            this.LastError = "Could not take control of Wrath Combo.";
            Service.PluginLog.Warning($"[Automation] {this.LastError}");
            return;
        }

        this.bookTravel.Cancel("The enemies automation started.");

        this.startedBookId = RelicNote.Instance()->RelicNoteId;
        this.currentBook = BraveBook.GetValue(this.startedBookId);
        this.LastError = string.Empty;
        this.blacklist.Clear();
        Log($"Starting enemies automation for {this.currentBook.Name}.");
        this.TransitionTo(AutomationState.SelectNextEnemy);
    }

    /// <summary>
    ///     Stop the automation and release all controlled plugins.
    /// </summary>
    /// <param name="reason">Reason displayed to the user.</param>
    public void Stop(string reason)
    {
        this.Cleanup();
        this.State = AutomationState.Idle;
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

    /// <summary>
    ///     Get the current kill count of a book enemy slot.
    /// </summary>
    /// <param name="slot">Enemy slot, 0 to 9.</param>
    /// <returns>The kill count, or -1 when no book is active.</returns>
    internal static unsafe int GetMonsterProgress(int slot)
    {
        var relicNote = RelicNote.Instance();
        return relicNote == null ? -1 : relicNote->GetMonsterProgress(slot);
    }

    /// <summary>
    ///     Get whether a book dungeon slot has been completed.
    /// </summary>
    /// <param name="slot">Dungeon slot, 0 to 2.</param>
    /// <returns>Whether the dungeon is complete; false when no book is active.</returns>
    internal static unsafe bool IsDungeonComplete(int slot)
    {
        var relicNote = RelicNote.Instance();
        return relicNote != null && relicNote->IsDungeonComplete(slot);
    }

    /// <summary>
    ///     Get whether a book FATE slot has been completed.
    /// </summary>
    /// <param name="slot">FATE slot, 0 to 2.</param>
    /// <returns>Whether the FATE is complete; false when no book is active.</returns>
    internal static unsafe bool IsFateComplete(int slot)
    {
        var relicNote = RelicNote.Instance();
        return relicNote != null && relicNote->IsFateComplete(slot);
    }

    /// <summary>
    ///     Get whether a book leve slot has been completed.
    /// </summary>
    /// <param name="slot">Leve slot, 0 to 3.</param>
    /// <returns>Whether the leve is complete; false when no book is active.</returns>
    internal static unsafe bool IsLeveComplete(int slot)
    {
        var relicNote = RelicNote.Instance();
        return relicNote != null && relicNote->IsLeveComplete(slot);
    }

    /// <summary>
    ///     Get the RelicNote row ID of the currently active book.
    /// </summary>
    /// <returns>The book ID, or 0 when no book is active.</returns>
    internal static unsafe uint GetActiveBookId()
    {
        var relicNote = RelicNote.Instance();
        return relicNote == null ? 0u : relicNote->RelicNoteId;
    }

    /// <summary>
    ///     Convert a map coordinate to a world coordinate.
    /// </summary>
    /// <param name="mapCoord">Map coordinate.</param>
    /// <param name="sizeFactor">Size factor of the map.</param>
    /// <param name="offset">Map offset for the axis.</param>
    /// <returns>The world coordinate.</returns>
    internal static float MapToWorld(float mapCoord, float sizeFactor, short offset)
    {
        var c = sizeFactor / 100.0f;
        return ((((mapCoord - 1.0f) * c / 41.0f * 2048.0f) - 1024.0f) / c) - offset;
    }

    /// <summary>
    ///     Get the distance at which the player can attack a mob.
    /// </summary>
    /// <param name="player">The local player.</param>
    /// <param name="mob">The mob to attack.</param>
    /// <returns>The attack range in yalms.</returns>
    internal static float GetAttackRange(IPlayerCharacter player, IBattleNpc mob)
    {
        var range = mob.HitboxRadius + player.HitboxRadius + ExtraMeleeRange;

        // Ranged and caster jobs attack from much further away.
        if (player.ClassJob.Value.Role is 3 or 4)
        {
            range += RangedAttackBonus;
        }

        return range;
    }

    /// <summary>
    ///     Find a hostile combatant that is keeping the player in combat, so it can
    ///     be finished off. Besides the enemy attacking the player directly, this
    ///     catches any nearby enemy still flagged in combat - a straggler that
    ///     engaged during a FATE (often one attacking the chocobo companion rather
    ///     than the player) and never disengaged - which otherwise leaves the
    ///     automation stuck "fighting off attackers" indefinitely.
    /// </summary>
    /// <param name="player">The local player.</param>
    /// <returns>The nearest such enemy, or <c>null</c> if none is around.</returns>
    internal static IBattleNpc? FindLingeringAttacker(IPlayerCharacter player)
    {
        return Service.ObjectTable.OfType<IBattleNpc>()
            .Where(b => !b.IsDead
                        && b.IsTargetable
                        && b.IsHostile()
                        && b.BattleNpcKind == BattleNpcSubKind.Combatant
                        && Vector3.Distance(b.Position, player.Position) <= AggroSearchRange
                        && (b.TargetObjectId == player.GameObjectId
                            || b.StatusFlags.HasFlag(StatusFlags.InCombat)))

            // Whatever is on the player comes first, then the closest.
            .OrderByDescending(b => b.TargetObjectId == player.GameObjectId)
            .ThenBy(b => Vector3.DistanceSquared(b.Position, player.Position))
            .FirstOrDefault();
    }

    /// <summary>
    ///     Use a general action if it is currently available.
    /// </summary>
    /// <param name="actionId">General action ID.</param>
    /// <returns>Whether the action was used.</returns>
    internal static unsafe bool TryUseGeneralAction(uint actionId)
    {
        var actionManager = CSGame.ActionManager.Instance();
        var status = actionManager->GetActionStatus(CSGame.ActionType.GeneralAction, actionId);
        if (status != 0)
        {
            Service.PluginLog.Debug($"[Automation] General action {actionId} unavailable (status {status}).");
            return false;
        }

        return actionManager->UseAction(CSGame.ActionType.GeneralAction, actionId);
    }

    /// <summary>
    ///     Check whether flying is available here right now.
    /// </summary>
    /// <returns>Whether flying is available.</returns>
    internal static bool IsFlightAvailable()
    {
        // The game's flight gate only answers reliably while mounted (it also
        // covers ground-only mounts from the roulette). Unmounted it reports
        // false even where flight works, so assume flight is possible then -
        // anyone working on a Zodiac relic has finished the ARR MSQ. If the
        // assumption is ever wrong, the stuck recovery re-plans on the ground.
        if (!Service.Condition[ConditionFlag.Mounted])
        {
            return true;
        }

        return CSGame.Control.Control.CanFly;
    }

    /// <summary>
    ///     Land after a flying path ended while still airborne. vnavmesh flies to
    ///     within path tolerance of the ground destination but never switches out
    ///     of flight, so InFlight stays set and the player hovers just above the
    ///     goal; dismounting drops the small remaining height. The destination can
    ///     sit on terrain the game refuses to dismount onto (a rock or a small
    ///     object), so after a few seconds of failed attempts this gives up and
    ///     lets the caller treat hovering at the destination as arrival - the
    ///     follow-up states descend on their own at the actual enemy, FATE, or
    ///     NPC, all of which stand on landable ground.
    /// </summary>
    /// <param name="navmesh">Navmesh IPC of the calling manager.</param>
    /// <param name="throttleKey">Throttle key for the landing action, also the
    ///     key under which the give-up deadline is tracked.</param>
    /// <returns>Whether a landing is still being attempted and the caller should
    ///     wait; false once landed or once the attempt is abandoned.</returns>
    internal static bool LandIfHovering(NavmeshIpc navmesh, string throttleKey)
    {
        // Only a flying path that has stopped leaves us hovering; while a path or
        // pathfind is still running the descent (or the leg) is not finished yet.
        if (!Service.Condition[ConditionFlag.InFlight]
            || navmesh.IsPathRunning
            || navmesh.IsPathfindInProgress)
        {
            HoverLandingStartedAt.Remove(throttleKey);
            return false;
        }

        if (!HoverLandingStartedAt.TryGetValue(throttleKey, out var since))
        {
            since = DateTime.UtcNow;
            HoverLandingStartedAt[throttleKey] = since;
        }

        if (EzThrottler.Throttle(throttleKey, 1000))
        {
            Service.PluginLog.Debug("[Automation] Flying path ended hovering at the destination; dismounting to land.");
            TryUseGeneralAction(DismountActionId);
        }

        // A landable spot clears InFlight well under a second; if it has not
        // cleared after several, the spot is not landable - stop waiting.
        if (DateTime.UtcNow - since > TimeSpan.FromSeconds(4))
        {
            HoverLandingStartedAt.Remove(throttleKey);
            Service.PluginLog.Debug("[Automation] Could not dismount at the destination; proceeding while airborne.");
            return false;
        }

        return true;
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

            // A kill can be counted while traveling, scanning or clearing aggro, not
            // just in the Fighting state (ranged jobs kill mobs mid-approach); check
            // completion of the current enemy in every state of the kill loop.
            if (this.CurrentSlot >= 0
                && this.State is AutomationState.NavigatingToArea
                    or AutomationState.Scanning
                    or AutomationState.MovingToEnemy
                    or AutomationState.Fighting
                && GetMonsterProgress(this.CurrentSlot) >= this.currentEnemy.RequiredKills)
            {
                this.navmesh.Stop();
                Log($"{this.currentEnemy.Name} complete!");
                this.stateAfterAggro = AutomationState.SelectNextEnemy;
                this.TransitionTo(AutomationState.HandlingAggro);
                return;
            }

            switch (this.State)
            {
                case AutomationState.SelectNextEnemy:
                    this.HandleSelectNextEnemy();
                    break;
                case AutomationState.Teleporting:
                    this.HandleTeleporting();
                    break;
                case AutomationState.WaitingForNavmesh:
                    this.HandleWaitingForNavmesh();
                    break;
                case AutomationState.ProbingRoute:
                    this.HandleProbingRoute();
                    break;
                case AutomationState.NavigatingToArea:
                    this.HandleNavigatingToArea();
                    break;
                case AutomationState.Scanning:
                    this.HandleScanning();
                    break;
                case AutomationState.MovingToEnemy:
                    this.HandleMovingToEnemy();
                    break;
                case AutomationState.Fighting:
                    this.HandleFighting();
                    break;
                case AutomationState.HandlingAggro:
                    this.HandleAggro();
                    break;
            }
        }
        catch (Exception ex)
        {
            Service.PluginLog.Error(ex, "Exception during Trial of the Braves automation.");
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
            return this.State == AutomationState.Teleporting;
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

        if (GetActiveBookId() != this.startedBookId)
        {
            this.Fail("The Trial of the Braves book is no longer active. Keep your Zodiac weapon equipped.");
            return false;
        }

        return true;
    }

    private void HandleSelectNextEnemy()
    {
        this.CurrentSlot = -1;
        for (var i = 0; i < this.currentBook.Enemies.Length; i++)
        {
            if (GetMonsterProgress(i) < this.currentBook.Enemies[i].RequiredKills)
            {
                this.CurrentSlot = i;
                break;
            }
        }

        if (this.CurrentSlot < 0)
        {
            this.Cleanup();
            this.State = AutomationState.Completed;
            this.StatusDetail = string.Empty;
            Log($"The enemies page of {this.currentBook.Name} is complete!");
            return;
        }

        this.currentEnemy = this.currentBook.Enemies[this.CurrentSlot];
        this.lastProgress = GetMonsterProgress(this.CurrentSlot);
        this.reapproachedOnce = false;

        var mapLink = this.currentEnemy.Position;
        var map = Service.DataManager.GetExcelSheet<Map>().GetRow(mapLink.Map.RowId);
        var x = MapToWorld(mapLink.XCoord, map.SizeFactor, map.OffsetX);
        var z = MapToWorld(mapLink.YCoord, map.SizeFactor, map.OffsetY);
        this.destination = new Vector3(x, 0, z);

        Log($"Next enemy: {this.currentEnemy.Name} in {this.currentEnemy.ZoneName} " +
                  $"({GetMonsterProgress(this.CurrentSlot)}/{this.currentEnemy.RequiredKills}).");

        this.flyDisabledForLeg = false;
        this.aetheryteCandidates = AtmaManager.GetAetherytesByDistance(mapLink);

        if (Service.ClientState.TerritoryType == mapLink.TerritoryType.RowId)
        {
            // Already here; if the destination turns out unreachable from this spot,
            // the aetheryte fallback starts from the closest one.
            this.aetheryteIndex = -1;
            this.TransitionTo(AutomationState.WaitingForNavmesh);
            return;
        }

        if (this.aetheryteCandidates.Count == 0)
        {
            this.Fail($"No aetheryte found in {this.currentEnemy.ZoneName}.");
            return;
        }

        this.aetheryteIndex = 0;
        this.TransitionTo(AutomationState.Teleporting);
    }

    private void HandleTeleporting()
    {
        this.StatusDetail = $"Teleporting to {this.currentEnemy.ZoneName}...";

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

        if (Service.ClientState.TerritoryType == this.currentEnemy.Position.TerritoryType.RowId
            && (this.sawZoning || this.teleportStartTerritory != this.currentEnemy.Position.TerritoryType.RowId))
        {
            this.TransitionTo(AutomationState.WaitingForNavmesh);
            return;
        }

        // Something is attacking us; deal with it before we can teleport.
        if (Service.Condition[ConditionFlag.InCombat] && !Service.Condition[ConditionFlag.Casting])
        {
            this.stateAfterAggro = AutomationState.Teleporting;
            this.TransitionTo(AutomationState.HandlingAggro);
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
                this.Fail($"Teleport to {this.currentEnemy.ZoneName} failed repeatedly.");
                return;
            }

            var aetheryteId = this.aetheryteCandidates.ElementAtOrDefault(Math.Max(this.aetheryteIndex, 0));
            if (aetheryteId == 0 || !AtmaManager.ExecuteTeleport(aetheryteId))
            {
                this.Fail($"Could not teleport to {this.currentEnemy.ZoneName}.");
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
            this.TransitionTo(AutomationState.ProbingRoute);
            return;
        }

        if (this.StateAge > TimeSpan.FromSeconds(180))
        {
            this.Fail("The navmesh did not become ready in time.");
        }
    }

    private void HandleProbingRoute()
    {
        this.StatusDetail = $"Checking the route to {this.currentEnemy.Name}'s area...";
        var player = Service.ObjectTable.LocalPlayer!;

        if (!this.stateEntered)
        {
            var floor = this.navmesh.FindNavigablePoint(this.destination);
            if (floor is null)
            {
                this.NextAetheryteOrFail($"No navigable point found near {this.currentEnemy.Name}'s position.");
                return;
            }

            this.destination = floor.Value;
            this.probeTask = null;
            this.stateEntered = true;
        }

        // Deal with attackers before planning any travel.
        if (Service.Condition[ConditionFlag.InCombat])
        {
            this.stateAfterAggro = AutomationState.ProbingRoute;
            this.TransitionTo(AutomationState.HandlingAggro);
            return;
        }

        if (this.probeTask is null)
        {
            var wantFly = this.WantFly();

            // Flying requires a mount; mount up before probing so the probe mode
            // matches how we will actually travel. Ground travel also mounts for
            // longer legs, purely for speed. The window is generous because the
            // post-zoning action lockout eats into it.
            if ((wantFly || this.ShouldMount(player.Position)) && this.StateAge < TimeSpan.FromSeconds(10))
            {
                // The roulette action is a toggle; pressing it while already
                // mounted would dismount instead.
                if (!Service.Condition[ConditionFlag.Mounted]
                    && EzThrottler.Throttle("ZodiacBuddy.AtmaAuto.Mount", 1000))
                {
                    TryUseGeneralAction(MountRouletteActionId);
                }

                if (!Service.Condition[ConditionFlag.Mounted])
                {
                    return;
                }
            }

            // Re-evaluate after mounting: the game reports flight as available
            // only in situations that can change with the mount.
            this.probingFly = this.WantFly() && Service.Condition[ConditionFlag.Mounted];
            Service.PluginLog.Debug(
                $"[Automation] Route probe: fly={this.probingFly} " +
                $"(canFly={CSGame.Control.Control.CanFly}, mounted={Service.Condition[ConditionFlag.Mounted]}, " +
                $"flyDisabledForLeg={this.flyDisabledForLeg})");
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
        this.TransitionTo(AutomationState.NavigatingToArea);
    }

    private void HandleNavigatingToArea()
    {
        this.StatusDetail = $"Traveling to {this.currentEnemy.Name}'s area...";
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
            this.stateAfterAggro = AutomationState.ProbingRoute;
            this.TransitionTo(AutomationState.HandlingAggro);
            return;
        }

        // If the enemy we want is already close by, cut travel short - but not
        // while airborne, ground pathing can't start from up there.
        if (!Service.Condition[ConditionFlag.InFlight]
            && EzThrottler.Throttle("ZodiacBuddy.AtmaAuto.EarlyScan", 1000))
        {
            var mob = this.FindEnemy();
            if (mob is not null && Vector3.Distance(player.Position, mob.Position) <= EarlyTargetRange)
            {
                this.navmesh.Stop();
                this.currentMobId = mob.GameObjectId;
                this.TransitionTo(AutomationState.MovingToEnemy);
                return;
            }
        }

        // The flying path leaves us hovering just over the destination; land if
        // the spot allows it. An unlandable spot still counts as arrival -
        // Scanning roams and MovingToEnemy dismounts at the enemy on landable
        // ground, so there is no need to be on foot here.
        if (LandIfHovering(this.navmesh, "ZodiacBuddy.AtmaAuto.Land"))
        {
            return;
        }

        if (Vector3.Distance(player.Position, this.destination) <= 5f && !this.navmesh.IsPathRunning)
        {
            this.TransitionTo(AutomationState.Scanning);
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
                this.TransitionTo(AutomationState.ProbingRoute);
            }
            else
            {
                this.NextAetheryteOrFail("Stuck while navigating.");
            }

            return;
        }

        if (this.StateAge > TimeSpan.FromSeconds(300))
        {
            this.Fail($"Could not reach {this.currentEnemy.Name}'s area in time.");
        }
    }

    private void HandleScanning()
    {
        this.StatusDetail = $"Searching for {this.currentEnemy.Name}...";

        if (!this.stateEntered)
        {
            this.lastSpawnNoticeAt = DateTime.UtcNow;
            this.stateEntered = true;
        }

        if (Service.Condition[ConditionFlag.InCombat])
        {
            this.navmesh.Stop();
            this.stateAfterAggro = AutomationState.Scanning;
            this.TransitionTo(AutomationState.HandlingAggro);
            return;
        }

        if (!EzThrottler.Throttle("ZodiacBuddy.AtmaAuto.Scan", 1000))
        {
            return;
        }

        var mob = this.FindEnemy();
        if (mob is not null)
        {
            this.navmesh.Stop();
            this.currentMobId = mob.GameObjectId;
            this.TransitionTo(AutomationState.MovingToEnemy);
            return;
        }

        // No spawns found; roam around the anchor point while waiting.
        if (this.StateAge > TimeSpan.FromSeconds(10) && !this.navmesh.IsPathRunning && !this.navmesh.IsPathfindInProgress)
        {
            var angle = this.random.NextDouble() * Math.Tau;
            var range = (float)(this.random.NextDouble() * RoamRange);
            var seed = this.destination + new Vector3((float)Math.Cos(angle) * range, 5f, (float)Math.Sin(angle) * range);
            var floor = this.navmesh.PointOnFloor(seed, 10f);
            if (floor is not null)
            {
                this.navmesh.PathfindAndMoveTo(floor.Value);
            }
        }

        if (DateTime.UtcNow - this.lastSpawnNoticeAt > TimeSpan.FromSeconds(60))
        {
            this.lastSpawnNoticeAt = DateTime.UtcNow;
            Log($"Still waiting for {this.currentEnemy.Name} to spawn...");
        }
    }

    private void HandleMovingToEnemy()
    {
        this.StatusDetail = $"Moving to {this.currentEnemy.Name}...";
        var player = Service.ObjectTable.LocalPlayer!;

        var mob = this.GetCurrentMob();
        if (mob is null)
        {
            this.TransitionTo(AutomationState.Scanning);
            return;
        }

        if (Service.TargetManager.Target?.GameObjectId != mob.GameObjectId)
        {
            Service.TargetManager.Target = mob;
        }

        var range = GetAttackRange(player, mob);
        if (Vector3.Distance(player.Position, mob.Position) <= range)
        {
            this.navmesh.Stop();

            // Combat can't be done mounted.
            if (Service.Condition[ConditionFlag.Mounted])
            {
                if (EzThrottler.Throttle("ZodiacBuddy.AtmaAuto.Dismount", 500))
                {
                    TryUseGeneralAction(DismountActionId);
                }

                return;
            }

            this.TransitionTo(AutomationState.Fighting);
            return;
        }

        if (!this.stateEntered)
        {
            this.navmesh.PathfindAndMoveCloseTo(mob.Position, Math.Max(range - 0.5f, 1f));
            this.lastMobPosition = mob.Position;
            this.repathAttempts = 0;
            this.ResetStuckDetection(player.Position);
            this.stateEntered = true;
            return;
        }

        // Re-path when the mob wandered off from where we were heading.
        if (EzThrottler.Throttle("ZodiacBuddy.AtmaAuto.Repath", 1000)
            && Vector3.Distance(mob.Position, this.lastMobPosition) > 5f)
        {
            this.navmesh.Stop();
            this.navmesh.PathfindAndMoveCloseTo(mob.Position, Math.Max(range - 0.5f, 1f));
            this.lastMobPosition = mob.Position;
        }

        // This mob may be somewhere unreachable; give up on it and rescan.
        if (this.CheckStuck(player.Position, () =>
            {
                this.navmesh.Stop();
                this.navmesh.PathfindAndMoveCloseTo(mob.Position, Math.Max(range - 0.5f, 1f));
            })
            || this.StateAge > TimeSpan.FromSeconds(60))
        {
            this.Blacklist(mob.GameObjectId);
            this.TransitionTo(AutomationState.Scanning);
        }
    }

    private void HandleFighting()
    {
        var progress = GetMonsterProgress(this.CurrentSlot);
        this.StatusDetail = $"Fighting {this.currentEnemy.Name} ({progress}/{this.currentEnemy.RequiredKills}).";

        // Slot completion is handled globally in OnUpdate.
        if (progress > this.lastProgress)
        {
            // A kill was counted; look for the next spawn of the same enemy.
            this.lastProgress = progress;
            this.TransitionTo(AutomationState.Scanning);
            return;
        }

        var mob = this.GetCurrentMob();
        if (mob is null || mob.IsDead)
        {
            // The mob died without progress (tapped by someone else) or despawned.
            this.TransitionTo(AutomationState.Scanning);
            return;
        }

        // Keep the mob hard-targeted so Wrath attacks it.
        if (Service.TargetManager.Target?.GameObjectId != mob.GameObjectId)
        {
            Service.TargetManager.Target = mob;
        }

        // Combat never started; the mob may be unreachable.
        if (!Service.Condition[ConditionFlag.InCombat] && this.StateAge > TimeSpan.FromSeconds(10))
        {
            if (!this.reapproachedOnce)
            {
                this.reapproachedOnce = true;
                this.TransitionTo(AutomationState.MovingToEnemy);
            }
            else
            {
                this.Blacklist(mob.GameObjectId);
                this.TransitionTo(AutomationState.Scanning);
            }

            return;
        }

        if (this.StateAge > TimeSpan.FromSeconds(120))
        {
            this.Blacklist(mob.GameObjectId);
            this.TransitionTo(AutomationState.Scanning);
        }
    }

    private void HandleAggro()
    {
        this.StatusDetail = "Fighting off attackers...";
        var player = Service.ObjectTable.LocalPlayer!;

        var attacker = FindLingeringAttacker(player);

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
                if (EzThrottler.Throttle("ZodiacBuddy.AtmaAuto.Dismount", 500))
                {
                    TryUseGeneralAction(DismountActionId);
                }

                return;
            }

            if (Service.TargetManager.Target?.GameObjectId != attacker.GameObjectId)
            {
                Service.TargetManager.Target = attacker;
            }

            var range = GetAttackRange(player, attacker);
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

    private void TransitionTo(AutomationState state)
    {
        // Wrath is configured to attack our hard target even out of combat, so drop
        // the target when heading anywhere that isn't a fight to avoid unwanted pulls.
        if (state is AutomationState.SelectNextEnemy
            or AutomationState.Teleporting
            or AutomationState.ProbingRoute
            or AutomationState.NavigatingToArea
            or AutomationState.Scanning)
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
               && IsFlightAvailable();
    }

    private void OnProbeFailed()
    {
        if (this.probingFly)
        {
            // The air route failed; try the ground mesh before changing aetheryte.
            this.flyDisabledForLeg = true;
            return;
        }

        this.NextAetheryteOrFail($"{this.currentEnemy.Name}'s area is not reachable from here.");
    }

    private void NextAetheryteOrFail(string reason)
    {
        this.aetheryteIndex++;
        this.flyDisabledForLeg = false;
        if (this.aetheryteIndex >= this.aetheryteCandidates.Count)
        {
            this.Fail($"{reason} No aetheryte in {this.currentEnemy.ZoneName} can reach it.");
            return;
        }

        Log($"{reason} Trying the next aetheryte.");
        this.TransitionTo(AutomationState.Teleporting);
    }

    private void Fail(string reason)
    {
        this.Cleanup();
        this.State = AutomationState.Errored;
        this.StatusDetail = string.Empty;
        this.LastError = reason;
        Service.PluginLog.Warning($"[Automation] Stopped: {reason}");
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

    private IBattleNpc? FindEnemy()
    {
        var player = Service.ObjectTable.LocalPlayer;
        if (player is null)
        {
            return null;
        }

        this.PruneBlacklist();
        return Service.ObjectTable.OfType<IBattleNpc>()
            .Where(b => b.NameId == this.currentEnemy.BNpcNameId
                        && !b.IsDead
                        && b.IsTargetable
                        && !this.blacklist.ContainsKey(b.GameObjectId)
                        && (b.TargetObject is null || b.TargetObjectId == player.GameObjectId))
            .OrderByDescending(b => b.TargetObjectId == player.GameObjectId)
            .ThenBy(b => Vector3.DistanceSquared(b.Position, player.Position))
            .FirstOrDefault();
    }

    private IBattleNpc? GetCurrentMob()
    {
        var mob = Service.ObjectTable.SearchById(this.currentMobId) as IBattleNpc;
        if (mob is null || mob.IsDead || !mob.IsTargetable)
        {
            return null;
        }

        // Someone else claimed it while we were approaching.
        var player = Service.ObjectTable.LocalPlayer;
        if (player is not null && mob.TargetObject is not null && mob.TargetObjectId != player.GameObjectId
            && !Service.Condition[ConditionFlag.InCombat])
        {
            return null;
        }

        return mob;
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
                TryUseGeneralAction(JumpActionId);
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
                    TryUseGeneralAction(JumpActionId);
                    recover();
                }
            }
        }

        return false;
    }

    private void Blacklist(ulong gameObjectId)
    {
        this.blacklist[gameObjectId] = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        this.currentMobId = 0;
    }

    private void PruneBlacklist()
    {
        var now = DateTime.UtcNow;
        foreach (var expired in this.blacklist.Where(kv => kv.Value < now).Select(kv => kv.Key).ToList())
        {
            this.blacklist.Remove(expired);
        }
    }

    private static void Log(string message)
    {
        Service.PluginLog.Information($"[Automation] {message}");
    }
}
