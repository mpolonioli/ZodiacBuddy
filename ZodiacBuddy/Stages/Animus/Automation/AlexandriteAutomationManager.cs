using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using ECommons.Throttlers;
using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;
using System.Numerics;
using ZodiacBuddy.Stages.Atma;
using ZodiacBuddy.Stages.Atma.Automation;
using ZodiacBuddy.Stages.Atma.Data;

namespace ZodiacBuddy.Stages.Animus.Automation;

/// <summary>
///     Automates the Alexandrite grind of the Animus stage: buys mysterious maps
///     from Auriana in Mor Dhona for Allagan tomestones of poetics, deciphers
///     them, travels to the spot the game itself marks, digs the treasure coffer
///     up, kills what comes out of it and loots it, over and over until the
///     configured number of maps is farmed. Two maps fit in the bag at once - one
///     deciphered and one still sealed - so every trip to the vendor buys both.
/// </summary>
internal sealed class AlexandriteAutomationManager : IDisposable
{
    /// <summary>
    ///     The sealed map bought from the vendor, and the Alexandrite its coffer
    ///     holds five of.
    /// </summary>
    private const uint MysteriousMapItemId = 7884;

    private const uint AlexandriteItemId = 7883;

    /// <summary>
    ///     Treasure hunt rank of the mysterious map, as its item data points at.
    ///     The deciphered map reports this rank, which is how a map of ours is
    ///     told from any other one the character may be carrying.
    /// </summary>
    private const uint MysteriousMapRank = 6;

    /// <summary>
    ///     Currency a map is exchanged for: 75 Allagan tomestones of poetics.
    /// </summary>
    private const uint PoeticsItemId = 28;

    private const string VendorName = "Auriana";
    private const uint VendorTerritoryId = 156;

    /// <summary>
    ///     Distinctive part of the entry of Auriana's greeting menu that opens the
    ///     map exchange ("Mysterious Map Exchange").
    /// </summary>
    private const string ExchangeMenuEntry = "Mysterious Map";

    /// <summary>
    ///     The Decipher action of the general list. It does not decipher a map by
    ///     itself: it opens the list of maps the character is carrying, and the one
    ///     to open is picked from there.
    /// </summary>
    private const uint DecipherActionId = 19;

    private const uint DigActionId = 20;
    private const uint DismountActionId = 23;

    /// <summary>
    ///     How close to the vendor counts as standing at the exchange, so a map
    ///     can be bought without another teleport.
    /// </summary>
    private const float VendorRange = 50f;

    /// <summary>
    ///     How far from the dig spot the coffer is looked for. It rises out of the
    ///     ground where the shovel went in, but the character may have dug from a
    ///     step away.
    /// </summary>
    private const float CofferSearchRange = 30f;

    /// <summary>
    ///     How close to the coffer, measured on the ground, the character has to
    ///     stand before reaching for it. The game's own range check answers yes
    ///     from further out than the coffer actually opens from, which left the
    ///     character clicking at a chest it could not reach.
    /// </summary>
    private const float CofferInteractRange = 2.5f;

    /// <summary>
    ///     How close to the marked spot, measured on the ground, the character has
    ///     to stand before digging. The height of the spot is where the coffer
    ///     comes out rather than where the character stands, so it is left out of
    ///     the comparison.
    /// </summary>
    private const float DigRange = 3f;

    /// <summary>
    ///     How long the character has to stay out of the vendor's event, with no
    ///     window of hers open, before the run moves on.
    /// </summary>
    private static readonly TimeSpan DialogueSettleTime = TimeSpan.FromSeconds(2);

    private static string? mapName;

    private readonly NavmeshIpc navmesh;
    private readonly CombatIpc combat;
    private readonly BookTravelManager bookTravel;

    private int mapsTarget;
    private int mapsDone;
    private int alexandriteAtStart;
    private int alexandriteAtDig;
    private int mapsBeforePurchase;
    private int digAttempts;
    private int cofferApproaches;

    private Vector3 digPosition;
    private uint digTerritoryId;
    private string digZoneName = string.Empty;
    private MapLinkPayload? digMapLink;

    private Vector3 destination;
    private List<uint> aetheryteCandidates = [];
    private int teleportAttempts;
    private int travelAttempts;
    private uint teleportStartTerritory;
    private bool sawZoning;
    private DateTime lastTeleportAt;

    private DateTime? dialogueClearSince;
    private DateTime? lootedSince;
    private AlexandriteAutomationState stateAfterFight;

    private DateTime stateEnteredAt;
    private bool stateEntered;

    /// <summary>
    ///     Initializes a new instance of the <see cref="AlexandriteAutomationManager" /> class.
    /// </summary>
    /// <param name="bookTravel">Shared travel helper, which mounts, flies and
    ///     recovers from getting stuck on the way to the vendor and the dig spot.</param>
    public AlexandriteAutomationManager(BookTravelManager bookTravel)
    {
        this.navmesh = new NavmeshIpc();
        this.combat = new CombatIpc();
        this.bookTravel = bookTravel;
        this.combat.ControlLost += this.OnControlLost;
        Service.Framework.Update += this.OnUpdate;
    }

    /// <summary>
    ///     Gets the current state of the automation.
    /// </summary>
    public AlexandriteAutomationState State { get; private set; } = AlexandriteAutomationState.Idle;

    /// <summary>
    ///     Gets a human-readable detail about the current state.
    /// </summary>
    public string StatusDetail { get; private set; } = string.Empty;

    /// <summary>
    ///     Gets the last error message, if any.
    /// </summary>
    public string LastError { get; private set; } = string.Empty;

    /// <summary>
    ///     Gets how many maps of the run are farmed.
    /// </summary>
    public int MapsDone => this.mapsDone;

