using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Memory;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System;
using System.Linq;
using System.Text;

namespace ZodiacBuddy.Stages.Atma.Automation;

/// <summary>
///     Native game interactions used by the levequests automation: reading the
///     journal's levequest state and driving the levemete hand-in dialogs.
/// </summary>
internal static unsafe class LeveGameActions
{
    /// <summary>
    ///     Journal sequence value of a levequest whose objectives are complete,
    ///     making it ready to be handed in at the levemete.
    /// </summary>
    public const byte ReadyToHandInSequence = 255;

    /// <summary>
    ///     Journal sequence value of a failed levequest.
    /// </summary>
    public const byte FailedSequence = 3;

    /// <summary>
    ///     Get the journal sequence of a levequest.
    /// </summary>
    /// <param name="leveId">Leve row ID.</param>
    /// <returns>The sequence, or null when the levequest is not in the journal.</returns>
    public static byte? GetLeveSequence(uint leveId)
    {
        var leveWork = QuestManager.Instance()->GetLeveQuestById((ushort)leveId);
        return leveWork == null ? null : leveWork->Sequence;
    }

    /// <summary>
    ///     Get the levemete NPC that issued a levequest, if it is loaded.
    /// </summary>
    /// <param name="issuerId">ENpc data ID of the issuer.</param>
    /// <returns>The NPC, or null when it is out of object table range.</returns>
    public static IGameObject? GetIssuerNpc(uint issuerId)
        => Service.ObjectTable.FirstOrDefault(
            o => o.BaseId == issuerId && o.ObjectKind == ObjectKind.EventNpc && o.IsTargetable);

    /// <summary>
    ///     Click the "Complete" button of the open JournalResult window, claiming
    ///     the levequest reward.
    /// </summary>
    /// <returns>Whether the window was open and the button was clicked.</returns>
    public static bool CompleteJournalResult()
    {
        var addon = GetReadyAddon("JournalResult");
        if (addon == null)
        {
            return false;
        }

        new AddonMaster.JournalResult((nint)addon).Complete();
        return true;
    }

    /// <summary>
    ///     Click the "Decline" button of the open JournalResult window. The
    ///     levequest stays completed in the journal and can be collected later.
    /// </summary>
    /// <returns>Whether the window was open and the button was clicked.</returns>
    public static bool DeclineJournalResult()
    {
        var addon = GetReadyAddon("JournalResult");
        if (addon == null)
        {
            return false;
        }

        new AddonMaster.JournalResult((nint)addon).Decline();
        return true;
    }

    /// <summary>
    ///     Get the text shown by the open JournalResult reward window, which
    ///     includes the name of the levequest it belongs to.
    /// </summary>
    /// <returns>The window's string values, or null when the window is not open.</returns>
    public static string? GetJournalResultText()
    {
        var addon = GetReadyAddon("JournalResult");
        if (addon == null)
        {
            return null;
        }

        var builder = new StringBuilder();
        for (var i = 0; i < addon->AtkValuesCount; i++)
        {
            var value = addon->AtkValues[i];
            var type = (AtkValueType)((byte)value.Type & (byte)AtkValueType.TypeMask);
            if (type is AtkValueType.String or AtkValueType.ConstString && value.String.HasValue)
            {
                builder.Append(MemoryHelper.ReadSeStringNullTerminated((nint)value.String.Value).TextValue);
                builder.Append('\n');
            }
        }

        return builder.ToString();
    }

    /// <summary>
    ///     Select the entry matching the given levequest name in the open
    ///     SelectIconString hand-in list. Never selects a non-matching entry, so
    ///     unrelated completed levequests cannot be handed in by accident.
    /// </summary>
    /// <param name="leveName">Name of the levequest to hand in.</param>
    /// <returns>Whether a matching entry was found and selected.</returns>
    public static bool SelectHandInEntry(string leveName)
    {
        var addon = GetReadyAddon("SelectIconString");
        if (addon == null)
        {
            return false;
        }

        foreach (var entry in new AddonMaster.SelectIconString((nint)addon).Entries)
        {
            if (entry.Text.Contains(leveName, StringComparison.OrdinalIgnoreCase))
            {
                entry.Select();
                return true;
            }
        }

        return false;
    }

    /// <summary>
    ///     Drive the levemete's SelectString menu toward the hand-in: select the
    ///     given levequest if it is listed directly, otherwise the "Collect
    ///     Reward." entry that leads to the list of completed levequests. Never
    ///     selects anything else.
    /// </summary>
    /// <param name="leveName">Name of the levequest to hand in.</param>
    /// <returns>Whether an entry was selected.</returns>
    public static bool SelectMenuEntry(string leveName)
    {
        var addon = GetReadyAddon("SelectString");
        if (addon == null)
        {
            return false;
        }

        var entries = new AddonMaster.SelectString((nint)addon).Entries;
        foreach (var entry in entries)
        {
            if (entry.Text.Contains(leveName, StringComparison.OrdinalIgnoreCase))
            {
                entry.Select();
                return true;
            }
        }

        foreach (var entry in entries)
        {
            if (entry.Text.Contains("Collect Reward", StringComparison.OrdinalIgnoreCase))
            {
                entry.Select();
                return true;
            }
        }

        return false;
    }

    /// <summary>
    ///     Close the levemete's SelectString menu if it is open.
    /// </summary>
    /// <returns>Whether the menu was open.</returns>
    public static bool CloseMenu()
    {
        var addon = GetReadyAddon("SelectString");
        if (addon == null)
        {
            return false;
        }

        addon->Close(true);
        return true;
    }

    /// <summary>
    ///     Close the levequest offer window if it is open. It opens instead of the
    ///     hand-in list when the levemete has nothing to receive.
    /// </summary>
    /// <returns>Whether the window was open.</returns>
    public static bool CloseLeveOfferWindow()
    {
        var addon = RaptureAtkUnitManager.Instance()->GetAddonByName("GuildLeve");
        if (addon == null || !addon->IsVisible)
        {
            return false;
        }

        addon->Close(true);
        return true;
    }

    private static AtkUnitBase* GetReadyAddon(string name)
    {
        var addon = RaptureAtkUnitManager.Instance()->GetAddonByName(name);
        return addon != null && addon->IsVisible && addon->IsReady && addon->IsFullyLoaded() ? addon : null;
    }
}
