using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using FFXIVClientStructs.FFXIV.Client.Game;
using System;
using ZodiacBuddy.InformationWindow;
using ZodiacBuddy.Stages.Brave.Automation;

namespace ZodiacBuddy.Stages.Brave;

/// <summary>
///     Brave information window.
/// </summary>
internal class BraveWindow : InformationWindow.InformationWindow
{
    private readonly ZetaAutomationManager automation;

    private bool canStart;
    private string cannotStartReason = string.Empty;
    private DateTime lastCanStartProbeAt = DateTime.MinValue;

    /// <summary>
    ///     Initializes a new instance of the <see cref="BraveWindow" /> class.
    /// </summary>
    /// <param name="automation">The mahatma automation driven by this window.</param>
    public BraveWindow(ZetaAutomationManager automation)
        : base("Zodiac Brave Information")
    {
        this.automation = automation;
    }

    private static InformationWindowConfiguration InfoWindowConfiguration => Service.Configuration.InformationWindow;

    /// <inheritdoc />
    protected override void DisplayRelicInfo(InventoryItem item)
    {
        if (!BraveRelic.Items.TryGetValue(item.ItemId, out var name))
        {
            return;
        }

        name = name
            .Replace("Œ", "Oe")
            .Replace("œ", "oe");
        ImGui.Text(name);

        ImGui.PushStyleColor(ImGuiCol.PlotHistogram, InfoWindowConfiguration.ProgressColor);

        var mahatmaValue = item.SpiritbondOrCollectability / 500 + 1;
        if (item.SpiritbondOrCollectability == 0)
        {
            mahatmaValue = 0;
        }

        var mahatmaProgress = mahatmaValue / 12f;
        ImGui.ProgressBar(mahatmaProgress, DetermineProgressSize(name), $"{mahatmaValue}/12");

        var value = item.SpiritbondOrCollectability % 500;
        if (value == 1)
        {
            value -= 1;
        }

        var progress = value / 80f;
        ImGui.ProgressBar(progress, DetermineProgressSize(name), $"{value / 2}/40");

        ImGui.PopStyleColor();
    }

    /// <inheritdoc />
    protected override void DisplayFooter()
    {
        if (this.automation.IsRunning)
        {
            if (ImGui.Button("Stop##ZetaAutomation"))
            {
                this.automation.Stop("stopped by user.");
            }
        }
        else
        {
            // The dependency checks go through the plugin list; probe them only
            // every few seconds instead of every frame of the overlay.
            if (DateTime.UtcNow - this.lastCanStartProbeAt > TimeSpan.FromSeconds(2))
            {
                this.lastCanStartProbeAt = DateTime.UtcNow;
                this.canStart = ZetaAutomationManager.CanStart(out this.cannotStartReason);
            }

            ImGui.BeginDisabled(!this.canStart);
            if (ImGui.Button("Start mahatma automation##ZetaAutomation"))
            {
                this.automation.Start();
            }

            ImGui.EndDisabled();
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            {
                ImGui.SetTooltip(this.canStart
                    ? "Buy the next mahatma from Remon at Swiftperch when none is\n" +
                      "attached or the current one is awakened, and charge it by\n" +
                      "running The Bowl of Embers unsynced through AutoDuty."
                    : this.cannotStartReason);
            }
        }

        if (this.automation.IsRunning)
        {
            ImGui.SameLine();
            ImGui.Text($"State: {this.automation.State}");
        }

        if (this.automation.StatusDetail.Length > 0)
        {
            ImGui.Text(this.automation.StatusDetail);
        }

        if (!this.automation.IsRunning && this.automation.LastError.Length > 0)
        {
            ImGui.TextColored(ImGuiColors.DalamudRed, this.automation.LastError);
        }
    }
}
