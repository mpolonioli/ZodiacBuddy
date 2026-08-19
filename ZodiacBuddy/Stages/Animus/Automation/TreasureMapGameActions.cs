using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using CSTreasure = FFXIVClientStructs.FFXIV.Client.Game.Object.Treasure;

namespace ZodiacBuddy.Stages.Animus.Automation;

/// <summary>
///     Native game interactions used by the Alexandrite automation: reading which
///     treasure map is deciphered and where it leads, finding the coffer it digs
///     up and buying maps from the vendor's exchange window.
/// </summary>
internal static unsafe class TreasureMapGameActions
{
    /// <summary>
    ///     Row of the Addon sheet holding "Decipher" as the item's right-click
    ///     menu labels it, so the entry is found in whatever language the client
    ///     runs in.
    /// </summary>
    private const uint DecipherEntryRow = 8100;

    /// <summary>
    ///     The bags a bought map can land in.
    /// </summary>
    private static readonly InventoryType[] Bags =
    [
        InventoryType.Inventory1,
        InventoryType.Inventory2,
        InventoryType.Inventory3,
        InventoryType.Inventory4,
    ];

    private static string? decipherLabel;

    /// <summary>
    ///     Get how many of an item the character is carrying, armory and equipped
    ///     gear excluded.
    /// </summary>
    /// <param name="itemId">Item row ID.</param>
    /// <returns>The item count.</returns>
    public static int GetItemCount(uint itemId)
    {
        var inventory = InventoryManager.Instance();
        return inventory == null ? 0 : inventory->GetInventoryItemCount(itemId, false, false, false);
    }

    /// <summary>
    ///     Get how many of a currency the character holds.
    /// </summary>
    /// <param name="itemId">Item row ID of the currency.</param>
    /// <returns>The currency count.</returns>
    public static int GetCurrencyCount(uint itemId)
    {
        var inventory = InventoryManager.Instance();
        return inventory == null ? 0 : inventory->GetItemCountInContainer(itemId, InventoryType.Currency);
    }

    /// <summary>
    ///     Get the treasure hunt rank of the map the character currently carries
    ///     deciphered, which is what tells one kind of map from another.
    /// </summary>
    /// <returns>The TreasureHuntRank row ID, or 0 when no map is deciphered.</returns>
    public static uint GetOpenMapRank()
    {
        var manager = EventItemManager.Instance();
        return manager == null ? 0 : manager->GetTreasureHuntRank();
    }

    /// <summary>
    ///     Get which of the rank's dig spots the deciphered map points at. This is
    ///     the same number the game itself uses to draw the map, so no guessing
    ///     from the picture - or another plugin - is needed.
    /// </summary>
    /// <returns>The TreasureSpot subrow ID.</returns>
    public static ushort GetOpenMapSpotKey()
    {
        var manager = EventItemManager.Instance();
        return manager == null ? (ushort)0 : manager->GetTreasureSpotSubKey();
    }

    /// <summary>
    ///     Look the dig spot of a deciphered map up in the game's own data.
    /// </summary>
    /// <param name="rank">TreasureHuntRank row ID of the map.</param>
    /// <param name="spotKey">TreasureSpot subrow ID of the spot.</param>
    /// <param name="position">World position to dig at.</param>
    /// <param name="territoryId">Territory the spot is in.</param>
    /// <param name="mapId">Map the spot is on.</param>
    /// <returns>Whether the spot could be looked up.</returns>
    public static bool TryGetDigSpot(uint rank, ushort spotKey, out Vector3 position, out uint territoryId, out uint mapId)
    {
        position = Vector3.Zero;
        territoryId = 0;
        mapId = 0;

        try
        {
            var sheet = Service.DataManager.GetSubrowExcelSheet<TreasureSpot>();
            if (!sheet.TryGetRow(rank, out var spots) || spotKey >= spots.Count)
            {
                Service.PluginLog.Warning($"[Alexandrite] No treasure spot {rank}#{spotKey} in the game data.");
                return false;
            }

            var level = spots[spotKey].Location.ValueNullable;
            if (level is null)
            {
                Service.PluginLog.Warning($"[Alexandrite] Treasure spot {rank}#{spotKey} has no location.");
                return false;
            }

            position = new Vector3(level.Value.X, level.Value.Y, level.Value.Z);
            territoryId = level.Value.Territory.RowId;
            mapId = level.Value.Map.RowId;
            return territoryId != 0 && mapId != 0;
        }
        catch (Exception ex)
        {
            Service.PluginLog.Warning(ex, $"Could not look up treasure spot {rank}#{spotKey}.");
            return false;
        }
    }

