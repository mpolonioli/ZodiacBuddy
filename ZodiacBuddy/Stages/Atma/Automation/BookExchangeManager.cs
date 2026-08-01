using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Plugin.Services;
using ECommons.Throttlers;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;
using System.Numerics;
using ZodiacBuddy.Stages.Atma.Data;
using RelicNote = FFXIVClientStructs.FFXIV.Client.Game.UI.RelicNote;

namespace ZodiacBuddy.Stages.Atma.Automation;

/// <summary>
///     Takes a new Trial of the Braves book from G'jusana in Mor Dhona once the
///     current one is completed: travels to her, opens the Trials of the Braves
///     exchange and picks the first book category that still has books left to
///     complete, paying the Allagan tomestones of poetics she asks for.
/// </summary>
internal sealed class BookExchangeManager : IDisposable
{
    /// <summary>
    ///     Currency a book is exchanged for: 100 Allagan tomestones of poetics.
    /// </summary>
    private const uint PoeticsItemId = 28;
    private const int BookCost = 100;

    private const string NpcName = "G'jusana";
    private const uint NpcTerritoryId = 156;
    private const uint DismountActionId = 23;

    /// <summary>
    ///     Distinctive part of the entry of G'jusana's greeting menu that opens the
    ///     book exchange ("Trials of the Braves Exchange").
    /// </summary>
    private const string ExchangeMenuEntry = "Braves";

    /// <summary>
    ///     How long the character has to stay out of the exchange event, with no
    ///     window of G'jusana's open, before the next automation may take over.
    /// </summary>
    private static readonly TimeSpan DialogueSettleTime = TimeSpan.FromSeconds(2);

    private readonly NavmeshIpc navmesh;
    private readonly BookTravelManager bookTravel;

    private uint bookIdBeforeExchange;
    private string takenCategory = string.Empty;

    private List<uint> aetheryteCandidates = [];
    private Vector3 destination;
    private int teleportAttempts;
    private int travelAttempts;
    private uint teleportStartTerritory;
    private bool sawZoning;
    private DateTime lastTeleportAt;

    private DateTime? dialogueClearSince;

    private DateTime stateEnteredAt;
    private bool stateEntered;

    /// <summary>
    ///     Initializes a new instance of the <see cref="BookExchangeManager" /> class.
    /// </summary>
    /// <param name="bookTravel">Shared travel helper, which mounts, flies and
    ///     recovers from getting stuck on the way to the exchange NPC.</param>
    public BookExchangeManager(BookTravelManager bookTravel)
    {
        this.navmesh = new NavmeshIpc();
        this.bookTravel = bookTravel;
        Service.Framework.Update += this.OnUpdate;
    }

    /// <summary>
    ///     Gets the current state of the automation.
    /// </summary>
    public BookExchangeState State { get; private set; } = BookExchangeState.Idle;

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
    public bool IsRunning => this.State is not (BookExchangeState.Idle or BookExchangeState.Completed or BookExchangeState.Errored);

    /// <summary>
    ///     Gets a value indicating whether the automation took a new book.
    /// </summary>
    public bool IsCompleted => this.State == BookExchangeState.Completed;

    private static MapLinkPayload NpcMapLink => new(NpcTerritoryId, 25, 22.9f, 7.3f);

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

        if (!NavmeshIpc.IsInstalled)
        {
            reason = "The vnavmesh plugin is not installed.";
            return false;
        }

        var poetics = GetPoeticsCount();
        if (poetics < BookCost)
        {
            reason = $"Not enough Allagan tomestones of poetics for a new book: {poetics}/{BookCost}.";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    /// <summary>
    ///     Travel to G'jusana and take a new Trial of the Braves book.
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
            Service.PluginLog.Warning($"[BookExchange] Cannot start: {reason}");
            return;
        }

        this.bookIdBeforeExchange = AtmaAutomationManager.GetActiveBookId();
        this.takenCategory = string.Empty;
        this.travelAttempts = 0;
        this.LastError = string.Empty;
        Log($"Taking a new Trial of the Braves book from {NpcName}.");

