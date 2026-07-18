namespace ZodiacBuddy.Stages.Atma.Automation;

/// <summary>
///     Configuration for the Trial of the Braves enemies automation.
/// </summary>
public class AtmaAutomationConfiguration
{
    /// <summary>
    ///     Gets or sets a value indicating whether to open the automation window
    ///     when a Trial of the Braves book is opened.
    /// </summary>
    public bool AutoOpenWindow { get; set; } = true;

    /// <summary>
    ///     Gets or sets a value indicating whether to use the mount roulette for longer travels.
    /// </summary>
    public bool UseMount { get; set; } = true;

    /// <summary>
    ///     Gets or sets a value indicating whether to travel by flying when possible.
    /// </summary>
    public bool UseFlight { get; set; } = true;

    /// <summary>
    ///     Gets or sets a value indicating whether clicking an enemy, FATE or leve
    ///     in the book also travels to it after the teleport.
    /// </summary>
    public bool TravelOnBookClick { get; set; } = false;

    /// <summary>
    ///     Gets or sets a value indicating whether clicking a dungeon in the book
    ///     starts an unsynced AutoDuty run instead of opening the duty finder.
    /// </summary>
    public bool UseAutoDutyForDungeons { get; set; } = true;

    /// <summary>
    ///     Gets or sets a value indicating whether finishing one automation step
    ///     automatically starts the next book step that still has work to do, so
    ///     the whole book can run from a single Start press.
    /// </summary>
    public bool ChainAutomations { get; set; } = true;
}
