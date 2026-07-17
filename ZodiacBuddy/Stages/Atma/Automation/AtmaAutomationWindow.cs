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
///     tab (driven by this plugin) and a Dungeons tab (run through AutoDuty).
/// </summary>
internal sealed class AtmaAutomationWindow : Window, IDisposable
{
    private readonly AtmaAutomationManager manager;
    private readonly DungeonAutomationManager dungeonManager;

    private bool navmeshInstalled;
    private bool wrathAvailable;
    private bool autoDutyInstalled;

    /// <summary>
    ///     Initializes a new instance of the <see cref="AtmaAutomationWindow" /> class.
    /// </summary>
    /// <param name="manager">The enemies automation manager driven by this window.</param>
    /// <param name="dungeonManager">The dungeons automation manager driven by this window.</param>
    public AtmaAutomationWindow(AtmaAutomationManager manager, DungeonAutomationManager dungeonManager)
        : base("Trial of the Braves Automation###ZodiacBuddyAtmaAutomation")
    {
        this.manager = manager;
        this.dungeonManager = dungeonManager;

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
        this.wrathAvailable = WrathComboIpc.IsAvailable();
        this.autoDutyInstalled = AutoDutyIpc.IsInstalled;
    }

    /// <inheritdoc />
    public override void Draw()
    {
        var bookId = AtmaAutomationManager.GetActiveBookId();
        if (bookId == 0)
        {
            ImGui.TextColored(ImGuiColors.DalamudGrey, "No Trial of the Braves book is active. Equip your Zodiac weapon.");
            return;
        }

        var book = BraveBook.GetValue(bookId);
        ImGui.Text($"Book: {book.Name}");
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

        ImGui.EndTabBar();
    }

    private static void DrawStatusLine(string label, bool ok, string okText, string errorText)
    {
        ImGui.Text(label);
        ImGui.SameLine();
        ImGui.TextColored(ok ? ImGuiColors.HealerGreen : ImGuiColors.DalamudRed, ok ? okText : errorText);
    }

    private void DrawEnemiesTab(BraveBook book)
    {
        DrawStatusLine("vnavmesh:", this.navmeshInstalled, "Installed", "Not installed");
        DrawStatusLine("Wrath Combo:", this.wrathAvailable, "Ready", "Not available");
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
        ImGui.TableSetupColumn("Zone");
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
            ImGui.Text(dungeon.ZoneName);
            ImGui.TableNextColumn();
            ImGui.TextColored(
                complete ? ImGuiColors.HealerGreen : ImGuiColors.DalamudWhite,
                complete ? "Complete" : "Incomplete");
        }

        ImGui.EndTable();
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
            var canStart = AtmaAutomationManager.CanStart(this.navmeshInstalled, this.wrathAvailable, out var reason);
            if (canStart && this.dungeonManager.IsRunning)
            {
                canStart = false;
                reason = "The dungeons automation is running.";
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
        if (Service.Configuration.AtmaAutomation.AutoOpenWindow && !this.manager.IsRunning && !this.dungeonManager.IsRunning)
        {
            this.IsOpen = false;
        }
    }
}
