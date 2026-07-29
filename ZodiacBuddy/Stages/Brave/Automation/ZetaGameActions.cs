using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ZodiacBuddy.Stages.Brave.Automation;

/// <summary>
///     Native game interactions used by the Zodiac Zeta mahatma automation:
///     finding Remon and driving his mahatma purchase dialogs.
/// </summary>
internal static unsafe class ZetaGameActions
{
    /// <summary>
    ///     Entries never selected in Remon's menus, so an unexpected dialog cannot
    ///     be answered with something unintended. "(Complete)" marks the mahatmas
    ///     already awakened in his list.
    /// </summary>
    private static readonly string[] IgnoredEntries = ["cancel", "nothing", "(complete)"];

    /// <summary>
    ///     Get Remon at Swiftperch, if he is loaded.
    /// </summary>
    /// <param name="name">Name of the NPC, in the client's language.</param>
    /// <returns>The NPC, or null when he is out of object table range.</returns>
    public static IGameObject? FindNpcByName(string name)
        => Service.ObjectTable.FirstOrDefault(
            o => o.ObjectKind == ObjectKind.EventNpc
                 && o.IsTargetable
                 && string.Equals(o.Name.TextValue, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    ///     Check whether Remon's dialogue currently holds a window, so the NPC is
    ///     not interacted with again while a menu is being worked through.
    /// </summary>
    /// <returns>Whether a dialogue window is open.</returns>
    public static bool IsDialogOpen()
        => GetReadyAddon("SelectString") != null
           || GetReadyAddon("SelectIconString") != null
           || GetReadyAddon("SelectYesno") != null
           || GetReadyAddon("Talk") != null;

    /// <summary>
    ///     Select an entry of the open SelectString or SelectIconString menu,
    ///     trying the needles in order so the most specific one wins. Entries like
    ///     "Cancel", "Nothing" or an already awakened "(Complete)" mahatma are
    ///     never selected.
    /// </summary>
    /// <param name="needles">Texts to look for, most specific first.</param>
    /// <returns>The text of the selected entry, or null when nothing matched.</returns>
    public static string? SelectDialogEntry(params string[] needles)
    {
        foreach (var needle in needles)
        {
            if (needle.Length == 0)
            {
                continue;
            }

            var addon = GetReadyAddon("SelectString");
            if (addon != null)
            {
                foreach (var entry in new AddonMaster.SelectString((nint)addon).Entries)
                {
                    if (Matches(entry.Text, needle))
                    {
                        entry.Select();
                        return entry.Text;
                    }
                }
            }

            addon = GetReadyAddon("SelectIconString");
            if (addon != null)
            {
                foreach (var entry in new AddonMaster.SelectIconString((nint)addon).Entries)
                {
                    if (Matches(entry.Text, needle))
                    {
                        entry.Select();
                        return entry.Text;
                    }
                }
            }
        }

        return null;
    }

    /// <summary>
    ///     Get the entries of the open SelectString or SelectIconString menu, for
    ///     diagnostics when none of them matched.
    /// </summary>
    /// <returns>The entry texts, or an empty list when no menu is open.</returns>
    public static List<string> GetOpenMenuEntries()
    {
        var entries = new List<string>();

        var addon = GetReadyAddon("SelectString");
        if (addon != null)
        {
            entries.AddRange(new AddonMaster.SelectString((nint)addon).Entries.Select(e => e.Text));
        }

        addon = GetReadyAddon("SelectIconString");
        if (addon != null)
        {
            entries.AddRange(new AddonMaster.SelectIconString((nint)addon).Entries.Select(e => e.Text));
        }

        return entries;
    }

    /// <summary>
    ///     Confirm the SelectYesno dialog if it is about a mahatma or tomestone
    ///     exchange. Any other confirmation is left alone.
    /// </summary>
    /// <returns>Whether a dialog was confirmed.</returns>
    public static bool ConfirmPurchaseYesNo()
    {
        var addon = GetReadyAddon("SelectYesno");
        if (addon == null)
        {
            return false;
        }

        var master = new AddonMaster.SelectYesno((nint)addon);
        var text = master.Text;
        if (!text.Contains("mahatma", StringComparison.OrdinalIgnoreCase)
            && !text.Contains("tomestone", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        master.Yes();
        return true;
    }

    /// <summary>
    ///     Close Remon's menu if one is still open, e.g. the mahatma list he
    ///     returns to after a purchase.
    /// </summary>
    /// <returns>Whether a menu was closed.</returns>
    public static bool CloseMenu()
    {
        foreach (var name in new[] { "SelectString", "SelectIconString" })
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

    private static bool Matches(string entryText, string needle)
    {
        if (IgnoredEntries.Any(ignored => entryText.Contains(ignored, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        return entryText.Contains(needle, StringComparison.OrdinalIgnoreCase);
    }

    private static AtkUnitBase* GetReadyAddon(string name)
    {
        var addon = RaptureAtkUnitManager.Instance()->GetAddonByName(name);
        return addon != null && addon->IsVisible && addon->IsReady && addon->IsFullyLoaded() ? addon : null;
    }
}
