using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using FFXIVClientStructs.FFXIV.Client.Game;
using System;
using ZodiacBuddy.InformationWindow;
using ZodiacBuddy.Stages.Novus.Automation;

namespace ZodiacBuddy.Stages.Novus;

/// <summary>
///     Novus information window.
/// </summary>
internal class NovusWindow : InformationWindow.InformationWindow
{
    private readonly NovusAutomationManager automation;

    private bool canStart;
    private string cannotStartReason = string.Empty;
    private DateTime lastCanStartProbeAt = DateTime.MinValue;

    /// <summary>
    ///     Initializes a new instance of the <see cref="NovusWindow" /> class.
    /// </summary>
    /// <param name="automation">The light automation driven by this window.</param>
    public NovusWindow(NovusAutomationManager automation)
        : base("Novus Zodiac Information")
    {
        this.automation = automation;
    }

    private static InformationWindowConfiguration InfoWindowConfiguration => Service.Configuration.InformationWindow;

    /// <inheritdoc />
    protected override void DisplayRelicInfo(InventoryItem item)
    {
        if (!NovusRelic.Items.TryGetValue(item.ItemId, out var name))
        {
            return;
        }

        name = name
            .Replace("Œ", "Oe")
            .Replace("œ", "oe");
        ImGui.Text(name);

        ImGui.PushStyleColor(ImGuiCol.PlotHistogram, InfoWindowConfiguration.ProgressColor);

        var value = item.SpiritbondOrCollectability;
        var progress = value / 2000f;
        ImGui.ProgressBar(progress, DetermineProgressSize(name), $"{value}/2000");

        ImGui.PopStyleColor();
    }

    /// <inheritdoc />
    protected override void DisplayFooter()
    {
        if (this.automation.IsRunning)
        {
            if (ImGui.Button("Stop##NovusAutomation"))
            {
                this.automation.Stop("stopped by user.");
            }

            ImGui.SameLine();
            ImGui.Text($"State: {this.automation.State}");
        }
        else
        {
            // The dependency checks go through the plugin list; probe them only
            // every few seconds instead of every frame of the overlay.
            if (DateTime.UtcNow - this.lastCanStartProbeAt > TimeSpan.FromSeconds(2))
            {
                this.lastCanStartProbeAt = DateTime.UtcNow;
                this.canStart = NovusAutomationManager.CanStart(out this.cannotStartReason);
            }

            ImGui.BeginDisabled(!this.canStart);
            if (ImGui.Button("Start light automation##NovusAutomation"))
            {
                this.automation.Start();
            }

            ImGui.EndDisabled();
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            {
                ImGui.SetTooltip(this.canStart ? BuildStartTooltip() : this.cannotStartReason);
            }
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

    private static string BuildStartTooltip()
    {
        var duty = NovusAutomationManager.GetConfiguredDuty(out _);
        var dutyName = duty is null
            ? "the configured duty"
            : duty.DutyName.Replace("Œ", "Oe").Replace("œ", "oe");

        return $"Run {dutyName} unsynced through AutoDuty over and over\n" +
               "until the relic holds all 2000 light.\n" +
               "The duty can be changed in the Novus settings.";
    }
}
