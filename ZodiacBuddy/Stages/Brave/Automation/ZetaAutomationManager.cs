using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Plugin.Services;
using ECommons.Throttlers;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;
using System.Numerics;
using ZodiacBuddy.Stages.Atma;
using ZodiacBuddy.Stages.Atma.Automation;

namespace ZodiacBuddy.Stages.Brave.Automation;

/// <summary>
///     Automates the Zodiac Zeta mahatma grind: buys the next available mahatma
///     from Remon at Swiftperch for Allagan tomestones of poetics whenever none is
///     attached or the attached one is fully awakened, and charges the attached
///     mahatma by running The Bowl of Embers unsynced through AutoDuty.
/// </summary>
internal sealed class ZetaAutomationManager : IDisposable
{
    /// <summary>
    ///     Charge value of a fully awakened mahatma. The relic's spiritbond holds
    ///     the mahatma index in steps of 500 and the current mahatma's charge in
    ///     the remainder: +1 for attaching it and +2 per point of light, full at
    ///     the 40 points shown in game.
    /// </summary>
    private const int FullCharge = 80;

    private const int MahatmaBand = 500;
    private const int MahatmaTotal = 12;

    /// <summary>
    ///     Territory of the duty used to charge the mahatma: The Bowl of Embers
    ///     (Extreme), run unsynced.
    /// </summary>
    private const uint DutyTerritoryId = 295;
    private const string DutyName = "The Bowl of Embers (Extreme)";

    private const string RemonName = "Remon";
    private const uint RemonTerritoryId = 138;
    private const uint DismountActionId = 23;

    /// <summary>
    ///     Currency each mahatma is exchanged for: 50 Allagan tomestones of poetics.
    /// </summary>
    private const uint PoeticsItemId = 28;
    private const int MahatmaCost = 50;

    /// <summary>
    ///     Entry of Remon's greeting menu that opens the mahatma exchange.
    /// </summary>
    private const string ExchangeMenuEntry = "Mahatma Exchange";

    /// <summary>
    ///     Marker Remon's mahatma list puts after the one that can be attached
    ///     next; the awakened ones are marked "(Complete)" instead.
    /// </summary>
    private const string AvailableMarker = "(Available)";

    /// <summary>
    ///     Distinctive part of each mahatma's name ("Mahatma of the ..."), in
    ///     order. Remon's menu lists already awakened mahatmas too, so the entry
    ///     is picked by this keyword rather than by the first "mahatma" match.
    /// </summary>
    private static readonly string[] MahatmaKeywords =
    [
        "Ram",
        "Bull",
        "Twins",
        "Crab",
        "Lion",
        "Maiden",
        "Scales",
        "Scorpion",
        "Archer",
        "Goat",
        "Water-bearer",
        "Fish",
    ];

    private readonly NavmeshIpc navmesh;
    private readonly AutoDutyIpc autoDuty;

    private int relicSlot;
    private uint relicItemId;
    private string relicName = string.Empty;
    private string nextMahatmaKeyword = string.Empty;

    private List<uint> aetheryteCandidates = [];
    private Vector3 destination;
    private uint spiritbondBeforePurchase;
    private int chargeAtRunStart;
    private int noProgressRuns;
    private int teleportAttempts;
    private uint teleportStartTerritory;
    private bool sawZoning;
    private DateTime lastTeleportAt;

    private DateTime stateEnteredAt;
    private bool stateEntered;

    /// <summary>
    ///     Initializes a new instance of the <see cref="ZetaAutomationManager" /> class.
    /// </summary>
    public ZetaAutomationManager()
    {
        this.navmesh = new NavmeshIpc();
        this.autoDuty = new AutoDutyIpc();
        Service.Framework.Update += this.OnUpdate;
    }

    /// <summary>
    ///     Gets the current state of the automation.
    /// </summary>
    public ZetaAutomationState State { get; private set; } = ZetaAutomationState.Idle;

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
    public bool IsRunning => this.State is not (ZetaAutomationState.Idle or ZetaAutomationState.Completed or ZetaAutomationState.Errored);

    private static MapLinkPayload RemonMapLink => new(RemonTerritoryId, 18, 34.3f, 31.7f);

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

        if (TryGetRelicSlot(out _) is null)
        {
            reason = "No Zodiac weapon is equipped.";
            return false;
        }

