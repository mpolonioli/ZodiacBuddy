using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.Game.Fate;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System.Linq;
using CSGameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace ZodiacBuddy.Stages.Atma.Automation;

/// <summary>
///     Native game interactions used by the FATEs automation: talking to NPCs,
///     advancing dialogue and handing collectables over. Ported from the
///     bundleoftweaks clib helpers.
/// </summary>
internal static unsafe class FateGameActions
{
    private const uint InvalidEntityId = 0xE0000000;

    /// <summary>
    ///     Get the FATE an object belongs to.
    /// </summary>
    /// <param name="obj">Object to inspect.</param>
    /// <returns>The Fate row ID, or 0 when the object belongs to no FATE.</returns>
    public static ushort GetObjectFateId(IGameObject obj)
        => ((CSGameObject*)obj.Address)->FateId;

    /// <summary>
    ///     Get the NPC that starts the given FATE when spoken to, if it is loaded.
    /// </summary>
    /// <param name="fateId">Fate row ID.</param>
    /// <returns>The NPC, or null when the FATE has none or it is out of range.</returns>
    public static IGameObject? GetMotivationNpc(uint fateId)
    {
        var fate = FateManager.Instance()->GetFateById((ushort)fateId);
        if (fate == null)
        {
            return null;
        }

        var entityId = fate->MotivationNpc;
        if (entityId is 0 or InvalidEntityId)
        {
            return null;
        }

        // Freshly spawned NPCs briefly appear as garbage objects of another kind.
        return Service.ObjectTable.FirstOrDefault(
            o => o.EntityId == entityId && o.ObjectKind == ObjectKind.BattleNpc && o.IsTargetable);
    }

    /// <summary>
    ///     Get the NPC that receives the collectables of the given FATE, if it is loaded.
    /// </summary>
    /// <param name="fateId">Fate row ID.</param>
    /// <returns>The NPC, or null when the FATE has none or it is out of range.</returns>
    public static IGameObject? GetObjectiveNpc(uint fateId)
    {
        var fate = FateManager.Instance()->GetFateById((ushort)fateId);
        if (fate == null)
        {
            return null;
        }

        var entityId = fate->ObjectiveNpc;
        if (entityId is 0 or InvalidEntityId)
        {
            return null;
        }

        return Service.ObjectTable.FirstOrDefault(o => o.EntityId == entityId && o.IsTargetable);
    }

    /// <summary>
    ///     Check whether the player is currently level synced to the given FATE.
    /// </summary>
    /// <param name="fateId">Fate row ID.</param>
    /// <returns>Whether the player is synced to it.</returns>
    public static bool IsSyncedTo(uint fateId)
        => FateManager.Instance()->SyncedFateId == fateId;

    /// <summary>
    ///     Check whether the game considers the player inside the given FATE.
    /// </summary>
    /// <param name="fateId">Fate row ID.</param>
    /// <returns>Whether the player is inside it.</returns>
    public static bool IsInsideFate(uint fateId)
    {
        var current = FateManager.Instance()->CurrentFate;
        return current != null && current->FateId == fateId;
    }

    /// <summary>
    ///     Apply level sync to the FATE the player is currently inside, like
    ///     clicking the "Level Sync" button under the FATE widget.
    /// </summary>
    public static void LevelSync()
        => FateManager.Instance()->LevelSync();

    /// <summary>
    ///     Check whether an addon is visible and ready for input.
    /// </summary>
    /// <param name="name">Internal name of the addon.</param>
    /// <returns>Whether the addon is ready.</returns>
    public static bool IsAddonReady(string name)
    {
        var addon = RaptureAtkUnitManager.Instance()->GetAddonByName(name);
        return addon != null && addon->IsVisible && addon->IsReady && addon->IsFullyLoaded();
    }

