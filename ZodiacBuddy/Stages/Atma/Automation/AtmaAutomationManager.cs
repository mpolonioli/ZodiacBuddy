using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using ECommons.Throttlers;
using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using ZodiacBuddy.Stages.Atma.Data;
using CSGame = FFXIVClientStructs.FFXIV.Client.Game;
using RelicNote = FFXIVClientStructs.FFXIV.Client.Game.UI.RelicNote;

namespace ZodiacBuddy.Stages.Atma.Automation;

/// <summary>
///     Automates the "Enemies" step of the Trial of the Braves books: teleports to
///     each incomplete enemy, navigates to its area with vnavmesh, scans for it and
///     lets Wrath Combo kill it until the book page is complete.
/// </summary>
internal sealed class AtmaAutomationManager : IDisposable
{
    private const float EarlyTargetRange = 40f;
    private const float RoamRange = 30f;
    private const float ExtraMeleeRange = 2.5f;
    private const float RangedAttackBonus = 8f;
    private const float MountDistance = 30f;
    private const uint JumpActionId = 2;
    private const uint MountRouletteActionId = 9;
    private const uint DismountActionId = 23;

    private readonly NavmeshIpc navmesh;
    private readonly WrathComboIpc wrath;
    private readonly Dictionary<ulong, DateTime> blacklist = [];
    private readonly Random random = new();

    private uint startedBookId;
    private BraveBook currentBook;
    private BraveTarget currentEnemy;
    private Vector3 destination;
    private ulong currentMobId;

    private DateTime stateEnteredAt;
    private bool stateEntered;
    private bool pathStarted;
    private AutomationState stateAfterAggro;

    private Vector3 lastPosition;
    private Vector3 lastMobPosition;
    private DateTime lastMovedAt;
    private DateTime lastTeleportAt;
    private int teleportAttempts;
    private int repathAttempts;
    private bool reapproachedOnce;
    private int lastProgress;
    private DateTime lastSpawnNoticeAt;

    /// <summary>
    ///     Initializes a new instance of the <see cref="AtmaAutomationManager" /> class.
    /// </summary>
    public AtmaAutomationManager()
    {
        this.navmesh = new NavmeshIpc();
        this.wrath = new WrathComboIpc();
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
    ///     Get the RelicNote row ID of the currently active book.
    /// </summary>
    /// <returns>The book ID, or 0 when no book is active.</returns>
    internal static unsafe uint GetActiveBookId()
    {
        var relicNote = RelicNote.Instance();
        return relicNote == null ? 0u : relicNote->RelicNoteId;
    }

    private static float MapToWorld(float mapCoord, float sizeFactor, short offset)
    {
        var c = sizeFactor / 100.0f;
        return ((((mapCoord - 1.0f) * c / 41.0f * 2048.0f) - 1024.0f) / c) - offset;
    }

    private static float GetAttackRange(IPlayerCharacter player, IBattleNpc mob)
    {
        var range = mob.HitboxRadius + player.HitboxRadius + ExtraMeleeRange;

        // Ranged and caster jobs attack from much further away.
        if (player.ClassJob.Value.Role is 3 or 4)
        {
            range += RangedAttackBonus;
        }

        return range;
    }

    private static unsafe bool TryUseGeneralAction(uint actionId)
    {
        var actionManager = CSGame.ActionManager.Instance();
        return actionManager->GetActionStatus(CSGame.ActionType.GeneralAction, actionId) == 0
               && actionManager->UseAction(CSGame.ActionType.GeneralAction, actionId);
    }

    private static unsafe bool CanUseGeneralAction(uint actionId)
        => CSGame.ActionManager.Instance()->GetActionStatus(CSGame.ActionType.GeneralAction, actionId) == 0;

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

        this.TransitionTo(Service.ClientState.TerritoryType == mapLink.TerritoryType.RowId
            ? AutomationState.WaitingForNavmesh
            : AutomationState.Teleporting);
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
            this.stateEntered = true;
        }

        if (Service.ClientState.TerritoryType == this.currentEnemy.Position.TerritoryType.RowId
            && !Service.Condition[ConditionFlag.BetweenAreas]
            && !Service.Condition[ConditionFlag.BetweenAreas51])
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

