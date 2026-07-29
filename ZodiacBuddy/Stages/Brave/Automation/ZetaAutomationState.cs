namespace ZodiacBuddy.Stages.Brave.Automation;

/// <summary>
///     States of the Zodiac Zeta mahatma automation.
/// </summary>
internal enum ZetaAutomationState
{
    /// <summary>
    ///     Not running.
    /// </summary>
    Idle,

    /// <summary>
    ///     Reading the relic's mahatma progress and deciding whether to buy the
    ///     next mahatma or to charge the current one.
    /// </summary>
    DecidingNextStep,

    /// <summary>
    ///     Teleporting to Swiftperch in Western La Noscea.
    /// </summary>
    Teleporting,

    /// <summary>
    ///     Waiting for the navmesh of the zone to be ready.
    /// </summary>
    WaitingForNavmesh,

    /// <summary>
    ///     Traveling to Remon's spot at Swiftperch.
    /// </summary>
    NavigatingToRemon,

    /// <summary>
    ///     Talking to Remon and buying the next mahatma.
    /// </summary>
    TalkingToRemon,

    /// <summary>
    ///     Starting an unsynced AutoDuty run of the charging duty.
    /// </summary>
    StartingDuty,

    /// <summary>
    ///     Waiting for the AutoDuty run to finish.
    /// </summary>
    RunningDuty,

    /// <summary>
    ///     All twelve mahatmas are awakened.
    /// </summary>
    Completed,

    /// <summary>
    ///     Stopped due to an error.
    /// </summary>
    Errored,
}
