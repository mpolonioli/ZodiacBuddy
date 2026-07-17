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
///     Status window of the Trial of the Braves enemies automation.
/// </summary>
internal sealed class AtmaAutomationWindow : Window, IDisposable
{
    private readonly AtmaAutomationManager manager;

    private bool navmeshInstalled;
    private bool wrathAvailable;

    /// <summary>
    ///     Initializes a new instance of the <see cref="AtmaAutomationWindow" /> class.
    /// </summary>
    /// <param name="manager">The automation manager driven by this window.</param>
    public AtmaAutomationWindow(AtmaAutomationManager manager)
        : base("Trial of the Braves Automation###ZodiacBuddyAtmaAutomation")
    {
        this.manager = manager;

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
    }

    /// <inheritdoc />
    public override void Draw()
    {
        this.DrawDependencyStatus();
        ImGui.Separator();

        var bookId = AtmaAutomationManager.GetActiveBookId();
        if (bookId == 0)
        {
            ImGui.TextColored(ImGuiColors.DalamudGrey, "No Trial of the Braves book is active. Equip your Zodiac weapon.");
        }
        else
        {
            this.DrawBook(bookId);
        }

        ImGui.Separator();
        this.DrawControls();
    }

    private static void DrawStatusLine(string label, bool ok, string okText, string errorText)
    {
        ImGui.Text(label);
        ImGui.SameLine();
        ImGui.TextColored(ok ? ImGuiColors.HealerGreen : ImGuiColors.DalamudRed, ok ? okText : errorText);
    }

    private void DrawDependencyStatus()
    {
        DrawStatusLine("vnavmesh:", this.navmeshInstalled, "Installed", "Not installed");
        DrawStatusLine("Wrath Combo:", this.wrathAvailable, "Ready", "Not available");
    }

    private void DrawBook(uint bookId)
    {
        var book = BraveBook.GetValue(bookId);
        ImGui.Text($"Book: {book.Name}");
        ImGui.Spacing();

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
        if (Service.Configuration.AtmaAutomation.AutoOpenWindow && !this.manager.IsRunning)
        {
            this.IsOpen = false;
        }
    }
}
