using Dalamud.Bindings.ImGui;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Windowing;
using System;
using System.Numerics;
using ZodiacBuddy.Stages.Atma.Data;

namespace ZodiacBuddy.Stages.Atma.Automation;

/// <summary>
///     Status window of the Trial of the Braves automation, split into an Enemies
///     tab (driven by this plugin), a Dungeons tab (run through AutoDuty), a
///     FATEs tab and a Levequests tab (driven by this plugin).
/// </summary>
internal sealed class AtmaAutomationWindow : Window, IDisposable
{
    private readonly AtmaAutomationManager manager;
    private readonly DungeonAutomationManager dungeonManager;
    private readonly FateAutomationManager fateManager;
    private readonly LeveAutomationManager leveManager;
    private readonly BookExchangeManager bookExchangeManager;

    private bool navmeshInstalled;
    private bool combatAvailable;
    private bool autoDutyInstalled;

    /// <summary>
    ///     Initializes a new instance of the <see cref="AtmaAutomationWindow" /> class.
    /// </summary>
    /// <param name="manager">The enemies automation manager driven by this window.</param>
    /// <param name="dungeonManager">The dungeons automation manager driven by this window.</param>
    /// <param name="fateManager">The FATEs automation manager driven by this window.</param>
    /// <param name="leveManager">The levequests automation manager driven by this window.</param>
    /// <param name="bookExchangeManager">The book exchange automation, whose progress is shown by this window.</param>
    public AtmaAutomationWindow(AtmaAutomationManager manager, DungeonAutomationManager dungeonManager, FateAutomationManager fateManager, LeveAutomationManager leveManager, BookExchangeManager bookExchangeManager)
        : base("Trial of the Braves Automation###ZodiacBuddyAtmaAutomation")
    {
        this.manager = manager;
        this.dungeonManager = dungeonManager;
        this.fateManager = fateManager;
        this.leveManager = leveManager;
        this.bookExchangeManager = bookExchangeManager;

        this.RespectCloseHotkey = true;
        this.SizeCondition = ImGuiCond.FirstUseEver;
        this.Size = new Vector2(480, 420);

        Service.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "RelicNoteBook", this.OnBookOpened);
        Service.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "RelicNoteBook", this.OnBookClosed);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Service.AddonLifecycle.UnregisterListener(this.OnBookOpened);
        Service.AddonLifecycle.UnregisterListener(this.OnBookClosed);
    }

    /// <inheritdoc />
    public override void OnOpen()
    {
        // The dependency checks go through IPC and the plugin list; only probe
        // them when the window opens instead of every frame.
        this.navmeshInstalled = NavmeshIpc.IsInstalled;
        this.combatAvailable = CombatIpc.IsAvailable();
        this.autoDutyInstalled = AutoDutyIpc.IsInstalled;
    }

    /// <inheritdoc />
    public override void Draw()
    {
        var bookId = AtmaAutomationManager.GetActiveBookId();
        if (bookId == 0)
        {
            ImGui.TextColored(ImGuiColors.DalamudGrey, "No Trial of the Braves book is active. Equip your Zodiac weapon.");
            this.DrawBookExchangeStatus();
            return;
        }

        var book = BraveBook.GetValue(bookId);
        ImGui.Text($"Book: {book.Name}");

        var chain = Service.Configuration.AtmaAutomation.ChainAutomations;
        if (ImGui.Checkbox("Chain steps", ref chain))
        {
            Service.Configuration.AtmaAutomation.ChainAutomations = chain;
            Service.Configuration.Save();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "When a step finishes, automatically start the next book step that\n" +
                "still has work to do, wrapping around the order (Enemies, Dungeons,\n" +
                "FATEs, Levequests). Press the Start button of any step to run the\n" +
                "whole book from there.");
        }

        var chainBooks = Service.Configuration.AtmaAutomation.ChainBooks;
        if (ImGui.Checkbox("Chain books", ref chainBooks))
        {
            Service.Configuration.AtmaAutomation.ChainBooks = chainBooks;
            Service.Configuration.Save();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "Once every page of the book is complete, travel to G'jusana in Mor\n" +
                "Dhona and take a new book from the first category that still has\n" +
                "books left, spending 100 Allagan tomestones of poetics. With\n" +
                "\"Chain steps\" on, the new book is then worked through as well.");
        }

        this.DrawCombatPluginSelector();
        this.DrawBookExchangeStatus();

        ImGui.Spacing();

        if (!ImGui.BeginTabBar("##AtmaAutomationTabs"))
        {
            return;
        }

        if (ImGui.BeginTabItem("Enemies"))
        {
            this.DrawEnemiesTab(book);
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("Dungeons"))
        {
            this.DrawDungeonsTab(book);
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("FATEs"))
        {
            this.DrawFatesTab(book);
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("Leves"))
        {
            this.DrawLevesTab(book);
            ImGui.EndTabItem();
        }

        ImGui.EndTabBar();
    }

    private static void DrawStatusLine(string label, bool ok, string okText, string errorText)
    {
        ImGui.Text(label);
        ImGui.SameLine();
        ImGui.TextColored(ok ? ImGuiColors.HealerGreen : ImGuiColors.DalamudRed, ok ? okText : errorText);
    }

    private void DrawCombatPluginSelector()
    {
        var configuration = Service.Configuration.AtmaAutomation;
        ImGui.BeginDisabled(this.AnyStepRunning());
        ImGui.SetNextItemWidth(140f);
        if (ImGui.BeginCombo("Combat plugin", CombatIpc.GetName(configuration.CombatPlugin)))
        {
            foreach (var plugin in new[] { CombatPlugin.BossMod, CombatPlugin.WrathCombo })
            {
                if (ImGui.Selectable(CombatIpc.GetName(plugin), plugin == configuration.CombatPlugin))
                {
                    configuration.CombatPlugin = plugin;
                    Service.Configuration.Save();
                    this.combatAvailable = CombatIpc.IsAvailable();
                }
            }

            ImGui.EndCombo();
        }

        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.SetTooltip(
                "The plugin that fights during the Enemies, FATEs and Leves steps.\n" +
                "BossMod runs the rotation and dodges AoEs; Wrath Combo only runs\n" +
                "the rotation. Cannot be changed while an automation is running.");
        }
    }

    /// <summary>
    ///     Show what the book exchange is doing, so the trip to G'jusana is not a
    ///     black box, and let it be stopped like any other step.
    /// </summary>
    private void DrawBookExchangeStatus()
    {
        var exchange = this.bookExchangeManager;
        ImGui.Separator();

        if (exchange.IsRunning)
        {
            if (ImGui.Button("Stop##BookExchange"))
            {
                exchange.Stop("stopped by user.");
            }

            ImGui.SameLine();
            ImGui.Text($"New book: {exchange.State}");

            if (exchange.StatusDetail.Length > 0)
            {
                ImGui.Text(exchange.StatusDetail);
            }

            return;
        }

        // The trip to G'jusana can also be made on its own, without waiting for a
        // step to finish and chain onto it.
        var canStart = BookExchangeManager.CanStart(out var reason);
        if (canStart && exchange.EveryBookCompleted)
        {
            canStart = false;
            reason = BookExchangeManager.NoBooksLeftMessage;
        }

        if (canStart && !AutomationChainManager.IsBookComplete())
        {
            canStart = false;
            reason = "The current book still has work to do.";
        }

        if (canStart && this.AnyStepRunning())
        {
            canStart = false;
            reason = "Another automation is running.";
        }

        ImGui.BeginDisabled(!canStart);
        if (ImGui.Button("Take a new book"))
        {
            exchange.Start();
        }

        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.SetTooltip(canStart
                ? "Travel to G'jusana in Mor Dhona and take a new book, without\n" +
                  "waiting for \"Chain books\" to do it at the end of a run."
                : reason);
        }

        if (exchange.State == BookExchangeState.Errored && exchange.LastError.Length > 0)
        {
            ImGui.TextColored(ImGuiColors.DalamudRed, exchange.LastError);
        }
        else if (exchange.EveryBookCompleted)
        {
            ImGui.TextWrapped(BookExchangeManager.NoBooksLeftMessage);
        }
    }

    private bool AnyStepRunning()
        => this.manager.IsRunning
           || this.dungeonManager.IsRunning
           || this.fateManager.IsRunning
           || this.leveManager.IsRunning;

    private void DrawEnemiesTab(BraveBook book)
    {
        DrawStatusLine("vnavmesh:", this.navmeshInstalled, "Installed", "Not installed");
        DrawStatusLine($"{CombatIpc.ConfiguredName}:", this.combatAvailable, "Ready", "Not available");
        ImGui.Separator();

        this.DrawEnemiesTable(book);

        ImGui.Separator();
        this.DrawControls();
    }

    private void DrawEnemiesTable(BraveBook book)
    {
        if (!ImGui.BeginTable("##AtmaAutomationEnemies", 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
        {
            return;
        }

        ImGui.TableSetupColumn("##Current", ImGuiTableColumnFlags.WidthFixed, 20f);
        ImGui.TableSetupColumn("Enemy");
        ImGui.TableSetupColumn("Zone");
        ImGui.TableSetupColumn("Kills", ImGuiTableColumnFlags.WidthFixed, 60f);
        ImGui.TableHeadersRow();

        for (var i = 0; i < book.Enemies.Length; i++)
        {
            var enemy = book.Enemies[i];
            var progress = AtmaAutomationManager.GetMonsterProgress(i);
            var complete = progress >= enemy.RequiredKills;

            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            if (this.manager.IsRunning && this.manager.CurrentSlot == i)
            {
                ImGui.TextColored(ImGuiColors.DalamudYellow, ">");
            }

            ImGui.TableNextColumn();
            ImGui.Text(enemy.Name);
            ImGui.TableNextColumn();
            ImGui.Text(enemy.ZoneName);
            ImGui.TableNextColumn();
            ImGui.TextColored(
                complete ? ImGuiColors.HealerGreen : ImGuiColors.DalamudWhite,
                $"{Math.Max(progress, 0)}/{enemy.RequiredKills}");
        }

        ImGui.EndTable();
    }

    private void DrawDungeonsTab(BraveBook book)
    {
        DrawStatusLine("AutoDuty:", this.autoDutyInstalled, "Installed", "Not installed");
        ImGui.Separator();

        this.DrawDungeonsTable(book);

        ImGui.Separator();
        this.DrawDungeonControls();
    }

    private void DrawDungeonControls()
    {
        if (this.dungeonManager.IsRunning)
        {
            if (ImGui.Button("Stop"))
            {
                this.dungeonManager.Stop("stopped by user.");
            }
        }
        else
        {
            // The enemies automation drives movement itself; running both at once
            // would fight over the character, so block starting while it runs.
            var canStart = DungeonAutomationManager.CanStart(out var reason);
            if (canStart && this.manager.IsRunning)
            {
                canStart = false;
                reason = "The enemies automation is running.";
            }

            if (canStart && this.fateManager.IsRunning)
            {
                canStart = false;
                reason = "The FATEs automation is running.";
            }

            if (canStart && this.leveManager.IsRunning)
            {
                canStart = false;
                reason = "The levequests automation is running.";
            }

            ImGui.BeginDisabled(!canStart);
            if (ImGui.Button("Start"))
            {
                this.dungeonManager.Start();
            }

            ImGui.EndDisabled();
            if (!canStart && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            {
                ImGui.SetTooltip(reason);
            }
        }

        ImGui.SameLine();
        ImGui.Text($"State: {this.dungeonManager.State}");

        if (this.dungeonManager.StatusDetail.Length > 0)
        {
            ImGui.Text(this.dungeonManager.StatusDetail);
        }

        if (this.dungeonManager.LastError.Length > 0)
        {
            ImGui.TextColored(ImGuiColors.DalamudRed, this.dungeonManager.LastError);
        }
    }

    private void DrawDungeonsTable(BraveBook book)
    {
        if (!ImGui.BeginTable("##AtmaAutomationDungeons", 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
        {
            return;
        }

        ImGui.TableSetupColumn("##Current", ImGuiTableColumnFlags.WidthFixed, 20f);
        ImGui.TableSetupColumn("Dungeon");
        ImGui.TableSetupColumn("Boss");
        ImGui.TableSetupColumn("Status", ImGuiTableColumnFlags.WidthFixed, 90f);
        ImGui.TableHeadersRow();

        for (var i = 0; i < book.Dungeons.Length; i++)
        {
            var dungeon = book.Dungeons[i];
            var complete = AtmaAutomationManager.IsDungeonComplete(i);

            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            if (this.dungeonManager.IsRunning && this.dungeonManager.CurrentSlot == i)
            {
                ImGui.TextColored(ImGuiColors.DalamudYellow, ">");
            }

            ImGui.TableNextColumn();
            ImGui.Text(dungeon.Name);
            ImGui.TableNextColumn();
            ImGui.Text(dungeon.BossName);
            ImGui.TableNextColumn();
            ImGui.TextColored(
                complete ? ImGuiColors.HealerGreen : ImGuiColors.DalamudWhite,
                complete ? "Complete" : "Incomplete");
        }

        ImGui.EndTable();
    }

    private void DrawFatesTab(BraveBook book)
    {
        DrawStatusLine("vnavmesh:", this.navmeshInstalled, "Installed", "Not installed");
        DrawStatusLine($"{CombatIpc.ConfiguredName}:", this.combatAvailable, "Ready", "Not available");
        ImGui.Separator();

        this.DrawFatesTable(book);

        ImGui.Separator();
        this.DrawFateControls();
    }

    private void DrawFatesTable(BraveBook book)
    {
        if (!ImGui.BeginTable("##AtmaAutomationFates", 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
        {
            return;
        }

        ImGui.TableSetupColumn("##Current", ImGuiTableColumnFlags.WidthFixed, 20f);
        ImGui.TableSetupColumn("FATE");
        ImGui.TableSetupColumn("Zone");
        ImGui.TableSetupColumn("Status", ImGuiTableColumnFlags.WidthFixed, 90f);
        ImGui.TableHeadersRow();

        for (var i = 0; i < book.Fates.Length; i++)
        {
            var fate = book.Fates[i];
            var complete = AtmaAutomationManager.IsFateComplete(i);

            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            if (this.fateManager.IsRunning && this.fateManager.CurrentSlot == i)
            {
                ImGui.TextColored(ImGuiColors.DalamudYellow, ">");
            }

            ImGui.TableNextColumn();
            ImGui.Text(fate.Name);
            ImGui.TableNextColumn();
            ImGui.Text(fate.ZoneName);
            ImGui.TableNextColumn();
            ImGui.TextColored(
                complete ? ImGuiColors.HealerGreen : ImGuiColors.DalamudWhite,
                complete ? "Complete" : "Incomplete");
        }

        ImGui.EndTable();
    }

    private void DrawFateControls()
    {
        if (this.fateManager.IsRunning)
        {
            if (ImGui.Button("Stop"))
            {
                this.fateManager.Stop("stopped by user.");
            }
        }
        else
        {
            // All three automations drive the character; never run two at once.
            var canStart = FateAutomationManager.CanStart(this.navmeshInstalled, this.combatAvailable, out var reason);
            if (canStart && this.manager.IsRunning)
            {
                canStart = false;
                reason = "The enemies automation is running.";
            }

            if (canStart && this.dungeonManager.IsRunning)
            {
                canStart = false;
                reason = "The dungeons automation is running.";
            }

            if (canStart && this.leveManager.IsRunning)
            {
                canStart = false;
                reason = "The levequests automation is running.";
            }

            ImGui.BeginDisabled(!canStart);
            if (ImGui.Button("Start"))
            {
                this.fateManager.Start();
            }

            ImGui.EndDisabled();
            if (!canStart && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            {
                ImGui.SetTooltip(reason);
            }
        }

        ImGui.SameLine();
        ImGui.Text($"State: {this.fateManager.State}");

        if (this.fateManager.StatusDetail.Length > 0)
        {
            ImGui.Text(this.fateManager.StatusDetail);
        }

        if (this.fateManager.LastError.Length > 0)
        {
            ImGui.TextColored(ImGuiColors.DalamudRed, this.fateManager.LastError);
        }

        if (!Service.Configuration.DisableTeleport)
        {
            return;
        }

        ImGui.TextColored(
            ImGuiColors.DalamudGrey,
            "Note: the automation teleports even though \"Disable Teleport\" is enabled.");
    }

    private void DrawLevesTab(BraveBook book)
    {
        DrawStatusLine("vnavmesh:", this.navmeshInstalled, "Installed", "Not installed");
        DrawStatusLine($"{CombatIpc.ConfiguredName}:", this.combatAvailable, "Ready", "Not available");
        ImGui.Separator();

        this.DrawLevesTable(book);

        ImGui.Separator();
        ImGui.TextColored(
            ImGuiColors.DalamudGrey,
            "Accept and complete the levequests yourself; Start hands the completed ones in.");
        this.DrawLeveControls();
    }

    private void DrawLevesTable(BraveBook book)
    {
        if (!ImGui.BeginTable("##AtmaAutomationLeves", 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
        {
            return;
        }

        ImGui.TableSetupColumn("##Current", ImGuiTableColumnFlags.WidthFixed, 20f);
        ImGui.TableSetupColumn("Levequest");
        ImGui.TableSetupColumn("Issuer");
        ImGui.TableSetupColumn("Status", ImGuiTableColumnFlags.WidthFixed, 110f);
        ImGui.TableHeadersRow();

        for (var i = 0; i < book.Leves.Length; i++)
        {
            var leve = book.Leves[i];
            var status = LeveAutomationManager.GetSlotStatus(leve, i);

            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            if (this.leveManager.IsRunning && this.leveManager.CurrentSlot == i)
            {
                ImGui.TextColored(ImGuiColors.DalamudYellow, ">");
            }

            ImGui.TableNextColumn();
            ImGui.Text(leve.Name);
            ImGui.TableNextColumn();
            ImGui.Text(leve.Issuer);
            ImGui.TableNextColumn();
            var (color, text) = status switch
            {
                LeveSlotStatus.HandedIn => (ImGuiColors.HealerGreen, "Handed in"),
                LeveSlotStatus.ReadyToHandIn => (ImGuiColors.DalamudYellow, "Ready to hand in"),
                LeveSlotStatus.Failed => (ImGuiColors.DalamudRed, "Failed"),
                LeveSlotStatus.InProgress => (ImGuiColors.DalamudWhite, "In progress"),
                _ => (ImGuiColors.DalamudGrey, "Not accepted"),
            };
            ImGui.TextColored(color, text);
        }

        ImGui.EndTable();
    }

    private void DrawLeveControls()
    {
        if (this.leveManager.IsRunning)
        {
            if (ImGui.Button("Stop"))
            {
                this.leveManager.Stop("stopped by user.");
            }
        }
        else
        {
            // All automations drive the character; never run two at once.
            var canStart = LeveAutomationManager.CanStart(this.navmeshInstalled, this.combatAvailable, out var reason);
            if (canStart && this.manager.IsRunning)
            {
                canStart = false;
                reason = "The enemies automation is running.";
            }

            if (canStart && this.dungeonManager.IsRunning)
            {
                canStart = false;
                reason = "The dungeons automation is running.";
            }

            if (canStart && this.fateManager.IsRunning)
            {
                canStart = false;
                reason = "The FATEs automation is running.";
            }

            ImGui.BeginDisabled(!canStart);
            if (ImGui.Button("Start"))
            {
                this.leveManager.Start();
            }

            ImGui.EndDisabled();
            if (!canStart && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            {
                ImGui.SetTooltip(reason);
            }
        }

        ImGui.SameLine();
        ImGui.Text($"State: {this.leveManager.State}");

        if (this.leveManager.StatusDetail.Length > 0)
        {
            ImGui.Text(this.leveManager.StatusDetail);
        }

        if (this.leveManager.LastError.Length > 0)
        {
            ImGui.TextColored(ImGuiColors.DalamudRed, this.leveManager.LastError);
        }

        if (!Service.Configuration.DisableTeleport)
        {
            return;
        }

        ImGui.TextColored(
            ImGuiColors.DalamudGrey,
            "Note: the automation teleports even though \"Disable Teleport\" is enabled.");
    }

    private void DrawControls()
    {
        if (this.manager.IsRunning)
        {
            if (ImGui.Button("Stop"))
            {
                this.manager.Stop("stopped by user.");
            }
        }
        else
        {
            // Uses the dependency statuses probed on window open; the cheap
            // player/book checks stay live. Start() re-runs the full check.
            var canStart = AtmaAutomationManager.CanStart(this.navmeshInstalled, this.combatAvailable, out var reason);
            if (canStart && this.dungeonManager.IsRunning)
            {
                canStart = false;
                reason = "The dungeons automation is running.";
            }

            if (canStart && this.fateManager.IsRunning)
            {
                canStart = false;
                reason = "The FATEs automation is running.";
            }

            if (canStart && this.leveManager.IsRunning)
            {
                canStart = false;
                reason = "The levequests automation is running.";
            }

            ImGui.BeginDisabled(!canStart);
            if (ImGui.Button("Start"))
            {
                this.manager.Start();
            }

            ImGui.EndDisabled();
            if (!canStart && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            {
                ImGui.SetTooltip(reason);
            }
        }

        ImGui.SameLine();
        ImGui.Text($"State: {this.manager.State}");

        if (this.manager.StatusDetail.Length > 0)
        {
            ImGui.Text(this.manager.StatusDetail);
        }

        if (this.manager.LastError.Length > 0)
        {
            ImGui.TextColored(ImGuiColors.DalamudRed, this.manager.LastError);
        }

        if (!Service.Configuration.DisableTeleport)
        {
            return;
        }

        ImGui.TextColored(
            ImGuiColors.DalamudGrey,
            "Note: the automation teleports even though \"Disable Teleport\" is enabled.");
    }

    private void OnBookOpened(AddonEvent type, AddonArgs args)
    {
        if (Service.Configuration.AtmaAutomation.AutoOpenWindow)
        {
            this.IsOpen = true;
        }
    }

    private void OnBookClosed(AddonEvent type, AddonArgs args)
    {
        if (Service.Configuration.AtmaAutomation.AutoOpenWindow
            && !this.manager.IsRunning
            && !this.dungeonManager.IsRunning
            && !this.fateManager.IsRunning
            && !this.leveManager.IsRunning)
        {
            this.IsOpen = false;
        }
    }
}