        // Only a trip that is already short is walked; from anywhere else in Mor
        // Dhona the aetheryte is still the quicker way in.
        var player = Service.ObjectTable.LocalPlayer!;
        if (Service.ClientState.TerritoryType == NpcTerritoryId
            && Vector2.Distance(
                new Vector2(player.Position.X, player.Position.Z),
                this.NpcWorldPositionXZ()) < 150f)
        {
            this.TransitionTo(BookExchangeState.Traveling);
            return;
        }

        this.aetheryteCandidates = AtmaManager.GetAetherytesByDistance(NpcMapLink);
        if (this.aetheryteCandidates.Count == 0)
        {
            this.Fail("No aetheryte found in Mor Dhona.");
            return;
        }

        this.TransitionTo(BookExchangeState.Teleporting);
    }

    /// <summary>
    ///     Stop the automation.
    /// </summary>
    /// <param name="reason">Reason written to the log.</param>
    public void Stop(string reason)
    {
        this.navmesh.Stop();
        this.bookTravel.Cancel(reason);
        this.State = BookExchangeState.Idle;
        this.StatusDetail = string.Empty;
        Log($"Book exchange stopped: {reason}");
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Service.Framework.Update -= this.OnUpdate;
    }

    private static void Log(string message)
    {
        Service.PluginLog.Information($"[BookExchange] {message}");
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
    ///     Check whether a new book was taken, which the game shows immediately on
    ///     the relic note.
    /// </summary>
    /// <returns>Whether a book other than the one we came with is active.</returns>
    private unsafe bool HasNewBook()
    {
        var relicNote = RelicNote.Instance();
        return relicNote != null
               && relicNote->RelicNoteId != 0
               && relicNote->RelicNoteId != this.bookIdBeforeExchange;
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
                case BookExchangeState.Teleporting:
                    this.HandleTeleporting();
                    break;
                case BookExchangeState.Traveling:
                    this.HandleTraveling();
                    break;
                case BookExchangeState.TalkingToNpc:
                    this.HandleTalkingToNpc();
                    break;
                case BookExchangeState.DismissingDialogue:
                    this.HandleDismissingDialogue();
                    break;
            }
        }
        catch (Exception ex)
        {
            Service.PluginLog.Error(ex, "Exception during the Trial of the Braves book exchange.");
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

        // Zoning is expected while teleporting; the player is unavailable during it.
        if (Service.Condition[ConditionFlag.BetweenAreas] || Service.Condition[ConditionFlag.BetweenAreas51])
        {
            if (this.State == BookExchangeState.Teleporting)
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

        if (player.IsDead)
        {
            this.Fail("You died. Book exchange stopped.");
            return false;
        }

        return true;
    }

    private void HandleTeleporting()
    {
        this.StatusDetail = "Teleporting to Mor Dhona...";

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

        if (Service.ClientState.TerritoryType == NpcTerritoryId
            && (this.sawZoning || this.teleportStartTerritory != NpcTerritoryId))
        {
            this.TransitionTo(BookExchangeState.Traveling);
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
                this.Fail("Teleport to Mor Dhona failed repeatedly.");
                return;
            }

            if (!AtmaManager.ExecuteTeleport(this.aetheryteCandidates[0]))
            {
                this.Fail("Could not teleport to Mor Dhona.");
                return;
            }

            this.lastTeleportAt = DateTime.UtcNow;
        }
    }

    /// <summary>
    ///     Hand the trip to G'jusana over to the shared travel helper, which waits
    ///     for the navmesh, mounts, flies when the zone allows it, lands at the end
    ///     and recovers from getting stuck - demoting a jammed flying route to the
    ///     ground - instead of walking a bare path there.
    /// </summary>
    private void HandleTraveling()
    {
        this.StatusDetail = $"Traveling to {NpcName}...";

        if (!this.stateEntered)
        {
            // The teleport, when there was one, has already landed by now.
            this.bookTravel.Start(CreateTravelTarget(), waitForTeleport: false);
            this.stateEntered = true;
            return;
        }

        if (this.bookTravel.IsRunning)
        {
            if (this.StateAge > TimeSpan.FromSeconds(420))
            {
                this.Fail($"Could not reach {NpcName} in time.");
            }

            return;
        }

        if (!this.bookTravel.Arrived)
        {
            // The helper gives the travel up on combat as well as on a route it
            // cannot walk; wait the fight out and set off again rather than
            // ending the whole run over a mob that aggroed on the way.
            if (Service.Condition[ConditionFlag.InCombat])
            {
                this.StatusDetail = "Waiting for combat to end...";
                return;
            }

            if (++this.travelAttempts > 2)
            {
                this.Fail($"Could not travel to {NpcName}, see /xllog for details.");
                return;
            }

            Log($"The travel to {NpcName} was given up on; setting off again ({this.travelAttempts}/2).");
            this.stateEntered = false;
            return;
        }

        this.TransitionTo(BookExchangeState.TalkingToNpc);
    }

    /// <summary>
    ///     Describe G'jusana as a book target, so the shared travel helper can take
    ///     the character to her the same way it does for a clicked book entry.
    /// </summary>
    /// <returns>The travel target.</returns>
    private static BraveTarget CreateTravelTarget()
    {
        var mapLink = NpcMapLink;
        return new BraveTarget
        {
            Name = NpcName,
            ZoneName = mapLink.TerritoryType.Value.PlaceName.Value.Name.ToString(),
            ZoneId = mapLink.TerritoryType.RowId,
            LocationName = string.Empty,
            Position = mapLink,
        };
    }

    private void HandleTalkingToNpc()
    {
        if (this.HasNewBook())
        {
            this.navmesh.Stop();
            Log(this.takenCategory.Length > 0
                ? $"Took a new book from \"{this.takenCategory.ReplaceLineEndings(" ")}\"."
                : "Took a new book.");
            this.TransitionTo(BookExchangeState.DismissingDialogue);
            return;
        }

        this.StatusDetail = $"Talking to {NpcName}...";

        if (!this.stateEntered)
        {
            // Where to stand when G'jusana herself is not loaded yet.
            var xz = this.NpcWorldPositionXZ();
            var approximate = new Vector3(xz.X, Service.ObjectTable.LocalPlayer!.Position.Y, xz.Y);
            this.destination = this.navmesh.FindNavigablePoint(new Vector3(xz.X, 0, xz.Y)) ?? approximate;
            this.stateEntered = true;
        }

        // A flying approach ends hovering over the NPC; land before interacting,
        // and path in the air while still up there - the ground mesh has no start
        // point mid-air, so a ground pathfind from there silently finds nothing.
        if (AtmaAutomationManager.LandIfHovering(this.navmesh, "ZodiacBuddy.BookExchange.Land"))
        {
            return;
        }

        var fly = Service.Condition[ConditionFlag.InFlight];
        var npc = BookExchangeGameActions.FindNpcByName(NpcName);
        if (npc is null)
        {
            if (!this.navmesh.IsPathRunning && !this.navmesh.IsPathfindInProgress
                && EzThrottler.Throttle("ZodiacBuddy.BookExchange.NpcApproach", 2000))
            {
                this.navmesh.PathfindAndMoveCloseTo(this.destination, 2f, fly);
            }
        }
        else if (!FateGameActions.IsInInteractRange(npc))
        {
            if (!this.navmesh.IsPathRunning && !this.navmesh.IsPathfindInProgress
                && EzThrottler.Throttle("ZodiacBuddy.BookExchange.NpcApproach", 1000))
            {
                this.navmesh.PathfindAndMoveCloseTo(npc.Position, 2f, fly);
            }
        }
        else
        {
            this.navmesh.Stop();

            if (Service.Condition[ConditionFlag.Mounted])
            {
                if (EzThrottler.Throttle("ZodiacBuddy.BookExchange.Dismount", 500))
                {
                    AtmaAutomationManager.TryUseGeneralAction(DismountActionId);
                }

                return;
            }

            // Click G'jusana only while no window of hers is up, so an interact
            // can never close the menu we are working through.
            if (!BookExchangeGameActions.IsDialogOpen()
                && EzThrottler.Throttle("ZodiacBuddy.BookExchange.Interact", 2000))
            {
                FateGameActions.InteractWith(npc);
            }

            if (EzThrottler.Throttle("ZodiacBuddy.BookExchange.Dialogue", 250))
            {
                this.DriveExchangeDialog();
            }
        }

        if (this.StateAge > TimeSpan.FromSeconds(90))
        {
            var poetics = GetPoeticsCount();
            this.Fail(poetics < BookCost
                ? $"Not enough Allagan tomestones of poetics for a new book: {poetics}/{BookCost}."
                : $"Could not take a new book from {NpcName}.");
        }
    }

    /// <summary>
    ///     Walk one step of the book exchange each call: advance the line of
    ///     dialogue, then pick the entry that leads deeper into the exchange - the
    ///     first category with books left to complete in the category list, the
    ///     "Trials of the Braves Exchange" entry in the greeting menu - and
    ///     finally confirm the tomestone prompt.
    /// </summary>
    private void DriveExchangeDialog()
    {
        FateGameActions.ProgressTalk();

        // The category list is answered first: its entries are the only ones the
        // completion counts can be read from, so the greeting needle can never
        // match one of them by accident.
        var selected = BookExchangeGameActions.SelectIncompleteCategory(out var anyCategoryListed);
        if (selected is null)
        {
            if (anyCategoryListed)
            {
                this.Fail("Every Trial of the Braves book is already completed.");
                return;
            }

            selected = BookExchangeGameActions.SelectMenuEntry(ExchangeMenuEntry);
        }
        else
        {
            this.takenCategory = selected;
        }

        if (selected is not null)
        {
            Service.PluginLog.Debug($"[BookExchange] Selected \"{selected.ReplaceLineEndings(" ")}\".");
        }
        else if (EzThrottler.Throttle("ZodiacBuddy.BookExchange.EntryDump", 5000))
        {
            var entries = BookExchangeGameActions.GetOpenMenuEntryTexts();
            if (entries.Count > 0)
            {
                Service.PluginLog.Debug($"[BookExchange] No entry matched in: {string.Join(" | ", entries)}");
            }
        }

        BookExchangeGameActions.ConfirmExchangeYesNo();
    }

    private void HandleDismissingDialogue()
    {
        this.StatusDetail = "Dismissing the remaining dialogue...";

        // G'jusana's parting line only opens a moment after the book lands, so
        // nothing being up on the tick we get here does not mean the exchange is
        // over. The character stays flagged as occupied for the whole event, and
        // a teleport cast is refused while it lasts - which is what stranded the
        // next automation - so wait for that flag to clear and hold it clear for
        // a moment before handing over.
        if (Service.Condition[ConditionFlag.OccupiedInQuestEvent] || BookExchangeGameActions.IsDialogOpen())
        {
            this.dialogueClearSince = null;

            if (EzThrottler.Throttle("ZodiacBuddy.BookExchange.Dismiss", 250))
            {
                // The exchange ends on the parting line and the category list she
                // returns to; leave neither open.
                if (!FateGameActions.ProgressTalk())
                {
                    BookExchangeGameActions.CloseMenu();
                }
            }
        }
        else
        {
            this.dialogueClearSince ??= DateTime.UtcNow;
            if (DateTime.UtcNow - this.dialogueClearSince > DialogueSettleTime)
            {
                this.Complete();
                return;
            }
        }

        // Some dialogue keeps reopening (a quest offer, a window we do not know);
        // the book is ours either way, so do not fail over it.
        if (this.StateAge > TimeSpan.FromSeconds(30))
        {
            Log("Some dialogue is still open after the exchange; continuing anyway.");
            this.Complete();
        }
    }

    private Vector2 NpcWorldPositionXZ()
    {
        var mapLink = NpcMapLink;
        var map = Service.DataManager.GetExcelSheet<Map>().GetRow(mapLink.Map.RowId);
        return new Vector2(
            AtmaAutomationManager.MapToWorld(mapLink.XCoord, map.SizeFactor, map.OffsetX),
            AtmaAutomationManager.MapToWorld(mapLink.YCoord, map.SizeFactor, map.OffsetY));
    }

    private void TransitionTo(BookExchangeState state)
    {
        this.State = state;
        this.stateEnteredAt = DateTime.UtcNow;
        this.stateEntered = false;
        this.dialogueClearSince = null;
    }

    private void Complete()
    {
        this.navmesh.Stop();
        this.State = BookExchangeState.Completed;
        this.StatusDetail = string.Empty;
        Log("A new Trial of the Braves book is active.");
    }

    private void Fail(string reason)
    {
        this.navmesh.Stop();
        this.bookTravel.Cancel(reason);
        this.State = BookExchangeState.Errored;
        this.StatusDetail = string.Empty;
        this.LastError = reason;
        Service.PluginLog.Warning($"[BookExchange] Stopped: {reason}");
    }
}