    /// <summary>
    ///     Find the treasure coffer nearest to the dig spot.
    /// </summary>
    /// <param name="near">Position to search around.</param>
    /// <param name="radius">Search radius in yalms.</param>
    /// <param name="includeOpened">Whether coffers that are already open count.</param>
    /// <returns>The coffer, or null when none is around.</returns>
    public static IGameObject? FindCoffer(Vector3 near, float radius, bool includeOpened = false)
        => Service.ObjectTable
            .Where(o => o.ObjectKind == ObjectKind.Treasure
                        && o.IsValid()
                        && !o.IsDead
                        && Vector3.Distance(o.Position, near) <= radius
                        && (includeOpened || !IsCofferOpened(o)))
            .OrderBy(o => Vector3.DistanceSquared(o.Position, near))
            .FirstOrDefault();

    /// <summary>
    ///     Check whether a coffer has been opened at least once. The lid coming up
    ///     is not the end of it: the coffer of a map lets its guardians out first
    ///     and only hands the loot over once they are dealt with, so this says
    ///     nothing about whether it is finished with.
    /// </summary>
    /// <param name="coffer">The coffer to inspect.</param>
    /// <returns>Whether the coffer has been opened.</returns>
    public static bool IsCofferOpened(IGameObject coffer)
    {
        var treasure = (CSTreasure*)coffer.Address;
        return treasure != null && treasure->Flags.HasFlag(CSTreasure.TreasureFlags.Opened);
    }

    /// <summary>
    ///     Check whether a coffer is fading out of the world, which is the one
    ///     state it can no longer be reached for.
    /// </summary>
    /// <param name="coffer">The coffer to inspect.</param>
    /// <returns>Whether the coffer is spent.</returns>
    public static bool IsCofferSpent(IGameObject coffer)
    {
        var treasure = (CSTreasure*)coffer.Address;
        return treasure != null && treasure->Flags.HasFlag(CSTreasure.TreasureFlags.FadedOut);
    }

    /// <summary>
    ///     Get whether an action of the general list can be used right now.
    /// </summary>
    /// <param name="actionId">GeneralAction row ID.</param>
    /// <returns>The action status: 0 when it can be used, an error code otherwise.</returns>
    public static uint GetGeneralActionStatus(uint actionId)
    {
        var actionManager = ActionManager.Instance();
        return actionManager == null ? uint.MaxValue : actionManager->GetActionStatus(ActionType.GeneralAction, actionId);
    }

