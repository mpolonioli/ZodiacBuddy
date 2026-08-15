using System;

namespace ZodiacBuddy.Stages.Atma.Automation;

/// <summary>
///     Configuration for the Trial of the Braves enemies automation.
/// </summary>
public class AtmaAutomationConfiguration
{
    /// <summary>
    ///     The shortest spawn-point wait that can be configured. Anything below
    ///     this is shorter than the trip to a filler FATE and back.
    /// </summary>
    public const int MinFillerFateWaitSeconds = 15;

    /// <summary>
    ///     The longest spawn-point wait that can be configured, longer than any
    ///     FATE rotation takes to come back around.
    /// </summary>
    public const int MaxFillerFateWaitSeconds = 1800;

    /// <summary>
    ///     Gets or sets a value indicating whether to open the automation window
    ///     when a Trial of the Braves book is opened.
    /// </summary>
    public bool AutoOpenWindow { get; set; } = true;

    /// <summary>
    ///     Gets or sets the combat plugin the automations fight with. BossMod also
    ///     dodges AoEs, which Wrath Combo does not.
    /// </summary>
    public CombatPlugin CombatPlugin { get; set; } = CombatPlugin.BossMod;

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

    /// <summary>
    ///     Gets or sets a value indicating whether completing a book automatically
    ///     takes a new one from G'jusana in Mor Dhona, paying the Allagan
    ///     tomestones of poetics she asks for, and keeps going with it.
    /// </summary>
    public bool ChainBooks { get; set; } = false;

    /// <summary>
    ///     Gets or sets how long, in seconds, the FATEs automation waits at the
    ///     target FATE's spawn point before clearing an unrelated filler FATE to
    ///     free a slot in the zone's FATE cap. Does not apply right after a
    ///     prerequisite FATE has been cleared, which uses a fixed longer wait.
    /// </summary>
    public int FillerFateWaitSeconds { get; set; } = 60;

    /// <summary>
    ///     Gets <see cref="FillerFateWaitSeconds" /> clamped to the range the UI
    ///     offers, so a hand-edited configuration file cannot make the automation
    ///     leave the spawn point immediately or never at all.
    /// </summary>
    /// <returns>The filler FATE wait, in seconds.</returns>
    public int GetFillerFateWaitSeconds()
        => Math.Clamp(this.FillerFateWaitSeconds, MinFillerFateWaitSeconds, MaxFillerFateWaitSeconds);
}