    /// <summary>
    ///     Check whether the player is close enough to interact with an object.
    /// </summary>
    /// <param name="obj">Object to interact with.</param>
    /// <returns>Whether the object is in interact range.</returns>
    public static bool IsInInteractRange(IGameObject obj)
    {
        var player = Service.ObjectTable.LocalPlayer;
        return player is not null
               && EventFramework.Instance()->CheckInteractRange(
                   (CSGameObject*)player.Address, (CSGameObject*)obj.Address, 1, false);
    }

    /// <summary>
    ///     Interact with an object, like clicking it.
    /// </summary>
    /// <param name="obj">Object to interact with.</param>
    public static void InteractWith(IGameObject obj)
        => TargetSystem.Instance()->InteractWithObject((CSGameObject*)obj.Address, false);

    /// <summary>
    ///     Advance the Talk dialogue if one is open.
    /// </summary>
    /// <returns>Whether a dialogue was advanced.</returns>
    public static bool ProgressTalk()
    {
        var addon = RaptureAtkUnitManager.Instance()->GetAddonByName("Talk");
        if (addon == null || !addon->IsVisible || !addon->IsReady)
        {
            return false;
        }

        var evt = new AtkEvent { Listener = &addon->AtkEventListener, Target = &AtkStage.Instance()->AtkEventTarget };
        var data = new AtkEventData();
        addon->ReceiveEvent(AtkEventType.MouseClick, 0, &evt, &data);
        return true;
    }

    /// <summary>
    ///     Confirm the SelectYesno dialog if one is open.
    /// </summary>
    /// <returns>Whether a dialog was confirmed.</returns>
    public static bool ConfirmYesNo()
    {
        var addon = RaptureAtkUnitManager.Instance()->GetAddonByName("SelectYesno");
        if (addon == null || !addon->IsVisible || !addon->IsReady)
        {
            return false;
        }

        var evt = new AtkEvent { Listener = &addon->AtkEventListener, Target = &AtkStage.Instance()->AtkEventTarget };
        var data = new AtkEventData();
        addon->ReceiveEvent(AtkEventType.ButtonClick, 0, &evt, &data);
        return true;
    }

    /// <summary>
    ///     Fill and confirm the open hand-over Request window with the requested
    ///     items, e.g. the collectables of a collect FATE.
    /// </summary>
    /// <returns>Whether the hand-over was committed.</returns>
    public static bool TurnInRequests()
    {
        var agent = AgentNpcTrade.Instance();
        if (!agent->IsAgentActive())
        {
            return false;
        }

        if (agent->SelectedTurnInSlot >= 0)
        {
            Service.PluginLog.Debug($"[FateAutomation] Turn-in already in progress for slot {agent->SelectedTurnInSlot}.");
            return false;
        }

        var requests = &UIState.Instance()->NpcTrade.Requests;
        var res = new AtkValue();
        var param = stackalloc AtkValue[4];
        param[0].SetInt(2); // start turn-in
        param[2].SetInt(0);
        param[3].SetInt(0);
        for (var i = 0; i < requests->Count; i++)
        {
            param[1].SetInt(i); // slot
            agent->ReceiveEvent(&res, param, 4, 0);
        }

        if (agent->SelectedTurnInSlot != 0 || agent->SelectedTurnInSlotItemOptions <= 0)
        {
            Service.PluginLog.Debug(
                $"[FateAutomation] Could not start the turn-in: slot={agent->SelectedTurnInSlot}, options={agent->SelectedTurnInSlotItemOptions}.");
            return false;
        }

        param[0].SetInt(0); // confirm
        param[1].SetInt(0); // option #0
        agent->ReceiveEvent(&res, param, 4, 1);

        if (agent->SelectedTurnInSlot >= 0)
        {
            Service.PluginLog.Debug($"[FateAutomation] Turn-in not confirmed: slot={agent->SelectedTurnInSlot}.");
            return false;
        }

        // Commit and close the window.
        var addonId = agent->AddonId;
        agent->ReceiveEvent(&res, param, 4, 0);
        var addon = RaptureAtkUnitManager.Instance()->GetAddonById((ushort)addonId);
        if (addon != null && addon->IsVisible)
        {
            addon->Close(false);
        }

        return true;
    }
}