    /// <summary>
    ///     Gets how many maps the run is farming.
    /// </summary>
    public int MapsTarget => this.mapsTarget;

    /// <summary>
    ///     Gets a value indicating whether the automation is currently running.
    /// </summary>
    public bool IsRunning => this.State is not (AlexandriteAutomationState.Idle
        or AlexandriteAutomationState.Completed or AlexandriteAutomationState.Errored);

    private static AnimusConfiguration Configuration => Service.Configuration.Animus;

    /// <summary>
    ///     Gets the name of the mysterious map as the client words it, which is how
    ///     it is picked out of the Decipher list. Read once the game data is up
    ///     rather than when the type is loaded.
    /// </summary>
    private static string MapName
        => mapName ??= ItemUtil.GetItemName(MysteriousMapItemId, false).ToString();

    private static MapLinkPayload VendorMapLink => new(VendorTerritoryId, 25, 22.7f, 6.6f);

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

        if (Service.ObjectTable.LocalPlayer!.IsDead)
        {
            reason = "You are dead.";
            return false;
        }

        if (!NavmeshIpc.IsInstalled)
        {
            reason = "The vnavmesh plugin is not installed.";
            return false;
        }

        if (!CombatIpc.IsAvailable())
        {
            reason = $"The {CombatIpc.ConfiguredName} plugin is not installed or not ready.";
            return false;
        }

        // A map already in the bag is enough to get going; only a run that has to
        // buy its first map needs the tomestones up front.
        if (GetMapsInHand() == 0 && GetPoeticsCount() < AnimusConfiguration.MapCost)
        {
            reason = $"Not enough Allagan tomestones of poetics for a mysterious map: {GetPoeticsCount()}/{AnimusConfiguration.MapCost}.";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    /// <summary>
    ///     Get how much Alexandrite the character is carrying.
    /// </summary>
    /// <returns>The Alexandrite count.</returns>
    public static int GetAlexandriteCount()
        => TreasureMapGameActions.GetItemCount(AlexandriteItemId);

    /// <summary>
    ///     Farm the configured number of mysterious maps.
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
            Service.PluginLog.Warning($"[Alexandrite] Cannot start: {reason}");
            return;
        }

        if (!this.combat.BeginControl())
        {
            this.LastError = $"Could not take control of {CombatIpc.ConfiguredName}.";
            Service.PluginLog.Warning($"[Alexandrite] {this.LastError}");
            return;
        }

        this.bookTravel.Cancel("The Alexandrite automation started.");

