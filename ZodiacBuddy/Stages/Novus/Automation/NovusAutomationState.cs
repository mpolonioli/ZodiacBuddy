namespace ZodiacBuddy.Stages.Novus.Automation;

/// <summary>
///     States of the Novus light automation.
/// </summary>
internal enum NovusAutomationState
{
    /// <summary>
    ///     Not running.
    /// </summary>
    Idle,

    /// <summary>
    ///     Starting an unsynced AutoDuty run of the light duty.
    /// </summary>
    StartingDuty,

    /// <summary>
    ///     Waiting for the AutoDuty run to finish.
    /// </summary>
    RunningDuty,

    /// <summary>
    ///     The relic holds all the light it can.
    /// </summary>
    Completed,

    /// <summary>
    ///     Stopped due to an error.
    /// </summary>
    Errored,
}
