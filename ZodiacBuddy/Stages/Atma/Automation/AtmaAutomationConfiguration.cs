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
}
