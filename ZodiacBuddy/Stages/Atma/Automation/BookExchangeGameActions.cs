using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace ZodiacBuddy.Stages.Atma.Automation;

/// <summary>
///     Native game interactions used by the book exchange automation: finding
///     G'jusana and driving the Trials of the Braves exchange dialogs.
/// </summary>
internal static unsafe partial class BookExchangeGameActions
{
    /// <summary>
    ///     Entries never selected in the exchange menus, so an unexpected dialog
    ///     cannot be answered with something unintended.
    /// </summary>
    private static readonly string[] IgnoredEntries = ["cancel", "nothing", "quit"];

    /// <summary>
    ///     Get an NPC by name, if it is loaded.
    /// </summary>
    /// <param name="name">Name of the NPC, in the client's language.</param>
    /// <returns>The NPC, or null when it is out of object table range.</returns>
    public static IGameObject? FindNpcByName(string name)
        => Service.ObjectTable.FirstOrDefault(
            o => o.ObjectKind == ObjectKind.EventNpc
                 && o.IsTargetable
                 && string.Equals(o.Name.TextValue, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    ///     Check whether a dialogue window is currently open, so the NPC is not
    ///     interacted with again while a menu is being worked through.
    /// </summary>
    /// <returns>Whether a dialogue window is open.</returns>
    public static bool IsDialogOpen()
        => GetReadyAddon("SelectString") != null
           || GetReadyAddon("SelectIconString") != null
           || GetReadyAddon("SelectYesno") != null
           || GetReadyAddon("Talk") != null;

    /// <summary>
    ///     Select the first entry of the open menu that lists a book category with
    ///     books left to complete, e.g. "The Books of Fire (Rises Strength)
    ///     Completed: 1 of 3". Categories whose books are all completed are left
    ///     alone.
    /// </summary>
    /// <param name="anyCategoryListed">Whether the open menu lists categories at
    ///     all, whether or not one of them still has books to complete.</param>
    /// <returns>The text of the selected entry, or null when nothing was selected.</returns>
    public static string? SelectIncompleteCategory(out bool anyCategoryListed)
    {
        anyCategoryListed = false;

        foreach (var entry in GetOpenMenuEntries())
        {
            var match = CompletionCountRegex().Match(entry.Text);
            if (!match.Success)
            {
                continue;
            }

            anyCategoryListed = true;
            if (int.Parse(match.Groups[1].Value) >= int.Parse(match.Groups[2].Value))
            {
                continue;
            }

            entry.Select();
            return entry.Text;
        }

        return null;
    }

    /// <summary>
    ///     Select an entry of the open menu, trying the needles in order so the
    ///     most specific one wins. Entries like "Cancel" or "Nothing" are never
    ///     selected.
    /// </summary>
    /// <param name="needles">Texts to look for, most specific first.</param>
    /// <returns>The text of the selected entry, or null when nothing matched.</returns>
    public static string? SelectMenuEntry(params string[] needles)
    {
        foreach (var needle in needles)
        {
            if (needle.Length == 0)
            {
                continue;
            }

            foreach (var entry in GetOpenMenuEntries())
            {
                if (IgnoredEntries.Any(ignored => entry.Text.Contains(ignored, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                if (entry.Text.Contains(needle, StringComparison.OrdinalIgnoreCase))
                {
                    entry.Select();
                    return entry.Text;
                }
            }
        }

        return null;
    }

    /// <summary>
    ///     Confirm the SelectYesno dialog if it is about the book exchange. Any
    ///     other confirmation is left alone.
    /// </summary>
    /// <returns>Whether a dialog was confirmed.</returns>
    public static bool ConfirmExchangeYesNo()
    {
        var addon = GetReadyAddon("SelectYesno");
        if (addon == null)
        {
            return false;
        }

        var master = new AddonMaster.SelectYesno((nint)addon);
        var text = master.Text;
        if (!text.Contains("tomestone", StringComparison.OrdinalIgnoreCase)
            && !text.Contains("poetics", StringComparison.OrdinalIgnoreCase)
            && !text.Contains("braves", StringComparison.OrdinalIgnoreCase)
            && !text.Contains("book", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        master.Yes();
        return true;
    }

    /// <summary>
    ///     Get the texts of the entries of the open menu, for diagnostics when
    ///     none of them matched.
    /// </summary>
    /// <returns>The entry texts, or an empty list when no menu is open.</returns>
    public static List<string> GetOpenMenuEntryTexts()
        => GetOpenMenuEntries().Select(e => e.Text).ToList();

    /// <summary>
    ///     Close the exchange menu if one is still open, e.g. the category list
    ///     G'jusana returns to after handing a book over.
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

    /// <summary>
    ///     Number of books completed out of the books of a category, as the
    ///     exchange lists it next to each category ("Completed: 1 of 3").
    /// </summary>
    /// <returns>The compiled expression.</returns>
    [GeneratedRegex(@"(\d+)\s*(?:of|/)\s*(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex CompletionCountRegex();

    /// <summary>
    ///     Get the entries of the open SelectString or SelectIconString menu as a
    ///     uniform, selectable list.
    /// </summary>
    /// <returns>The entries, or an empty list when no menu is open.</returns>
    private static List<MenuEntry> GetOpenMenuEntries()
    {
        var entries = new List<MenuEntry>();

        var addon = GetReadyAddon("SelectString");
        if (addon != null)
        {
            entries.AddRange(new AddonMaster.SelectString((nint)addon).Entries
                .Select(e => new MenuEntry(e.Text, e.Select)));
        }

        addon = GetReadyAddon("SelectIconString");
        if (addon != null)
        {
            entries.AddRange(new AddonMaster.SelectIconString((nint)addon).Entries
                .Select(e => new MenuEntry(e.Text, e.Select)));
        }

        return entries;
    }

    private static AtkUnitBase* GetReadyAddon(string name)
    {
        var addon = RaptureAtkUnitManager.Instance()->GetAddonByName(name);
        return addon != null && addon->IsVisible && addon->IsReady && addon->IsFullyLoaded() ? addon : null;
    }

    /// <summary>
    ///     An entry of the open menu, whichever addon lists it.
    /// </summary>
    /// <param name="Text">Text of the entry.</param>
    /// <param name="Select">Selects the entry.</param>
    private sealed record MenuEntry(string Text, Action Select);
}