        this.mapsTarget = Configuration.GetMapCount();
        this.mapsDone = 0;
        this.alexandriteAtStart = GetAlexandriteCount();
        this.LastError = string.Empty;
        Log($"Farming {this.mapsTarget} mysterious maps ({this.alexandriteAtStart}/{AnimusConfiguration.AlexandriteGoal} Alexandrite).");
        this.TransitionTo(AlexandriteAutomationState.DecidingNextStep);
    }

    /// <summary>
    ///     Stop the automation and release the controlled plugins.
    /// </summary>
    /// <param name="reason">Reason displayed to the user.</param>
    public void Stop(string reason)
    {
        this.Cleanup();
        this.State = AlexandriteAutomationState.Idle;
        this.StatusDetail = string.Empty;
        Log($"Automation stopped: {reason}");
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Service.Framework.Update -= this.OnUpdate;
        this.combat.ControlLost -= this.OnControlLost;
        this.Cleanup();
        this.combat.Dispose();
    }

    private static void Log(string message)
    {
        Service.PluginLog.Information($"[Alexandrite] {message}");
    }

    /// <summary>
    ///     Get how many Allagan tomestones of poetics the player is carrying.
    /// </summary>
    /// <returns>The tomestone count.</returns>
    private static int GetPoeticsCount()
        => TreasureMapGameActions.GetCurrencyCount(PoeticsItemId);

    /// <summary>
    ///     Get how many maps the character is holding: the deciphered one, which
    ///     the game keeps as a key item, and the sealed one in the bag. Both slots
    ///     hold one map at a time, which is what makes the vendor trip worth two
    ///     purchases.
    /// </summary>
    /// <returns>The number of maps in hand, 0 to 2.</returns>
    private static int GetMapsInHand()
        => (TreasureMapGameActions.GetOpenMapRank() != 0 ? 1 : 0)
           + TreasureMapGameActions.GetItemCount(MysteriousMapItemId);

    /// <summary>
    ///     Check whether an NPC event still has the character: a window of the
    ///     vendor's is up, or the game keeps the character flagged as occupied.
    ///     Neither a teleport nor deciphering a map is allowed while it lasts.
    /// </summary>
    /// <returns>Whether an event is still going.</returns>
    private static bool IsEventOngoing()
        => Service.Condition[ConditionFlag.OccupiedInQuestEvent]
           || BookExchangeGameActions.IsDialogOpen()
           || TreasureMapGameActions.IsShopOpen();

    /// <summary>
    ///     Close one thing the vendor left open. The exchange can end on a line of
    ///     dialogue, on the menu she returns to, or on the currency shop window -
    ///     which the menu closer knows nothing about, and which is what left the
    ///     character occupied and unable to decipher.
    /// </summary>
    private static void DismissDialogue()
    {
        if (FateGameActions.ProgressTalk() || BookExchangeGameActions.CloseMenu())
        {
            return;
        }

        TreasureMapGameActions.CloseShop();
    }

    /// <summary>
    ///     Say what is keeping the map from being deciphered, so the log names the
    ///     culprit instead of leaving a bare timeout.
    /// </summary>
    /// <returns>The explanation.</returns>
    private static string DescribeDecipherBlock()
    {
        if (TreasureMapGameActions.IsShopOpen())
        {
            return "The vendor's exchange window would not close.";
        }

        if (BookExchangeGameActions.IsDialogOpen())
        {
            return "A dialogue window is in the way.";
        }

        if (Service.Condition[ConditionFlag.OccupiedInQuestEvent])
        {
            return "The character is still caught in an event.";
        }

        if (Service.Condition[ConditionFlag.Mounted])
        {
            return "The character would not dismount.";
        }

        return "Neither the Decipher list nor the map's right-click menu took; " +
               "see /xllog for what they offered.";
    }

    /// <summary>
    ///     Convert a world coordinate back to the map coordinate the map link and
    ///     the aetheryte search work in, the inverse of
    ///     <see cref="AtmaAutomationManager.MapToWorld" />.
    /// </summary>
    /// <param name="worldCoord">Coordinate in world space.</param>
    /// <param name="sizeFactor">Size factor of the map.</param>
    /// <param name="offset">Offset of the map on that axis.</param>
    /// <returns>The map coordinate.</returns>
    private static float WorldToMap(float worldCoord, float sizeFactor, short offset)
    {
        var c = sizeFactor / 100.0f;
        return (41.0f / c * (((worldCoord + offset) * c) + 1024.0f) / 2048.0f) + 1.0f;
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
                case AlexandriteAutomationState.DecidingNextStep:
                    this.HandleDecidingNextStep();
                    break;
                case AlexandriteAutomationState.TeleportingToVendor:
                    this.HandleTeleporting(VendorTerritoryId, "Mor Dhona", AlexandriteAutomationState.TravelingToVendor);
                    break;
                case AlexandriteAutomationState.TravelingToVendor:
                    this.HandleTravelingToVendor();
                    break;
                case AlexandriteAutomationState.BuyingMap:
                    this.HandleBuyingMap();
                    break;
                case AlexandriteAutomationState.DismissingDialogue:
                    this.HandleDismissingDialogue();
                    break;
                case AlexandriteAutomationState.Deciphering:
                    this.HandleDeciphering();
                    break;
                case AlexandriteAutomationState.TeleportingToSpot:
                    this.HandleTeleporting(this.digTerritoryId, this.digZoneName, AlexandriteAutomationState.TravelingToSpot);
                    break;
                case AlexandriteAutomationState.TravelingToSpot:
                    this.HandleTravelingToSpot();
                    break;
                case AlexandriteAutomationState.Digging:
                    this.HandleDigging();
                    break;
                case AlexandriteAutomationState.OpeningCoffer:
                    this.HandleOpeningCoffer();
                    break;
                case AlexandriteAutomationState.Fighting:
                    this.HandleFighting();
                    break;
            }
        }
        catch (Exception ex)
        {
            Service.PluginLog.Error(ex, "Exception during the Alexandrite automation.");
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
            if (this.State is AlexandriteAutomationState.TeleportingToVendor
                or AlexandriteAutomationState.TeleportingToSpot)
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
            // The map is lost with the coffer left standing, so there is nothing
            // to come back to; better to stop where the character fell.
            this.Fail("You died. Automation stopped.");
            return false;
        }

        return true;
    }

    /// <summary>
    ///     Decide what the run needs next. The order is what keeps the trips down:
    ///     a sealed map is deciphered on the spot, a deciphered one is dug up, and
    ///     the vendor is only visited when a map has to be bought - buying the
    ///     second one right after deciphering the first, while still standing
    ///     there.
    /// </summary>
    private void HandleDecidingNextStep()
    {
        if (this.mapsDone >= this.mapsTarget)
        {
            this.Complete();
            return;
        }

        var openRank = TreasureMapGameActions.GetOpenMapRank();
        var sealedMaps = TreasureMapGameActions.GetItemCount(MysteriousMapItemId);

        if (openRank == 0 && sealedMaps > 0)
        {
            this.TransitionTo(AlexandriteAutomationState.Deciphering);
            return;
        }

        if (openRank != 0)
        {
            // The trip is the expensive part of a map, so leave the vendor with
            // the bag full: one map deciphered, one still sealed.
            if (sealedMaps == 0
                && this.MapsLeftToBuy() > 0
                && this.IsAtVendor()
                && GetPoeticsCount() >= AnimusConfiguration.MapCost)
            {
                this.TransitionTo(AlexandriteAutomationState.BuyingMap);
                return;
            }

            if (!this.ResolveDigSpot(openRank))
            {
                return;
            }

            this.TransitionTo(Service.ClientState.TerritoryType == this.digTerritoryId
                ? AlexandriteAutomationState.TravelingToSpot
                : AlexandriteAutomationState.TeleportingToSpot);
            return;
        }

        var poetics = GetPoeticsCount();
        if (poetics < AnimusConfiguration.MapCost)
        {
            // Running out of tomestones is where the run ends, not something that
            // went wrong: every map farmed up to here is in the bag.
            this.Complete($"{poetics} of the {AnimusConfiguration.MapCost} " +
                          "Allagan tomestones of poetics a map costs are left");
            return;
        }

        if (this.IsAtVendor())
        {
            this.TransitionTo(AlexandriteAutomationState.BuyingMap);
            return;
        }

        this.aetheryteCandidates = AtmaManager.GetAetherytesByDistance(VendorMapLink);
        if (this.aetheryteCandidates.Count == 0)
        {
            this.Fail("No aetheryte found in Mor Dhona.");
            return;
        }

        this.TransitionTo(AlexandriteAutomationState.TeleportingToVendor);
    }

    private void HandleTeleporting(uint territoryId, string label, AlexandriteAutomationState next)
    {
        this.StatusDetail = $"Teleporting to {label}...";

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

        if (Service.ClientState.TerritoryType == territoryId
            && (this.sawZoning || this.teleportStartTerritory != territoryId))
        {
            this.TransitionTo(next);
            return;
        }

        if (this.StateAge > TimeSpan.FromSeconds(180))
        {
            this.Fail($"Could not get to {label} in time.");
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
                this.Fail($"Teleport to {label} failed repeatedly.");
                return;
            }

            if (this.aetheryteCandidates.Count == 0 || !AtmaManager.ExecuteTeleport(this.aetheryteCandidates[0]))
            {
                this.Fail($"Could not teleport to {label}.");
                return;
            }

            this.lastTeleportAt = DateTime.UtcNow;
        }
    }

    private void HandleTravelingToVendor()
    {
        if (this.DriveTravel(this.CreateVendorTarget(), VendorName))
        {
            this.TransitionTo(AlexandriteAutomationState.BuyingMap);
        }
    }

    private void HandleTravelingToSpot()
    {
        if (this.digMapLink is null)
        {
            this.Fail("The dig spot was lost; stopping.");
            return;
        }

        if (this.DriveTravel(this.CreateDigTarget(), $"the dig spot in {this.digZoneName}"))
        {
            this.TransitionTo(AlexandriteAutomationState.Digging);
        }
    }

    /// <summary>
    ///     Hand a trip over to the shared travel helper, which waits for the
    ///     navmesh, mounts, flies when the zone allows it, lands at the end and
    ///     recovers from getting stuck.
    /// </summary>
    /// <param name="target">Where to go.</param>
    /// <param name="label">How the destination is named in messages.</param>
    /// <returns>Whether the destination was reached.</returns>
    private bool DriveTravel(BraveTarget target, string label)
    {
        this.StatusDetail = $"Traveling to {label}...";

        if (!this.stateEntered)
        {
            // The teleport, when there was one, has already landed by now.
            this.bookTravel.Start(target, waitForTeleport: false);
            this.stateEntered = true;
            return false;
        }

        if (this.bookTravel.IsRunning)
        {
            if (this.StateAge > TimeSpan.FromSeconds(420))
            {
                this.Fail($"Could not reach {label} in time.");
            }

            return false;
        }

        if (!this.bookTravel.Arrived)
        {
            // The helper gives the travel up on combat as well as on a route it
            // cannot walk; kill whatever jumped us and set off again rather than
            // ending the whole run over a mob on the way.
            if (Service.Condition[ConditionFlag.InCombat])
            {
                this.FightThen(this.State);
                return false;
            }

            if (++this.travelAttempts > 2)
            {
                this.Fail($"Could not travel to {label}, see /xllog for details.");
                return false;
            }

            Log($"The travel to {label} was given up on; setting off again ({this.travelAttempts}/2).");
            this.stateEntered = false;
            return false;
        }

        return true;
    }

    private void HandleBuyingMap()
    {
        var sealedMaps = TreasureMapGameActions.GetItemCount(MysteriousMapItemId);

        if (!this.stateEntered)
        {
            this.mapsBeforePurchase = sealedMaps;

            // Where to stand when Auriana herself is not loaded yet.
            var xz = VendorWorldPositionXZ();
            var approximate = new Vector3(xz.X, Service.ObjectTable.LocalPlayer!.Position.Y, xz.Y);
            this.destination = this.navmesh.FindNavigablePoint(new Vector3(xz.X, 0, xz.Y)) ?? approximate;
            this.stateEntered = true;
        }

        if (sealedMaps > this.mapsBeforePurchase)
        {
            this.navmesh.Stop();
            Log($"Bought a mysterious map for {AnimusConfiguration.MapCost} Allagan tomestones of poetics.");
            this.TransitionTo(AlexandriteAutomationState.DismissingDialogue);
            return;
        }

        if (this.StateAge > TimeSpan.FromSeconds(90))
        {
            var poetics = GetPoeticsCount();
            this.Fail(poetics < AnimusConfiguration.MapCost
                ? $"Not enough Allagan tomestones of poetics for a mysterious map: {poetics}/{AnimusConfiguration.MapCost}."
                : $"Could not buy a mysterious map from {VendorName}. The exchange only opens once " +
                  "\"Celestial Radiance\" is complete.");
            return;
        }

        this.StatusDetail = $"Buying a mysterious map from {VendorName}...";

        // A flying approach ends hovering over the NPC; land before interacting,
        // and path in the air while still up there - the ground mesh has no start
        // point mid-air, so a ground pathfind from there silently finds nothing.
        if (AtmaAutomationManager.LandIfHovering(this.navmesh, "ZodiacBuddy.Alexandrite.Land"))
        {
            return;
        }

        var fly = Service.Condition[ConditionFlag.InFlight];
        var npc = BookExchangeGameActions.FindNpcByName(VendorName);
        if (npc is null)
        {
            if (!this.navmesh.IsPathRunning && !this.navmesh.IsPathfindInProgress
                && EzThrottler.Throttle("ZodiacBuddy.Alexandrite.NpcApproach", 2000))
            {
                this.navmesh.PathfindAndMoveCloseTo(this.destination, 2f, fly);
            }
        }
        else if (!FateGameActions.IsInInteractRange(npc))
        {
            if (!this.navmesh.IsPathRunning && !this.navmesh.IsPathfindInProgress
                && EzThrottler.Throttle("ZodiacBuddy.Alexandrite.NpcApproach", 1000))
            {
                this.navmesh.PathfindAndMoveCloseTo(npc.Position, 2f, fly);
            }
        }
        else
        {
            this.navmesh.Stop();

            if (Service.Condition[ConditionFlag.Mounted])
            {
                if (EzThrottler.Throttle("ZodiacBuddy.Alexandrite.Dismount", 500))
                {
                    AtmaAutomationManager.TryUseGeneralAction(DismountActionId);
                }

                return;
            }

            // Click Auriana only while no window of hers is up, so an interact can
            // never close the menu we are working through.
            if (!BookExchangeGameActions.IsDialogOpen()
                && !TreasureMapGameActions.IsShopOpen()
                && EzThrottler.Throttle("ZodiacBuddy.Alexandrite.Interact", 2000))
            {
                FateGameActions.InteractWith(npc);
            }

            if (EzThrottler.Throttle("ZodiacBuddy.Alexandrite.Dialogue", 250))
            {
                this.DriveExchangeDialog();
            }
        }

    }

    /// <summary>
    ///     Walk one step of the map exchange each call: advance the line of
    ///     dialogue, pick the entry that opens the mysterious map exchange, buy the
    ///     map from the exchange window should one open, and confirm the tomestone
    ///     prompt.
    /// </summary>
    private void DriveExchangeDialog()
    {
        FateGameActions.ProgressTalk();

        var selected = BookExchangeGameActions.SelectMenuEntry(ExchangeMenuEntry);
        if (selected is not null)
        {
            Service.PluginLog.Debug($"[Alexandrite] Selected \"{selected.ReplaceLineEndings(" ")}\".");
        }
        else if (EzThrottler.Throttle("ZodiacBuddy.Alexandrite.EntryDump", 5000))
        {
            var entries = BookExchangeGameActions.GetOpenMenuEntryTexts();
            if (entries.Count > 0)
            {
                Service.PluginLog.Debug($"[Alexandrite] No entry matched in: {string.Join(" | ", entries)}");
            }
        }

        // Whether the exchange is a plain question or a currency shop window
        // depends on the vendor; answer either. The shop is only clicked every
        // couple of seconds so a purchase is never queued twice.
        if (EzThrottler.Throttle("ZodiacBuddy.Alexandrite.Shop", 2000))
        {
            TreasureMapGameActions.TryBuyFromShop(MysteriousMapItemId);
        }

        TreasureMapGameActions.ConfirmPurchaseYesNo();
    }

    private void HandleDismissingDialogue()
    {
        this.StatusDetail = "Dismissing the remaining dialogue...";

        // The character stays flagged as occupied for the whole event, and a
        // teleport cast - or deciphering the map just bought - is refused while it
        // lasts, so wait for that flag to clear and hold it clear for a moment
        // before moving on.
        if (IsEventOngoing())
        {
            this.dialogueClearSince = null;

            if (EzThrottler.Throttle("ZodiacBuddy.Alexandrite.Dismiss", 250))
            {
                DismissDialogue();
            }
        }
        else
        {
            this.dialogueClearSince ??= DateTime.UtcNow;
            if (DateTime.UtcNow - this.dialogueClearSince > DialogueSettleTime)
            {
                this.TransitionTo(AlexandriteAutomationState.DecidingNextStep);
                return;
            }
        }

        // Some dialogue keeps reopening (a quest offer, a window we do not know);
        // the purchase is done either way, so do not fail over it.
        if (this.StateAge > TimeSpan.FromSeconds(30))
        {
            Log("Some dialogue is still open after the purchase; continuing anyway.");
            this.TransitionTo(AlexandriteAutomationState.DecidingNextStep);
        }
    }

    private void HandleDeciphering()
    {
        if (TreasureMapGameActions.GetOpenMapRank() != 0)
        {
            Log("Deciphered a mysterious map.");
            this.TransitionTo(AlexandriteAutomationState.DecidingNextStep);
            return;
        }

        this.StatusDetail = "Deciphering the mysterious map...";

        // Checked before everything below, all of which can go on forever: a
        // window that will not close, a mount that will not come off, an action
        // the game keeps refusing. A run stuck in one of those never ends.
        if (this.StateAge > TimeSpan.FromSeconds(60))
        {
            this.Fail($"Could not decipher the mysterious map. {DescribeDecipherBlock()}");
            return;
        }

        // The list the Decipher action opens is answered before anything else:
        // it is a menu like any other, and the dismissal below would close it
        // again on every tick, which is what a run stuck here looks like.
        if (TreasureMapGameActions.SelectMapEntry(MapName))
        {
            Log($"Picked the {MapName} out of the decipher list.");
            return;
        }

        // ... and so is the question it asks before opening the map, which the
        // dismissal below cannot answer either.
        if (TreasureMapGameActions.ConfirmDecipherYesNo(MapName))
        {
            return;
        }

        // Deciphering right after a purchase is refused while the vendor's event
        // still has the character; clear whatever is left of it first.
        if (IsEventOngoing())
        {
            // Whatever is being closed here is worth a line: a decipher list
            // worded differently than the map itself would look exactly like
            // leftover vendor dialogue.
            if (EzThrottler.Throttle("ZodiacBuddy.Alexandrite.DecipherDump", 5000))
            {
                var open = BookExchangeGameActions.GetOpenMenuEntryTexts();
                if (open.Count > 0)
                {
                    Service.PluginLog.Debug($"[Alexandrite] Dismissing a menu while deciphering: {string.Join(" | ", open)}");
                }
            }

            if (EzThrottler.Throttle("ZodiacBuddy.Alexandrite.Dismiss", 250))
            {
                DismissDialogue();
            }

            return;
        }

        if (Service.Condition[ConditionFlag.Mounted])
        {
            if (EzThrottler.Throttle("ZodiacBuddy.Alexandrite.Dismount", 500))
            {
                AtmaAutomationManager.TryUseGeneralAction(DismountActionId);
            }

            return;
        }

        if (!EzThrottler.Throttle("ZodiacBuddy.Alexandrite.Decipher", 1000))
        {
            return;
        }

        // The right-click menu of the map in the bag, when it is the one that
        // came up, deciphers straight away.
        if (TreasureMapGameActions.IsContextMenuOpen())
        {
            if (TreasureMapGameActions.SelectDecipherEntry())
            {
                return;
            }

            // The menu came up on something else, or the entry is worded in a way
            // we do not know: say what was on it and start over.
            var entries = TreasureMapGameActions.GetContextMenuEntries();
            if (entries.Count > 0)
            {
                Service.PluginLog.Debug($"[Alexandrite] No \"Decipher\" entry in: {string.Join(" | ", entries)}");
            }

            TreasureMapGameActions.CloseContextMenu();
            return;
        }

        // Decipher opens the list of maps the character is carrying; the map is
        // deciphered by picking it from there, which the tick above does. Where
        // the action is not on the character's list, the map's own right-click
        // menu does the same job.
        if (!AtmaAutomationManager.TryUseGeneralAction(DecipherActionId))
        {
            if (TreasureMapGameActions.OpenItemContextMenu(MysteriousMapItemId))
            {
                Service.PluginLog.Debug("[Alexandrite] Opened the mysterious map's right-click menu.");
            }
            else
            {
                this.Fail("The mysterious map is no longer in the bag.");
            }
        }
    }

    private void HandleDigging()
    {
        var player = Service.ObjectTable.LocalPlayer!;
        this.StatusDetail = $"Digging at the marked spot in {this.digZoneName}...";

        if (!this.stateEntered)
        {
            this.digAttempts = 0;
            this.stateEntered = true;
        }

        // The coffer coming out of the ground is the proof the dig landed.
        if (TreasureMapGameActions.FindCoffer(this.digPosition, CofferSearchRange) is not null)
        {
            this.alexandriteAtDig = GetAlexandriteCount();
            this.cofferApproaches = 0;
            Log($"Dug up the treasure coffer in {this.digZoneName}.");
            this.TransitionTo(AlexandriteAutomationState.OpeningCoffer);
            return;
        }

        if (Service.Condition[ConditionFlag.InCombat])
        {
            this.FightThen(AlexandriteAutomationState.Digging);
            return;
        }

        if (this.StateAge > TimeSpan.FromSeconds(180))
        {
            this.Fail($"Could not dig the coffer up in {this.digZoneName}.");
            return;
        }

        if (AtmaAutomationManager.LandIfHovering(this.navmesh, "ZodiacBuddy.Alexandrite.Land"))
        {
            return;
        }

        var flatDistance = Vector2.Distance(
            new Vector2(player.Position.X, player.Position.Z),
            new Vector2(this.digPosition.X, this.digPosition.Z));

        if (flatDistance > DigRange)
        {
            if (!this.navmesh.IsPathRunning && !this.navmesh.IsPathfindInProgress
                && EzThrottler.Throttle("ZodiacBuddy.Alexandrite.DigApproach", 2000))
            {
                this.navmesh.PathfindAndMoveCloseTo(
                    this.digPosition, 1f, Service.Condition[ConditionFlag.InFlight]);
            }
        }
        else
        {
            this.navmesh.Stop();

            if (Service.Condition[ConditionFlag.Mounted])
            {
                if (EzThrottler.Throttle("ZodiacBuddy.Alexandrite.Dismount", 500))
                {
                    AtmaAutomationManager.TryUseGeneralAction(DismountActionId);
                }

                return;
            }

            // Digging is a cast; the action reports itself unavailable while one
            // is already going, so the throttle only keeps the log quiet.
            if (!Service.Condition[ConditionFlag.Casting]
                && EzThrottler.Throttle("ZodiacBuddy.Alexandrite.Dig", 2000))
            {
                AtmaAutomationManager.TryUseGeneralAction(DigActionId);

                // A few refusals in a row mean the shovel is going in a step too
                // far out; walk onto the spot itself rather than near it.
                if (++this.digAttempts % 4 == 0)
                {
                    Log("Nothing came up; moving onto the marked spot itself.");
                    this.navmesh.PathfindAndMoveTo(this.digPosition);
                }
            }
        }

    }

    private void HandleOpeningCoffer()
    {
        var alexandrite = GetAlexandriteCount();
        this.StatusDetail = $"Opening the treasure coffer... ({alexandrite} Alexandrite)";

        // Whatever came out of the coffer is dealt with before reaching for it
        // again; the second helping of loot is behind the fight.
        if (Service.Condition[ConditionFlag.InCombat])
        {
            this.FightThen(AlexandriteAutomationState.OpeningCoffer);
            return;
        }

        if (this.StateAge > TimeSpan.FromSeconds(300))
        {
            // The map is spent either way, so the run carries on with the next one
            // rather than ending over a coffer that would not open.
            Log("Gave up on the treasure coffer; moving on to the next map.");
            this.FinishMap(alexandrite);
            return;
        }

        var coffer = TreasureMapGameActions.FindCoffer(this.digPosition, CofferSearchRange, includeOpened: true);

        // Only a coffer fading out of the world is done with: the first click
        // lets its guardians out and leaves the lid up, and the loot is handed
        // over by clicking the very same coffer again once they are dead.
        var openable = coffer is not null && !TreasureMapGameActions.IsCofferSpent(coffer);

        // The loot is in the bag and the coffer has been emptied: give it a
        // moment, in case it still has a second helping to give.
        if (alexandrite > this.alexandriteAtDig
            && (coffer is null || TreasureMapGameActions.IsCofferOpened(coffer)))
        {
            this.lootedSince ??= DateTime.UtcNow;
            if (DateTime.UtcNow - this.lootedSince > TimeSpan.FromSeconds(5))
            {
                this.FinishMap(alexandrite);
            }

            return;
        }

        this.lootedSince = null;

        if (coffer is null)
        {
            // Nothing to open and nothing gained: the coffer timed out or someone
            // else's dig was picked up. Wait a little, then take the map as spent.
            if (this.StateAge > TimeSpan.FromSeconds(30))
            {
                Log("The treasure coffer is gone; moving on to the next map.");
                this.FinishMap(alexandrite);
            }

            return;
        }

        var player = Service.ObjectTable.LocalPlayer!;
        var distance = Vector2.Distance(
            new Vector2(player.Position.X, player.Position.Z),
            new Vector2(coffer.Position.X, coffer.Position.Z));

        // How far the character stands from the coffer decides this, not the
        // game's range check on its own: that one answers yes from out of reach.
        // It is still worth listening to once walking closer has stopped helping,
        // for a coffer the mesh cannot get right up to.
        var closeEnough = distance <= CofferInteractRange
                          || (this.cofferApproaches >= 5 && FateGameActions.IsInInteractRange(coffer));

        if (!closeEnough)
        {
            if (AtmaAutomationManager.LandIfHovering(this.navmesh, "ZodiacBuddy.Alexandrite.Land"))
            {
                return;
            }

            // A coffer cannot be opened from the saddle, and walking the last
            // yalms on foot is what the interact range is measured against.
            if (Service.Condition[ConditionFlag.Mounted])
            {
                if (EzThrottler.Throttle("ZodiacBuddy.Alexandrite.Dismount", 500))
                {
                    AtmaAutomationManager.TryUseGeneralAction(DismountActionId);
                }

                return;
            }

            if (!this.navmesh.IsPathRunning && !this.navmesh.IsPathfindInProgress
                && EzThrottler.Throttle("ZodiacBuddy.Alexandrite.CofferApproach", 1000))
            {
                // The coffer stands where the shovel went in, which is not always
                // a point the mesh knows; drop it onto the floor first, and once
                // stopping short of it has failed twice, walk onto it instead.
                var goal = this.navmesh.FindNavigablePointOnLayer(coffer.Position) ?? coffer.Position;
                var accepted = ++this.cofferApproaches <= 2
                    ? this.navmesh.PathfindAndMoveCloseTo(goal, 0.5f)
                    : this.navmesh.PathfindAndMoveTo(goal);
                Service.PluginLog.Debug(
                    $"[Alexandrite] Closing on the coffer: {distance:F1}y away, " +
                    $"attempt {this.cofferApproaches}, accepted={accepted}.");
            }

            return;
        }

        this.navmesh.Stop();

        if (openable && EzThrottler.Throttle("ZodiacBuddy.Alexandrite.Open", 3000))
        {
            FateGameActions.InteractWith(coffer);
        }

        if (EzThrottler.Throttle("ZodiacBuddy.Alexandrite.CofferDialog", 250))
        {
            FateGameActions.ProgressTalk();
            TreasureMapGameActions.ConfirmCofferYesNo();
        }
    }

    private void HandleFighting()
    {
        this.StatusDetail = "Fighting off the guardians...";
        var player = Service.ObjectTable.LocalPlayer!;

        // The nearest enemy still flagged in combat is finished off, but only the
        // one actually on us keeps the fight going once combat has dropped -
        // someone else's fight nearby is none of ours.
        var attacker = AtmaAutomationManager.FindLingeringAttacker(player);

        if (!Service.Condition[ConditionFlag.InCombat]
            && (attacker is null || attacker.TargetObjectId != player.GameObjectId))
        {
            this.TransitionTo(this.stateAfterFight);
            return;
        }

        if (this.StateAge > TimeSpan.FromSeconds(180))
        {
            this.Fail("Could not fight off the coffer's guardians in time.");
            return;
        }

        if (attacker is not null)
        {
            // Combat can't be done mounted.
            if (Service.Condition[ConditionFlag.Mounted])
            {
                if (EzThrottler.Throttle("ZodiacBuddy.Alexandrite.Dismount", 500))
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

    }

    /// <summary>
    ///     Work out where the deciphered map leads, from the same data the game
    ///     draws the map picture with.
    /// </summary>
    /// <param name="rank">Treasure hunt rank of the open map.</param>
    /// <returns>Whether the spot was resolved.</returns>
    private bool ResolveDigSpot(uint rank)
    {
        var spotKey = TreasureMapGameActions.GetOpenMapSpotKey();
        if (!TreasureMapGameActions.TryGetDigSpot(rank, spotKey, out var position, out var territoryId, out var mapId))
        {
            this.Fail("Could not work out where the deciphered map leads.");
            return false;
        }

        var map = Service.DataManager.GetExcelSheet<Map>().GetRow(mapId);
        var mapX = WorldToMap(position.X, map.SizeFactor, map.OffsetX);
        var mapY = WorldToMap(position.Z, map.SizeFactor, map.OffsetY);

        this.digPosition = position;
        this.digTerritoryId = territoryId;
        this.digMapLink = new MapLinkPayload(territoryId, mapId, mapX, mapY);
        this.digZoneName = this.digMapLink.TerritoryType.Value.PlaceName.Value.Name.ToString();

        if (rank != MysteriousMapRank)
        {
            Log("An unrelated treasure map is deciphered; completing it first to free the slot.");
        }

        Log($"The map leads to {this.digZoneName} ({mapX:F1}, {mapY:F1}).");

        if (territoryId != Service.ClientState.TerritoryType)
        {
            this.aetheryteCandidates = AtmaManager.GetAetherytesByDistance(this.digMapLink);
            if (this.aetheryteCandidates.Count == 0)
            {
                this.Fail($"No aetheryte found in {this.digZoneName}.");
                return false;
            }
        }

        return true;
    }

    /// <summary>
    ///     Describe the vendor as a book target, so the shared travel helper can
    ///     take the character to her the same way it does for a book entry.
    /// </summary>
    /// <returns>The travel target.</returns>
    private BraveTarget CreateVendorTarget()
    {
        var mapLink = VendorMapLink;
        return new BraveTarget
        {
            Name = VendorName,
            ZoneName = mapLink.TerritoryType.Value.PlaceName.Value.Name.ToString(),
            ZoneId = mapLink.TerritoryType.RowId,
            LocationName = string.Empty,
            Position = mapLink,
        };
    }

    /// <summary>
    ///     Describe the dig spot as a book target for the shared travel helper.
    ///     The last yalms are walked by the digging state itself, which needs the
    ///     exact spot rather than the map coordinate.
    /// </summary>
    /// <returns>The travel target.</returns>
    private BraveTarget CreateDigTarget()
        => new()
        {
            Name = "the marked spot",
            ZoneName = this.digZoneName,
            ZoneId = this.digTerritoryId,
            LocationName = string.Empty,
            Position = this.digMapLink!,
        };

    /// <summary>
    ///     Check whether the character stands close enough to the vendor to buy
    ///     without another trip.
    /// </summary>
    /// <returns>Whether the exchange is within reach.</returns>
    private bool IsAtVendor()
    {
        var player = Service.ObjectTable.LocalPlayer;
        if (player is null || Service.ClientState.TerritoryType != VendorTerritoryId)
        {
            return false;
        }

        return Vector2.Distance(
            new Vector2(player.Position.X, player.Position.Z), VendorWorldPositionXZ()) < VendorRange;
    }

    /// <summary>
    ///     Get how many maps still have to be bought to finish the run, the ones
    ///     already in hand taken off.
    /// </summary>
    /// <returns>The number of maps left to buy.</returns>
    private int MapsLeftToBuy()
        => this.mapsTarget - this.mapsDone - GetMapsInHand();

    private Vector2 VendorWorldPositionXZ()
    {
        var mapLink = VendorMapLink;
        var map = Service.DataManager.GetExcelSheet<Map>().GetRow(mapLink.Map.RowId);
        return new Vector2(
            AtmaAutomationManager.MapToWorld(mapLink.XCoord, map.SizeFactor, map.OffsetX),
            AtmaAutomationManager.MapToWorld(mapLink.YCoord, map.SizeFactor, map.OffsetY));
    }

    /// <summary>
    ///     Note a map as farmed and go back to deciding what comes next.
    /// </summary>
    /// <param name="alexandrite">Alexandrite carried now.</param>
    private void FinishMap(int alexandrite)
    {
        this.mapsDone++;
        var gained = alexandrite - this.alexandriteAtDig;
        Log($"Map {this.mapsDone}/{this.mapsTarget} done" +
            $" (+{gained} Alexandrite, {alexandrite}/{AnimusConfiguration.AlexandriteGoal} in the bag).");
        this.TransitionTo(AlexandriteAutomationState.DecidingNextStep);
    }

    /// <summary>
    ///     Deal with whatever is attacking, then pick the interrupted step back up.
    /// </summary>
    /// <param name="resumeAt">State to return to once the fight is over.</param>
    private void FightThen(AlexandriteAutomationState resumeAt)
    {
        this.bookTravel.Cancel("A fight interrupted the travel.");
        this.stateAfterFight = resumeAt;
        this.TransitionTo(AlexandriteAutomationState.Fighting);
    }

    private void TransitionTo(AlexandriteAutomationState state)
    {
        // A chase path left over from a fight would otherwise keep running while
        // the next step thinks it is standing still.
        if (this.State == AlexandriteAutomationState.Fighting && state != AlexandriteAutomationState.Fighting)
        {
            this.navmesh.Stop();
        }

        // The combat plugin attacks our hard target even out of combat, so drop
        // the target when heading anywhere that isn't a fight.
        if (state is not AlexandriteAutomationState.Fighting)
        {
            Service.TargetManager.Target = null;
        }

        // Only arm the combat plugin while a fight is in progress; BossMod's dodge
        // movement must not interfere with travel or teleport casts.
        this.combat.SetCombatActive(state is AlexandriteAutomationState.Fighting);
        this.State = state;
        this.stateEnteredAt = DateTime.UtcNow;
        this.stateEntered = false;
        this.dialogueClearSince = null;
        this.lootedSince = null;

        if (state is AlexandriteAutomationState.TravelingToVendor or AlexandriteAutomationState.TravelingToSpot)
        {
            this.travelAttempts = 0;
        }
    }

    /// <summary>
    ///     End the run, either with every map of it farmed or short of that for a
    ///     reason there is nothing to fix about.
    /// </summary>
    /// <param name="cutShortBecause">Why the run ended early, or null when it ran
    ///     its course.</param>
    private void Complete(string? cutShortBecause = null)
    {
        this.Cleanup();
        var alexandrite = GetAlexandriteCount();
        var maps = cutShortBecause is null
            ? $"{this.mapsDone} maps farmed"
            : $"Stopped after {this.mapsDone} of {this.mapsTarget} maps, only {cutShortBecause}";
        this.State = AlexandriteAutomationState.Completed;
        this.StatusDetail = $"{maps}, +{alexandrite - this.alexandriteAtStart} Alexandrite" +
                            $" ({alexandrite}/{AnimusConfiguration.AlexandriteGoal}).";
        Log(this.StatusDetail);
    }

    private void Cleanup()
    {
        this.navmesh.Stop();
        this.bookTravel.Cancel("The Alexandrite automation stopped.");
        this.combat.EndControl();
    }

    private void OnControlLost()
    {
        if (this.IsRunning)
        {
            this.Fail($"Control of {this.combat.ControlledName} was revoked.");
        }
    }

    private void Fail(string reason)
    {
        this.Cleanup();
        this.State = AlexandriteAutomationState.Errored;
        this.StatusDetail = string.Empty;
        this.LastError = reason;
        Service.PluginLog.Warning($"[Alexandrite] Stopped: {reason}");
    }
}
