namespace ZodiacBuddy.Stages.Novus;

/// <summary>
///     Configuration class for Nexus relic.
/// </summary>
public class NovusConfiguration
{
    /// <summary>
    ///     Gets or sets a value indicating whether to display the information about equipped relics.
    /// </summary>
    public bool DisplayRelicInfo { get; set; } = true;

    /// <summary>
    ///     Gets or sets a value indicating whether to show the actual numbers in the RelicGlass addon.
    /// </summary>
    public bool ShowNumbersInRelicGlass { get; set; } = true;

    /// <summary>
    ///     Gets or sets a value indicating whether to not display the first message on the RelicGlass addon.
    /// </summary>
    public bool DontPlayRelicGlassAnimation { get; set; } = true;

    /// <summary>
    ///     Gets or sets the territory of the duty the light automation runs over
    ///     and over. Defaults to Sastasha: it grants light by the dungeon tier
    ///     and is the quickest of them to clear unsynced.
    /// </summary>
    public uint AutomationTerritoryId { get; set; } = 1036;
}