    /// <summary>
    ///     Select a map by name in the list the Decipher action opens, which holds
    ///     every map the character could decipher. The name has to match exactly:
    ///     the vendor's "Mysterious Map Exchange" entry merely contains it, and
    ///     picking that one would send the run back into her shop.
    /// </summary>
    /// <param name="mapName">Name of the map to decipher.</param>
    /// <returns>Whether the map was picked.</returns>
    public static bool SelectMapEntry(string mapName)
    {
        var addon = GetReadyAddon("SelectString");
        if (addon != null)
        {
            foreach (var entry in new AddonMaster.SelectString((nint)addon).Entries)
            {
                if (string.Equals(entry.Text.Trim(), mapName, StringComparison.OrdinalIgnoreCase))
                {
                    entry.Select();
                    return true;
                }
            }
        }

        addon = GetReadyAddon("SelectIconString");
        if (addon != null)
        {
            foreach (var entry in new AddonMaster.SelectIconString((nint)addon).Entries)
            {
                if (string.Equals(entry.Text.Trim(), mapName, StringComparison.OrdinalIgnoreCase))
                {
                    entry.Select();
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    ///     Check whether an item's right-click menu is open.
    /// </summary>
    /// <returns>Whether the context menu is up.</returns>
    public static bool IsContextMenuOpen()
        => GetReadyAddon("ContextMenu") != null;

    /// <summary>
    ///     Open the right-click menu of the first bag slot holding the item. The
    ///     inventory window does not have to be open for this.
    /// </summary>
    /// <param name="itemId">Item row ID to open the menu for.</param>
    /// <returns>Whether a slot holding the item was found.</returns>
    public static bool OpenItemContextMenu(uint itemId)
    {
        var inventory = InventoryManager.Instance();
        var agent = AgentInventoryContext.Instance();
        var agentModule = AgentModule.Instance();
        if (inventory == null || agent == null || agentModule == null)
        {
            return false;
        }

        foreach (var bag in Bags)
        {
            var container = inventory->GetInventoryContainer(bag);
            if (container == null || !container->IsLoaded)
            {
                continue;
            }

            for (var slot = 0; slot < container->Size; slot++)
            {
                var item = container->GetInventorySlot(slot);
                if (item == null || item->ItemId != itemId)
                {
                    continue;
                }

                var inventoryAgent = agentModule->GetAgentByInternalId(AgentId.Inventory);
                var addonId = inventoryAgent == null ? 0 : inventoryAgent->GetAddonId();
                agent->OpenForItemSlot(bag, slot, 0, addonId);
                return true;
            }
        }

        return false;
    }

    /// <summary>
    ///     Pick the "Decipher" entry of the open right-click menu, which is how the
    ///     game itself turns a sealed map into the deciphered one.
    /// </summary>
    /// <returns>Whether the entry was picked.</returns>
    public static bool SelectDecipherEntry()
    {
        var addon = GetReadyAddon("ContextMenu");
        if (addon == null)
        {
            return false;
        }

        var label = GetDecipherLabel();
        if (label.Length == 0)
        {
            return false;
        }

        // The entries are read and clicked through the addon itself: the menu
        // keeps them in the addon's values behind a fixed offset, while the
        // click wants the plain entry number, and getting that wrong dismisses
        // the menu without doing anything.
        foreach (var entry in new AddonMaster.ContextMenu((nint)addon).Entries)
        {
            if (string.Equals(entry.Text.Trim(), label, StringComparison.OrdinalIgnoreCase))
            {
                return entry.Select();
            }
        }

        return false;
    }

    /// <summary>
    ///     Get the entries of the open right-click menu, for diagnostics when the
    ///     one we are after is not among them.
    /// </summary>
    /// <returns>The entry texts.</returns>
    public static List<string> GetContextMenuEntries()
    {
        var addon = GetReadyAddon("ContextMenu");
        return addon == null
            ? []
            : new AddonMaster.ContextMenu((nint)addon).Entries
                .Select(entry => $"{entry.Text}{(entry.Enabled ? string.Empty : " (disabled)")}")
                .ToList();
    }

    /// <summary>
    ///     Close the right-click menu, e.g. when it opened on the wrong item.
    /// </summary>
    /// <returns>Whether a menu was closed.</returns>
    public static bool CloseContextMenu()
    {
        var addon = GetReadyAddon("ContextMenu");
        if (addon == null)
        {
            return false;
        }

        addon->Close(true);
        return true;
    }

    /// <summary>
    ///     Check whether a currency exchange window is open, so the vendor is not
    ///     clicked again while her shop is being worked through.
    /// </summary>
    /// <returns>Whether an exchange window is open.</returns>
    public static bool IsShopOpen()
        => GetReadyAddon("ShopExchangeCurrency") != null
           || GetReadyAddon("ShopExchangeCurrencyDialog") != null;

    /// <summary>
    ///     Buy an item from the open currency exchange window, should the vendor
    ///     answer with one rather than a plain confirmation.
    /// </summary>
    /// <param name="itemId">Item row ID to buy.</param>
    /// <returns>Whether the item was selected for purchase.</returns>
    public static bool TryBuyFromShop(uint itemId)
    {
        var addon = GetReadyAddon("ShopExchangeCurrency");
        if (addon == null)
        {
            return false;
        }

        var master = new AddonMaster.ShopExchangeCurrency((nint)addon);
        foreach (var item in master.BasicShopItems)
        {
            if (item.ItemId != itemId)
            {
                continue;
            }

            item.Select();
            return true;
        }

        return false;
    }

    /// <summary>
    ///     Confirm the SelectYesno dialog if it is about buying a map. Any other
    ///     confirmation is left alone.
    /// </summary>
    /// <returns>Whether a dialog was confirmed.</returns>
    public static bool ConfirmPurchaseYesNo()
        => ConfirmYesNoAbout("map", "tomestone", "poetics");

    /// <summary>
    ///     Confirm the SelectYesno dialog if it is about deciphering the map, which
    ///     the game asks about before opening it. Any other confirmation is left
    ///     alone.
    /// </summary>
    /// <param name="mapName">Name of the map being deciphered.</param>
    /// <returns>Whether a dialog was confirmed.</returns>
    public static bool ConfirmDecipherYesNo(string mapName)
        => ConfirmYesNoAbout(mapName, "decipher");

    /// <summary>
    ///     Confirm the SelectYesno dialog if it is about opening the coffer. Any
    ///     other confirmation is left alone.
    /// </summary>
    /// <returns>Whether a dialog was confirmed.</returns>
    public static bool ConfirmCofferYesNo()
        => ConfirmYesNoAbout("coffer", "treasure", "chest");

    /// <summary>
    ///     Close the currency exchange window if the vendor left one open.
    /// </summary>
    /// <returns>Whether a window was closed.</returns>
    public static bool CloseShop()
    {
        foreach (var name in new[] { "ShopExchangeCurrency", "ShopExchangeCurrencyDialog" })
        {
            var addon = GetReadyAddon(name);
            if (addon != null)
            {
                addon->Close(true);
                return true;
            }
        }

        return false;
    }

    private static bool ConfirmYesNoAbout(params string[] needles)
    {
        var addon = GetReadyAddon("SelectYesno");
        if (addon == null)
        {
            return false;
        }

        var master = new AddonMaster.SelectYesno((nint)addon);
        var text = master.Text;
        if (!needles.Any(needle => text.Contains(needle, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        master.Yes();
        return true;
    }

    /// <summary>
    ///     Get "Decipher" as the client words it.
    /// </summary>
    /// <returns>The label, or an empty string when the sheet cannot be read.</returns>
    private static string GetDecipherLabel()
    {
        if (decipherLabel is not null)
        {
            return decipherLabel;
        }

        try
        {
            decipherLabel = Service.DataManager.GetExcelSheet<Addon>().GetRow(DecipherEntryRow).Text.ExtractText();
        }
        catch (Exception ex)
        {
            Service.PluginLog.Warning(ex, "Could not read the Decipher menu entry from the game data.");
            decipherLabel = "Decipher";
        }

        return decipherLabel;
    }

    private static AtkUnitBase* GetReadyAddon(string name)
    {
        var addon = RaptureAtkUnitManager.Instance()->GetAddonByName(name);
        return addon != null && addon->IsVisible && addon->IsReady && addon->IsFullyLoaded() ? addon : null;
    }
}