        if (!NavmeshIpc.IsInstalled)
        {
            reason = "The vnavmesh plugin is not installed.";
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
    ///     Start the mahatma automation for the equipped Zodiac weapon.
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
            Service.PluginLog.Warning($"[ZetaAutomation] Cannot start: {reason}");
            return;
        }

        var slot = TryGetRelicSlot(out var itemId)!.Value;
        this.relicSlot = slot;
        this.relicItemId = itemId;
        this.relicName = BraveRelic.Items[itemId];
        this.LastError = string.Empty;
        this.noProgressRuns = 0;
        Log($"Starting the mahatma automation for {this.relicName}.");
        this.TransitionTo(ZetaAutomationState.DecidingNextStep);
    }

    /// <summary>
    ///     Stop the automation and the current AutoDuty run.
    /// </summary>
    /// <param name="reason">Reason written to the log.</param>
    public void Stop(string reason)
    {
        this.navmesh.Stop();
        this.autoDuty.Stop();
        this.State = ZetaAutomationState.Idle;
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
        Service.PluginLog.Information($"[ZetaAutomation] {message}");
    }

    /// <summary>
    ///     Find the equipment slot holding a Zodiac (Zodiac Brave) weapon.
    /// </summary>
    /// <param name="itemId">Item ID of the found relic.</param>
    /// <returns>The slot, or null when no relic is equipped.</returns>
    private static int? TryGetRelicSlot(out uint itemId)
    {
        foreach (var slot in new[] { 0, 1 })
        {
            var item = Util.GetEquippedItem(slot);
            if (BraveRelic.Items.ContainsKey(item.ItemId))
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
                case ZetaAutomationState.DecidingNextStep:
                    this.HandleDecidingNextStep();
                    break;
                case ZetaAutomationState.Teleporting:
                    this.HandleTeleporting();
                    break;
                case ZetaAutomationState.WaitingForNavmesh:
                    this.HandleWaitingForNavmesh();
                    break;
                case ZetaAutomationState.NavigatingToRemon:
                    this.HandleNavigatingToRemon();
                    break;
                case ZetaAutomationState.TalkingToRemon:
                    this.HandleTalkingToRemon();
                    break;
                case ZetaAutomationState.StartingDuty:
                    this.HandleStartingDuty();
                    break;
                case ZetaAutomationState.RunningDuty:
                    this.HandleRunningDuty();
                    break;
            }
        }
        catch (Exception ex)
        {
            Service.PluginLog.Error(ex, "Exception during Zodiac Zeta automation.");
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

        // Zoning is expected while teleporting and while AutoDuty enters and
        // leaves the duty; the inventory and player are unavailable during it.
        if (Service.Condition[ConditionFlag.BetweenAreas] || Service.Condition[ConditionFlag.BetweenAreas51])
        {
            if (this.State == ZetaAutomationState.Teleporting)
            {
                this.sawZoning = true;
            }

            return false;
        }

        var player = Service.ObjectTable.LocalPlayer;
        if (player is null)
        {
            return false;
        }

        // Inside the duty AutoDuty recovers from deaths on its own.
        if (player.IsDead
            && this.State is not (ZetaAutomationState.StartingDuty or ZetaAutomationState.RunningDuty))
        {
            this.Fail("You died. Automation stopped.");
            return false;
        }

        if (Util.GetEquippedItem(this.relicSlot).ItemId != this.relicItemId)
        {
            this.Fail($"{this.relicName} is no longer equipped. Keep the Zodiac weapon equipped.");
            return false;
        }

        return true;
    }

    /// <summary>
    ///     Get how many Allagan tomestones of poetics the player is carrying.
    /// </summary>
    /// <returns>The tomestone count.</returns>
    private static unsafe int GetPoeticsCount()
    {
        var inventory = InventoryManager.Instance();
        return inventory == null
            ? 0
            : inventory->GetItemCountInContainer(PoeticsItemId, InventoryType.Currency);
    }

    /// <summary>
    ///     Get the relic's raw mahatma progress.
    /// </summary>
    /// <returns>Index of the current mahatma, its charge within the current band
    ///     and the raw spiritbond value.</returns>
    private (int Index, int Charge, uint Raw) ReadProgress()
    {
        var raw = Util.GetEquippedItem(this.relicSlot).SpiritbondOrCollectability;
        return ((int)(raw / MahatmaBand), (int)(raw % MahatmaBand), raw);
    }

    private void HandleDecidingNextStep()
    {
        var (index, charge, raw) = this.ReadProgress();

        // No mahatma attached yet, or the attached one is fully awakened.
        if (raw == 0 || charge >= FullCharge)
        {
            if (raw != 0 && index >= MahatmaTotal - 1)
            {
                this.navmesh.Stop();
                this.State = ZetaAutomationState.Completed;
                this.StatusDetail = "All 12 mahatmas are awakened! Speak with Jalzahn to receive your Zeta weapon.";
                Log("All 12 mahatmas are awakened!");
                return;
            }

            var next = raw == 0 ? 0 : index + 1;
            this.nextMahatmaKeyword = MahatmaKeywords[next];
            this.StatusDetail = $"Buying the Mahatma of the {this.nextMahatmaKeyword}...";

            // Remon would refuse the exchange and we would loop through his
            // menus until the timeout; say so before making the trip. The
            // charging duty grants no poetics, so waiting would not help.
            var poetics = GetPoeticsCount();
            if (poetics < MahatmaCost)
            {
                this.Fail($"Not enough Allagan tomestones of poetics for the Mahatma of the " +
                          $"{this.nextMahatmaKeyword}: {poetics}/{MahatmaCost}.");
                return;
            }

            var player = Service.ObjectTable.LocalPlayer!;
            if (Service.ClientState.TerritoryType == RemonTerritoryId
                && Vector2.Distance(
                    new Vector2(player.Position.X, player.Position.Z),
                    this.RemonWorldPositionXZ()) < 150f)
            {
                this.TransitionTo(ZetaAutomationState.WaitingForNavmesh);
                return;
            }

            this.aetheryteCandidates = AtmaManager.GetAetherytesByDistance(RemonMapLink);
            if (this.aetheryteCandidates.Count == 0)
            {
                this.Fail("No aetheryte found in Western La Noscea.");
                return;
            }

            this.TransitionTo(ZetaAutomationState.Teleporting);
            return;
        }

        this.TransitionTo(ZetaAutomationState.StartingDuty);
    }

    private void HandleTeleporting()
    {
        this.StatusDetail = "Teleporting to Swiftperch...";

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

        if (Service.ClientState.TerritoryType == RemonTerritoryId
            && (this.sawZoning || this.teleportStartTerritory != RemonTerritoryId))
        {
            this.TransitionTo(ZetaAutomationState.WaitingForNavmesh);
            return;
        }

        if (Service.Condition[ConditionFlag.Casting])
        {
            return;
        }

        // Issue the teleport; if we still haven't landed (or started casting or
        // zoning) after a while, the cast silently fizzled - retry it.
        if (DateTime.UtcNow - this.lastTeleportAt > TimeSpan.FromSeconds(15))
        {
            if (++this.teleportAttempts > 5)
            {
                this.Fail("Teleport to Swiftperch failed repeatedly.");
                return;
            }

            var aetheryteId = this.aetheryteCandidates[0];
            if (!AtmaManager.ExecuteTeleport(aetheryteId))
            {
                this.Fail("Could not teleport to Swiftperch.");
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
            this.TransitionTo(ZetaAutomationState.NavigatingToRemon);
            return;
        }

        if (this.StateAge > TimeSpan.FromSeconds(180))
        {
            this.Fail("The navmesh did not become ready in time.");
        }
    }

    private void HandleNavigatingToRemon()
    {
        this.StatusDetail = $"Traveling to {RemonName}...";
        var player = Service.ObjectTable.LocalPlayer!;

        if (!this.stateEntered)
        {
            var xz = this.RemonWorldPositionXZ();
            var floor = this.navmesh.FindNavigablePoint(new Vector3(xz.X, 0, xz.Y));
            if (floor is null)
            {
                this.Fail($"No navigable point found near {RemonName}.");
                return;
            }

            this.destination = floor.Value;
            this.navmesh.SetTolerance(0.5f);
            if (!this.navmesh.PathfindAndMoveTo(this.destination))
            {
                this.Fail("vnavmesh could not start pathfinding.");
                return;
            }

            this.stateEntered = true;
            return;
        }

        // Once Remon is loaded, the talking state approaches him precisely.
        if (ZetaGameActions.FindNpcByName(RemonName) is not null
            || (Vector3.Distance(player.Position, this.destination) <= 3f && !this.navmesh.IsPathRunning))
        {
            this.TransitionTo(ZetaAutomationState.TalkingToRemon);
            return;
        }

        if (!this.navmesh.IsPathRunning && !this.navmesh.IsPathfindInProgress
            && EzThrottler.Throttle("ZodiacBuddy.ZetaAuto.Repath", 2000))
        {
            this.navmesh.PathfindAndMoveTo(this.destination);
        }

        if (this.StateAge > TimeSpan.FromSeconds(180))
        {
            this.Fail($"Could not reach {RemonName} in time.");
        }
    }

    private void HandleTalkingToRemon()
    {
        if (!this.stateEntered)
        {
            this.spiritbondBeforePurchase = this.ReadProgress().Raw;
            this.stateEntered = true;
        }

        // The purchase is visible on the relic immediately: the spiritbond moves
        // to the next band (or from 0 to 1) and the new mahatma starts uncharged.
        var (index, charge, raw) = this.ReadProgress();
        if (raw != this.spiritbondBeforePurchase && charge < FullCharge)
        {
            Log($"Attached the Mahatma of the {MahatmaKeywords[Math.Min(index, MahatmaTotal - 1)]}.");

            // Remon returns to his mahatma list after the purchase; close it so
            // nothing blocks the duty queue.
            ZetaGameActions.CloseMenu();
            this.TransitionTo(ZetaAutomationState.DecidingNextStep);
            return;
        }

        this.StatusDetail = $"Talking to {RemonName}...";

        var npc = ZetaGameActions.FindNpcByName(RemonName);
        if (npc is null)
        {
            if (!this.navmesh.IsPathRunning && !this.navmesh.IsPathfindInProgress
                && EzThrottler.Throttle("ZodiacBuddy.ZetaAuto.NpcApproach", 2000))
            {
                this.navmesh.PathfindAndMoveCloseTo(this.destination, 2f);
            }
        }
        else if (!FateGameActions.IsInInteractRange(npc))
        {
            if (!this.navmesh.IsPathRunning && !this.navmesh.IsPathfindInProgress
                && EzThrottler.Throttle("ZodiacBuddy.ZetaAuto.NpcApproach", 1000))
            {
                this.navmesh.PathfindAndMoveCloseTo(npc.Position, 2f);
            }
        }
        else
        {
            this.navmesh.Stop();

            if (Service.Condition[ConditionFlag.Mounted])
            {
                if (EzThrottler.Throttle("ZodiacBuddy.ZetaAuto.Dismount", 500))
                {
                    AtmaAutomationManager.TryUseGeneralAction(DismountActionId);
                }

                return;
            }

            // Click Remon only while no window of his is up, so an interact can
            // never close the menu we are working through.
            if (!ZetaGameActions.IsDialogOpen()
                && EzThrottler.Throttle("ZodiacBuddy.ZetaAuto.Interact", 2000))
            {
                FateGameActions.InteractWith(npc);
            }

            if (EzThrottler.Throttle("ZodiacBuddy.ZetaAuto.Dialogue", 250))
            {
                this.DriveRemonDialog();
            }
        }

        if (this.StateAge > TimeSpan.FromSeconds(90))
        {
            var poetics = GetPoeticsCount();
            this.Fail(poetics < MahatmaCost
                ? $"Not enough Allagan tomestones of poetics for the Mahatma of the " +
                  $"{this.nextMahatmaKeyword}: {poetics}/{MahatmaCost}."
                : $"Could not buy the Mahatma of the {this.nextMahatmaKeyword} from {RemonName}.");
        }
    }

    /// <summary>
    ///     Walk one step of Remon's mahatma exchange each call: advance his line
    ///     of dialogue, then pick the entry that leads deeper into the exchange -
    ///     "Mahatma Exchange" in the greeting menu, the equipped relic in the
    ///     weapon menu, the "(Available)" mahatma in the full list - and finally
    ///     confirm the tomestone prompt. The needles are ordered most specific
    ///     first, so only one entry of the open menu can ever match.
    /// </summary>
    private void DriveRemonDialog()
    {
        FateGameActions.ProgressTalk();

        var selected = ZetaGameActions.SelectDialogEntry(
            AvailableMarker,
            ExchangeMenuEntry,
            this.relicName,
            this.nextMahatmaKeyword);

        if (selected is not null)
        {
            Service.PluginLog.Debug($"[ZetaAutomation] Selected \"{selected}\".");
        }
        else if (EzThrottler.Throttle("ZodiacBuddy.ZetaAuto.EntryDump", 5000))
        {
            var entries = ZetaGameActions.GetOpenMenuEntries();
            if (entries.Count > 0)
            {
                Service.PluginLog.Debug($"[ZetaAutomation] No entry matched in: {string.Join(" | ", entries)}");
            }
        }

        ZetaGameActions.ConfirmPurchaseYesNo();
    }

    private void HandleStartingDuty()
    {
        var (_, charge, _) = this.ReadProgress();
        this.StatusDetail = $"Starting {DutyName} ({charge / 2}/40)...";

        // Close any dialogue left over from the purchase before queuing.
        if (FateGameActions.ProgressTalk() || ZetaGameActions.CloseMenu())
        {
            return;
        }

        if (!this.stateEntered)
        {
            // AutoDuty juggles its own config for a moment after a run ends;
            // probing HasPath during that window can wrongly report no path.
            if (!this.autoDuty.IsStopped() || this.StateAge < TimeSpan.FromSeconds(2))
            {
                return;
            }

            if (!this.autoDuty.HasPath(DutyTerritoryId))
            {
                if (this.StateAge > TimeSpan.FromSeconds(12))
                {
                    this.Fail($"AutoDuty has no path for {DutyName}.");
                }

                return;
            }

            if (!this.autoDuty.RunUnsynced(DutyTerritoryId))
            {
                this.Fail($"Could not start an AutoDuty run of {DutyName}.");
                return;
            }

            Log($"Running {DutyName} unsynced through AutoDuty ({charge / 2}/40).");
            this.chargeAtRunStart = charge;
            this.stateEntered = true;
            return;
        }

        // Once AutoDuty reports it is busy, the run has begun.
        if (!this.autoDuty.IsStopped())
        {
            this.TransitionTo(ZetaAutomationState.RunningDuty);
            return;
        }

        if (this.StateAge > TimeSpan.FromSeconds(60))
        {
            this.Fail($"AutoDuty did not start {DutyName} in time.");
        }
    }

    private void HandleRunningDuty()
    {
        var (_, charge, _) = this.ReadProgress();
        this.StatusDetail = $"Running {DutyName} ({charge / 2}/40)...";

        // Only move on once AutoDuty has fully stopped, so we never issue the
        // next run or start traveling while it is still exiting the duty.
        if (!this.autoDuty.IsStopped())
        {
            if (this.StateAge > TimeSpan.FromMinutes(30))
            {
                this.Fail($"The {DutyName} run did not finish in time.");
            }

            return;
        }

        if (charge <= this.chargeAtRunStart)
        {
            if (++this.noProgressRuns >= 2)
            {
                this.Fail($"Runs of {DutyName} grant no light. Complete the duty once manually to check.");
                return;
            }

            Log($"The run granted no light; retrying ({this.noProgressRuns}/2).");
        }
        else
        {
            this.noProgressRuns = 0;
        }

        this.TransitionTo(ZetaAutomationState.DecidingNextStep);
    }

    private Vector2 RemonWorldPositionXZ()
    {
        var mapLink = RemonMapLink;
        var map = Service.DataManager.GetExcelSheet<Map>().GetRow(mapLink.Map.RowId);
        return new Vector2(
            AtmaAutomationManager.MapToWorld(mapLink.XCoord, map.SizeFactor, map.OffsetX),
            AtmaAutomationManager.MapToWorld(mapLink.YCoord, map.SizeFactor, map.OffsetY));
    }

    private void TransitionTo(ZetaAutomationState state)
    {
        this.State = state;
        this.stateEnteredAt = DateTime.UtcNow;
        this.stateEntered = false;
    }

    private void Fail(string reason)
    {
        this.navmesh.Stop();
        this.autoDuty.Stop();
        this.State = ZetaAutomationState.Errored;
        this.StatusDetail = string.Empty;
        this.LastError = reason;
        Service.PluginLog.Warning($"[ZetaAutomation] Stopped: {reason}");
    }
}
