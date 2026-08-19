using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using System;
using System.Numerics;
using ZodiacBuddy.Stages.Animus.Automation;

namespace ZodiacBuddy.Stages.Animus;

/// <summary>
///     The Alexandrite automation's controls - the gathered Alexandrite, how many
///     maps the next run farms, and the start and stop buttons - drawn wherever
///     they are asked for. The farm needs no relic equipped, so they sit both in
///     the relic overlay and in the settings window; each place keeps its own
///     instance for the values that are only probed every so often.
/// </summary>
internal sealed class AnimusAutomationControls
{
    private readonly AlexandriteAutomationManager automation;
    private readonly string id;

    private bool canStart;
    private string cannotStartReason = string.Empty;
    private DateTime lastCanStartProbeAt = DateTime.MinValue;

    private int alexandrite;
    private DateTime lastAlexandriteProbeAt = DateTime.MinValue;

    /// <summary>
    ///     Initializes a new instance of the <see cref="AnimusAutomationControls" /> class.
    /// </summary>
    /// <param name="automation">The automation the controls drive.</param>
    /// <param name="id">Widget ID, unique per place the controls are drawn in:
    ///     both can be on screen at once and would otherwise share their state.</param>
    public AnimusAutomationControls(AlexandriteAutomationManager automation, string id)
    {
        this.automation = automation;
        this.id = id;
    }

    /// <summary>
    ///     Gets the Alexandrite carried, counted at most once a second: walking the
    ///     inventory on every frame of an overlay would be wasteful.
    /// </summary>
    public int Alexandrite
    {
        get
        {
            if (DateTime.UtcNow - this.lastAlexandriteProbeAt > TimeSpan.FromSeconds(1))
            {
                this.lastAlexandriteProbeAt = DateTime.UtcNow;
                this.alexandrite = AlexandriteAutomationManager.GetAlexandriteCount();
            }

            return this.alexandrite;
        }
    }

    /// <summary>
    ///     Draw how much of the Alexandrite the Novus takes is gathered.
    /// </summary>
    /// <param name="size">Size of the progress bar.</param>
    public void DrawProgress(Vector2 size)
    {
        ImGui.PushStyleColor(ImGuiCol.PlotHistogram, Service.Configuration.InformationWindow.ProgressColor);
        ImGui.ProgressBar(
            Math.Min(this.Alexandrite / (float)AnimusConfiguration.AlexandriteGoal, 1f),
            size,
            $"{this.Alexandrite}/{AnimusConfiguration.AlexandriteGoal}");
        ImGui.PopStyleColor();
    }

    /// <summary>
    ///     Draw the run controls: the number of maps to farm and the button that
    ///     starts or stops the run, followed by what it is doing.
    /// </summary>
    public void DrawControls()
    {
        if (this.automation.IsRunning)
        {
            if (ImGui.Button($"Stop##{this.id}"))
            {
                this.automation.Stop("stopped by user.");
            }

            ImGui.SameLine();
            ImGui.Text($"State: {this.automation.State}" +
                       $" ({this.automation.MapsDone}/{this.automation.MapsTarget} maps)");
        }
        else
        {
            this.DrawMapCountSetting();

            // The dependency checks go through the plugin list; probe them only
            // every few seconds instead of every frame.
            if (DateTime.UtcNow - this.lastCanStartProbeAt > TimeSpan.FromSeconds(2))
            {
                this.lastCanStartProbeAt = DateTime.UtcNow;
                this.canStart = AlexandriteAutomationManager.CanStart(out this.cannotStartReason);
            }

            ImGui.BeginDisabled(!this.canStart);
            if (ImGui.Button($"Start Alexandrite automation##{this.id}"))
            {
                this.automation.Start();
            }

            ImGui.EndDisabled();
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            {
                ImGui.SetTooltip(this.canStart ? this.BuildStartTooltip() : this.cannotStartReason);
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

    /// <summary>
    ///     How many mysterious maps the next run farms. Fifteen of them are the 75
    ///     Alexandrite the Novus takes.
    /// </summary>
    private void DrawMapCountSetting()
    {
        var configuration = Service.Configuration.Animus;
        var maps = configuration.GetMapCount();

        ImGui.SetNextItemWidth(120f);
        if (ImGui.InputInt($"Maps to farm##{this.id}", ref maps, 1, 5))
        {
            configuration.MapCount = Math.Clamp(
                maps,
                AnimusConfiguration.MinMapCount,
                AnimusConfiguration.MaxMapCount);
            Service.Configuration.Save();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "How many mysterious maps to buy, dig up and loot before stopping.\n" +
                $"Each one is worth {AnimusConfiguration.AlexandritePerMap} Alexandrite and " +
                $"{AnimusConfiguration.MapCost} Allagan tomestones of poetics;\n" +
                $"{AnimusConfiguration.AlexandriteGoal / AnimusConfiguration.AlexandritePerMap} maps " +
                "are a whole relic's worth.\n" +
                "Maps already carried when the run starts count toward this.");
        }
    }

    private string BuildStartTooltip()
    {
        var missing = Math.Max(AnimusConfiguration.AlexandriteGoal - this.Alexandrite, 0);
        var maps = (missing + AnimusConfiguration.AlexandritePerMap - 1) / AnimusConfiguration.AlexandritePerMap;

        return "Buy mysterious maps from Auriana in Mor Dhona, decipher them,\n" +
               "dig the coffer up at the marked spot, kill what comes out of it\n" +
               "and loot it, until the configured number of maps is farmed.\n" +
               "No relic has to be equipped for this.\n" +
               $"{maps} more map(s) would finish the {AnimusConfiguration.AlexandriteGoal} Alexandrite.\n\n" +
               "Requires the vnavmesh plugin and a combat plugin.\n" +
               "Note: teleports even when \"Disable Teleport\" is enabled.";
    }
}
