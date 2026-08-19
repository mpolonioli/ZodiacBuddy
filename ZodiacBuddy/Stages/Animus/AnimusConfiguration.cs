using System;

namespace ZodiacBuddy.Stages.Animus;

/// <summary>
///     Configuration class for the Animus relic.
/// </summary>
public class AnimusConfiguration
{
    /// <summary>
    ///     Alexandrite a single mysterious map yields.
    /// </summary>
    public const int AlexandritePerMap = 5;

    /// <summary>
    ///     Alexandrite Jalzahn asks for to forge the Novus.
    /// </summary>
    public const int AlexandriteGoal = 75;

    /// <summary>
    ///     Allagan tomestones of poetics a single mysterious map is bought for.
    /// </summary>
    public const int MapCost = 75;

    /// <summary>
    ///     The fewest maps a run can farm.
    /// </summary>
    public const int MinMapCount = 1;

    /// <summary>
    ///     The most maps a run can farm, well past the 15 a whole relic takes.
    /// </summary>
    public const int MaxMapCount = 99;

    /// <summary>
    ///     Gets or sets a value indicating whether to display the information about equipped relics.
    /// </summary>
    public bool DisplayRelicInfo { get; set; } = true;

    /// <summary>
    ///     Gets or sets how many mysterious maps the automation farms per run.
    ///     Fifteen maps are the 75 Alexandrite a relic takes.
    /// </summary>
    public int MapCount { get; set; } = AlexandriteGoal / AlexandritePerMap;

    /// <summary>
    ///     Gets <see cref="MapCount" /> clamped to the range the UI offers, so a
    ///     hand-edited configuration file cannot start a run that farms nothing.
    /// </summary>
    /// <returns>The number of maps to farm.</returns>
    public int GetMapCount()
        => Math.Clamp(this.MapCount, MinMapCount, MaxMapCount);
}
