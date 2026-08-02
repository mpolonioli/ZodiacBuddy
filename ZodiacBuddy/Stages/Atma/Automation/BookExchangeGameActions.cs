using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace ZodiacBuddy.Stages.Atma.Automation;

/// <summary>
///     Native game interactions used by the book exchange automation: finding
///     G'jusana and driving the Trials of the Braves exchange dialogs.
/// </summary>
internal static unsafe partial class BookExchangeGameActions
{
    /// <summary>
    ///     Sheet holding every line G'jusana speaks in the Trials of the Braves
    ///     exchange, so the ones that end it without a book are recognized in
    ///     whatever language the client runs in.
    /// </summary>
    private const string ExchangeTextSheet = "custom/001/CmnDefRelicWeapon025GetNote_00167";

    /// <summary>
    ///     Column of <see cref="ExchangeTextSheet" /> holding the line itself; the
    ///     first column is the internal key of the line.
    /// </summary>
    private const int ExchangeTextColumn = 1;

    /// <summary>
    ///     "...Hm? It appears we're completely out of books pertaining to that
    ///     weapon. And small wonder, seeing as you're the one who cleaned us out!"
    /// </summary>
    private const uint OutOfBooksRow = 17;

    /// <summary>
    ///     "You have already completed all the trials for the equipped relic weapon
    ///     atma. Make your way to Hyrstmill, and present it to Jalzahn."
    /// </summary>
    private const uint AllTrialsCompleteRow = 18;

    /// <summary>
    ///     "You must complete all of the objectives in a book before you may
    ///     purchase another. [...]"
    /// </summary>
    private const uint BookNotCompleteRow = 21;

    /// <summary>
    ///     Entries never selected in the exchange menus, so an unexpected dialog
    ///     cannot be answered with something unintended.
    /// </summary>
    private static readonly string[] IgnoredEntries = ["cancel", "nothing", "quit"];

    /// <summary>
    ///     Distinctive part of each recognized line, used when the line cannot be
    ///     read from the sheet, e.g. after a patch moved it.
    /// </summary>
    private static readonly Dictionary<uint, string> LineFallbacks = new()
    {
        [OutOfBooksRow] = "completely out of books",
        [AllTrialsCompleteRow] = "already completed all the trials",
        [BookNotCompleteRow] = "complete all of the objectives",
    };

    /// <summary>
    ///     Lines read from <see cref="ExchangeTextSheet" />, kept so the sheet is
    ///     only read once per line rather than on every dialogue tick.
    /// </summary>
    private static readonly ConcurrentDictionary<uint, string> ExchangeLines = new();

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
    ///     Check whether G'jusana is saying she has no book left to hand over for
    ///     the equipped relic, which she does instead of opening the category list
    ///     once every trial of the relic is completed.
    /// </summary>
    /// <returns>Whether the exchange ran dry.</returns>
    public static bool IsOutOfBooksTalk()
        => TalkMatchesLine(OutOfBooksRow) || TalkMatchesLine(AllTrialsCompleteRow);

    /// <summary>
    ///     Check whether G'jusana is refusing to hand a book over because the one
    ///     already carried is not finished.
    /// </summary>
    /// <returns>Whether the current book still has objectives left.</returns>
    public static bool IsBookNotCompleteTalk()
        => TalkMatchesLine(BookNotCompleteRow);

    /// <summary>
    ///     Get everything the open dialogue box is showing, speaker name included.
    ///     Every text node is read rather than one known node, so a changed layout
    ///     costs nothing.
    /// </summary>
    /// <returns>The dialogue text, or an empty string when no dialogue is open.</returns>
    public static string GetTalkText()
    {
        var addon = GetReadyAddon("Talk");
        if (addon == null)
        {
            return string.Empty;
        }

        var text = new StringBuilder();
        for (var i = 0; i < addon->UldManager.NodeListCount; i++)
        {
            var node = addon->UldManager.NodeList[i];
            if (node == null || node->Type != NodeType.Text)
            {
                continue;
            }

            text.Append(node->GetAsAtkTextNode()->NodeText.ToString()).Append(' ');
        }

        return text.ToString();
    }

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
    ///     Runs of whitespace, which the dialogue box wraps its lines with in
    ///     places the sheet does not.
    /// </summary>
    /// <returns>The compiled expression.</returns>
    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    /// <summary>
    ///     Check whether the open dialogue box is showing a known line of the
    ///     exchange.
    /// </summary>
    /// <param name="rowId">Row of <see cref="ExchangeTextSheet" /> to look for.</param>
    /// <returns>Whether the line is being spoken right now.</returns>
    private static bool TalkMatchesLine(uint rowId)
    {
        var talk = Normalize(GetTalkText());
        if (talk.Length == 0)
        {
            return false;
        }

        // The whole line as the game words it and, should the box have rewrapped
        // it beyond recognition, the distinctive part of the English line it was
        // written against.
        return TalkContains(talk, GetExchangeLine(rowId))
               || TalkContains(talk, LineFallbacks.GetValueOrDefault(rowId, string.Empty));
    }

    /// <summary>
    ///     Check whether dialogue holds a line, ignoring how either of them is
    ///     wrapped.
    /// </summary>
    /// <param name="talk">Normalized dialogue text.</param>
    /// <param name="line">Line to look for.</param>
    /// <returns>Whether the dialogue holds the line.</returns>
    private static bool TalkContains(string talk, string line)
    {
        var needle = Normalize(line);
        return needle.Length > 0 && talk.Contains(needle, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Get a line of the exchange from the game's own text, falling back to the
    ///     distinctive part of the English line when the sheet cannot be read.
    /// </summary>
    /// <param name="rowId">Row of <see cref="ExchangeTextSheet" /> to read.</param>
    /// <returns>The line, or an empty string when it is unknown.</returns>
    private static string GetExchangeLine(uint rowId)
        => ExchangeLines.GetOrAdd(rowId, static id =>
        {
            try
            {
                var sheet = Service.DataManager.GetExcelSheet<RawRow>(null, ExchangeTextSheet);
                if (sheet.TryGetRow(id, out var row))
                {
                    var text = row.ReadStringColumn(ExchangeTextColumn).ExtractText();
                    if (text.Length > 0)
                    {
                        return text;
                    }
                }

                Service.PluginLog.Warning($"[BookExchange] Line {id} of {ExchangeTextSheet} is missing.");
            }
            catch (Exception ex)
            {
                Service.PluginLog.Warning(ex, $"Could not read line {id} of {ExchangeTextSheet}.");
            }

            return LineFallbacks.GetValueOrDefault(id, string.Empty);
        });

    /// <summary>
    ///     Collapse the whitespace of a line, so a dialogue box that wrapped it
    ///     still matches the sheet it came from.
    /// </summary>
    /// <param name="text">Text to normalize.</param>
    /// <returns>The normalized text.</returns>
    private static string Normalize(string text)
        => WhitespaceRegex().Replace(text, " ").Trim();

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
