using Dalamud.Plugin.Services;
using System;
using ZodiacBuddy.Stages.Animus.Automation;
using ZodiacBuddy.Stages.Atma.Automation;

namespace ZodiacBuddy.Stages.Animus;

/// <summary>
///     Your buddy for the Animus stage: the window showing how much of the
///     Alexandrite the Novus takes is gathered, and the automation that farms it.
/// </summary>
internal class AnimusManager : IDisposable
{
    private readonly AnimusWindow window;
    private readonly AlexandriteAutomationManager alexandriteAutomation;

    /// <summary>
    ///     Initializes a new instance of the <see cref="AnimusManager" /> class.
    /// </summary>
    /// <param name="bookTravel">Shared travel helper used by the automation.</param>
    public AnimusManager(BookTravelManager bookTravel)
    {
        this.alexandriteAutomation = new AlexandriteAutomationManager(bookTravel);
        this.window = new AnimusWindow(this.alexandriteAutomation);

        Service.Framework.Update += this.OnUpdate;
        Service.Interface.UiBuilder.Draw += this.window.Draw;
    }

    /// <summary>
    ///     Gets the Alexandrite automation, so the settings window can start it
    ///     too: the maps are farmed with no relic equipped.
    /// </summary>
    public AlexandriteAutomationManager Automation => this.alexandriteAutomation;

    private static AnimusConfiguration Configuration => Service.Configuration.Animus;

    /// <inheritdoc />
    public void Dispose()
    {
        Service.Framework.Update -= this.OnUpdate;
        Service.Interface.UiBuilder.Draw -= this.window.Draw;

        this.alexandriteAutomation.Dispose();
    }

    private void OnUpdate(IFramework framework)
    {
        try
        {
            if (!Configuration.DisplayRelicInfo)
            {
                this.window.ShowWindow = false;
                return;
            }

            var mainhand = Util.GetEquippedItem(0);
            var offhand = Util.GetEquippedItem(1);

            // The window is the automation's only home, so it stays up while a run
            // is going even if the relic is put away mid-run.
            this.window.ShowWindow =
                AnimusRelic.Items.ContainsKey(mainhand.ItemId) ||
                AnimusRelic.Items.ContainsKey(offhand.ItemId) ||
                this.alexandriteAutomation.IsRunning;

            this.window.MainHandItem = mainhand;
            this.window.OffhandItem = offhand;
        }
        catch (Exception ex)
        {
            Service.PluginLog.Error(ex, $"Unhandled error during {nameof(AnimusManager)}.{nameof(this.OnUpdate)}");
        }
    }
}
