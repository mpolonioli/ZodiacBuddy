using Dalamud.Bindings.ImGui;
using FFXIVClientStructs.FFXIV.Client.Game;
using ZodiacBuddy.Stages.Animus.Automation;

namespace ZodiacBuddy.Stages.Animus;

/// <summary>
///     Animus information window.
/// </summary>
internal class AnimusWindow : InformationWindow.InformationWindow
{
    private readonly AnimusAutomationControls controls;

    /// <summary>
    ///     Initializes a new instance of the <see cref="AnimusWindow" /> class.
    /// </summary>
    /// <param name="automation">The Alexandrite automation driven by this window.</param>
    public AnimusWindow(AlexandriteAutomationManager automation)
        : base("Animus Zodiac Information")
    {
        this.controls = new AnimusAutomationControls(automation, "AnimusWindow");
    }

    /// <inheritdoc />
    protected override void DisplayRelicInfo(InventoryItem item)
    {
        if (!AnimusRelic.Items.TryGetValue(item.ItemId, out var name))
        {
            return;
        }

        name = name
            .Replace("Œ", "Oe")
            .Replace("œ", "oe");
        ImGui.Text(name);
    }

    /// <inheritdoc />
    protected override void DisplayFooter()
    {
        this.controls.DrawProgress(DetermineProgressSize("Alexandrite"));
        this.controls.DrawControls();
    }
}
