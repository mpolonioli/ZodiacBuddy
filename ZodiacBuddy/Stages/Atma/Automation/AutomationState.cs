namespace ZodiacBuddy.Stages.Atma.Automation;

/// <summary>
///     States of the Trial of the Braves enemies automation.
/// </summary>
internal enum AutomationState
{
    /// <summary>
    ///     Automation is not running.
    /// </summary>
    Idle,

    /// <summary>
    ///     Picking the next incomplete enemy of the current book.
    /// </summary>
    SelectNextEnemy,

    /// <summary>
    ///     Teleporting to the aetheryte nearest to the current enemy.
    /// </summary>
    Teleporting,

    /// <summary>
    ///     Waiting for vnavmesh to finish building the navmesh of the current zone.
    /// </summary>
    WaitingForNavmesh,

    /// <summary>
    ///     Traveling to the approximate location of the current enemy.
    /// </summary>
    NavigatingToArea,

    /// <summary>
    ///     Scanning the area for the current enemy.
    /// </summary>
    Scanning,

    /// <summary>
    ///     Moving into attack range of a found enemy.
    /// </summary>
    MovingToEnemy,

    /// <summary>
    ///     Fighting the current enemy until the kill is counted.
    /// </summary>
    Fighting,

    /// <summary>
    ///     Killing leftover mobs that are attacking the player.
    /// </summary>
    HandlingAggro,

    /// <summary>
    ///     The enemies page of the book is complete.
    /// </summary>
    Completed,

    /// <summary>
    ///     Automation stopped because of an error.
    /// </summary>
    Errored,
}
