namespace ZodiacBuddy.Stages.Atma.Automation;

/// <summary>
///     States of the Trial of the Braves dungeons automation.
/// </summary>
internal enum DungeonAutomationState
{
    /// <summary>
    ///     Automation is not running.
    /// </summary>
    Idle,

    /// <summary>
    ///     Picking the next incomplete dungeon of the current book.
    /// </summary>
    SelectNextDungeon,

    /// <summary>
    ///     Asking AutoDuty to start an unsynced run of the current dungeon.
    /// </summary>
    StartingDuty,

    /// <summary>
    ///     Waiting for AutoDuty to finish the current dungeon.
    /// </summary>
    RunningDuty,

    /// <summary>
    ///     The dungeons page of the book is complete.
    /// </summary>
    Completed,

    /// <summary>
    ///     Automation stopped because of an error.
    /// </summary>
    Errored,
}
