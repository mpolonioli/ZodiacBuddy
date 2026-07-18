using Dalamud.Game.Command;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using ECommons;
using System;
using WrathCombo.API;
using ZodiacBuddy.BonusLight;
using ZodiacBuddy.Stages.Atma;
using ZodiacBuddy.Stages.Atma.Automation;
using ZodiacBuddy.Stages.Brave;
using ZodiacBuddy.Stages.Novus;

namespace ZodiacBuddy;

/// <summary>
///     Main plugin implementation.
/// </summary>
public sealed class ZodiacBuddyPlugin : IDalamudPlugin
{
    private const string Command = "/pzodiac";

    private readonly AdvancedUnstuck advancedUnstuck;
    private readonly AtmaManager animusBuddy;
    private readonly AtmaAutomationManager atmaAutomationManager;
    private readonly DungeonAutomationManager dungeonAutomationManager;
    private readonly FateAutomationManager fateAutomationManager;
    private readonly LeveAutomationManager leveAutomationManager;
    private readonly AutomationChainManager automationChainManager;
    private readonly AtmaAutomationWindow atmaAutomationWindow;
    private readonly BookTravelManager bookTravelManager;
    private readonly BraveManager braveManager;
    private readonly ConfigWindow configWindow;
    private readonly NovusManager novusManager;

    private readonly WindowSystem windowSystem;

    /// <summary>
    ///     Initializes a new instance of the <see cref="ZodiacBuddyPlugin" /> class.
    /// </summary>
    /// <param name="pluginInterface">Dalamud plugin interface.</param>
    public ZodiacBuddyPlugin(IDalamudPluginInterface pluginInterface)
    {
        pluginInterface.Create<Service>();

        Service.Plugin = this;
        Service.Configuration = pluginInterface.GetPluginConfig() as PluginConfiguration ?? new PluginConfiguration();

        ECommonsMain.Init(pluginInterface, this);
        WrathIPCWrapper.Init(pluginInterface, WrathIPCWrapper.ErrorType.IPCNotReady | WrathIPCWrapper.ErrorType.Unexpected);

        advancedUnstuck = new AdvancedUnstuck();
        bookTravelManager = new BookTravelManager(advancedUnstuck);
        atmaAutomationManager = new AtmaAutomationManager(advancedUnstuck, bookTravelManager);
        dungeonAutomationManager = new DungeonAutomationManager(bookTravelManager);
        fateAutomationManager = new FateAutomationManager(advancedUnstuck, bookTravelManager);
        leveAutomationManager = new LeveAutomationManager(advancedUnstuck, bookTravelManager);
        automationChainManager = new AutomationChainManager(atmaAutomationManager, dungeonAutomationManager, fateAutomationManager, leveAutomationManager);

        windowSystem = new WindowSystem("ZodiacBuddy");
        windowSystem.AddWindow(configWindow = new ConfigWindow());
        windowSystem.AddWindow(atmaAutomationWindow = new AtmaAutomationWindow(atmaAutomationManager, dungeonAutomationManager, fateAutomationManager, leveAutomationManager));

        Service.Interface.UiBuilder.OpenConfigUi += OnOpenConfigUi;
        Service.Interface.UiBuilder.Draw += windowSystem.Draw;

        Service.CommandManager.AddHandler(Command,
            new CommandInfo(OnCommand)
            {
                HelpMessage = "Open a window to edit various settings. Use \"/pzodiac auto\" for the Trial of the Braves automation.",
                ShowInHelp = true,
            });

        Service.BonusLightManager = new BonusLightManager();
        animusBuddy = new AtmaManager(atmaAutomationManager, bookTravelManager);
        novusManager = new NovusManager();
        braveManager = new BraveManager();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Service.CommandManager.RemoveHandler(Command);

        Service.Interface.UiBuilder.Draw -= windowSystem.Draw;
        Service.Interface.UiBuilder.OpenConfigUi -= OnOpenConfigUi;

        animusBuddy.Dispose();
        atmaAutomationWindow.Dispose();
        automationChainManager.Dispose();
        leveAutomationManager.Dispose();
        fateAutomationManager.Dispose();
        dungeonAutomationManager.Dispose();
        atmaAutomationManager.Dispose();
        bookTravelManager.Dispose();
        advancedUnstuck.Dispose();
        novusManager.Dispose();
        braveManager.Dispose();
        Service.BonusLightManager.Dispose();
        ECommonsMain.Dispose();
    }

    /// <summary>
    ///     Print a message.
    /// </summary>
    /// <param name="message">Message to send.</param>
    public void PrintMessage(SeString message)
    {
        var sb = new SeStringBuilder()
            .AddUiForeground("[ZodiacBuddy] ", 45)
            .Append(message);

        Service.ChatGui.Print(new XivChatEntry {Type = Service.Configuration.ChatType, Message = sb.BuiltString});
    }

    /// <summary>
    ///     Open the Trial of the Braves automation window.
    /// </summary>
    public void OpenAutomationWindow()
    {
        atmaAutomationWindow.IsOpen = true;
    }

    /// <summary>
    ///     Print an error message.
    /// </summary>
    /// <param name="message">Message to send.</param>
    public static void PrintError(string message)
    {
        Service.ChatGui.PrintError($"[ZodiacBuddy] {message}");
    }

    private void OnOpenConfigUi()
    {
        configWindow.IsOpen = true;
    }

    private void OnCommand(string command, string arguments)
    {
        if (arguments.Trim().Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            atmaAutomationWindow.IsOpen = true;
            return;
        }

        configWindow.IsOpen = true;
    }
}