        // While the cast bar is up or we are zoning, just wait.
        if (Service.Condition[ConditionFlag.Casting]
            || Service.Condition[ConditionFlag.BetweenAreas]
            || Service.Condition[ConditionFlag.BetweenAreas51])
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

            var aetheryteId = AtmaManager.GetNearestAetheryte(this.currentEnemy.Position);
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
            this.TransitionTo(AutomationState.NavigatingToArea);
            return;
        }

        if (this.StateAge > TimeSpan.FromSeconds(180))
        {
            this.Fail("The navmesh did not become ready in time.");
        }
    }

    private void HandleNavigatingToArea()
    {
        this.StatusDetail = $"Traveling to {this.currentEnemy.Name}'s area...";
        var player = Service.ObjectTable.LocalPlayer!;

        if (!this.stateEntered)
        {
            var floor = this.FindPointOnFloor(this.destination);
            if (floor is null)
            {
                this.Fail($"No navigable point found near {this.currentEnemy.Name}'s position.");
                return;
            }

            this.destination = floor.Value;
            this.repathAttempts = 0;
            this.stateEntered = true;
        }

        if (!this.pathStarted)
        {
            // Mount up for longer travels; the mount cast requires standing still,
            // so it has to finish before the path starts. Give up after a few
            // seconds (or when mounting is impossible here) and walk instead.
            if (this.ShouldMount(player.Position) && this.StateAge < TimeSpan.FromSeconds(6))
            {
                if (EzThrottler.Throttle("ZodiacBuddy.AtmaAuto.Mount", 1000))
                {
                    TryUseGeneralAction(MountRouletteActionId);
                }

                return;
            }

            this.navmesh.SetTolerance(0.5f);
            if (!this.navmesh.PathfindAndMoveTo(this.destination))
            {
                this.Fail("vnavmesh could not start pathfinding.");
                return;
            }

            this.ResetStuckDetection(player.Position);
            this.pathStarted = true;
            return;
        }

        // Fight back anything that aggroed us on the way.
        if (Service.Condition[ConditionFlag.InCombat])
        {
            this.navmesh.Stop();
            this.stateAfterAggro = AutomationState.NavigatingToArea;
            this.TransitionTo(AutomationState.HandlingAggro);
            return;
        }

        // If the enemy we want is already close by, cut travel short.
        if (EzThrottler.Throttle("ZodiacBuddy.AtmaAuto.EarlyScan", 1000))
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

        if (Vector3.Distance(player.Position, this.destination) <= 3f && !this.navmesh.IsPathRunning)
        {
            this.TransitionTo(AutomationState.Scanning);
            return;
        }

        if (this.CheckStuck(player.Position, () =>
            {
                this.navmesh.Stop();
                this.navmesh.PathfindAndMoveTo(this.destination);
            }))
        {
            this.Fail("Stuck while navigating.");
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

        var attacker = Service.ObjectTable.OfType<IBattleNpc>()
            .Where(b => !b.IsDead && b.IsTargetable && b.TargetObjectId == player.GameObjectId)
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
            or AutomationState.NavigatingToArea
            or AutomationState.Scanning)
        {
            Service.TargetManager.Target = null;
        }

        this.State = state;
        this.stateEnteredAt = DateTime.UtcNow;
        this.stateEntered = false;
        this.pathStarted = false;
    }

    private bool ShouldMount(Vector3 from)
    {
        return Service.Configuration.AtmaAutomation.UseMount
               && !Service.Condition[ConditionFlag.Mounted]
               && !Service.Condition[ConditionFlag.InCombat]
               && Vector3.Distance(from, this.destination) > MountDistance
               && CanUseGeneralAction(MountRouletteActionId);
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

    private Vector3? FindPointOnFloor(Vector3 approximate)
    {
        foreach (var y in new[] { 1024f, 0f })
        {
            foreach (var halfExtent in new[] { 5f, 10f, 20f, 50f })
            {
                var floor = this.navmesh.PointOnFloor(approximate with { Y = y }, halfExtent);
                if (floor is not null)
                {
                    return floor;
                }
            }
        }

        return null;
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

            // A hop first gets us over small obstacles the path clips through.
            TryUseGeneralAction(JumpActionId);
            this.ResetStuckDetection(position);
            recover();
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
