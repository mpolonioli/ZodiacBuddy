namespace ZodiacBuddy.Stages.Atma.Automation;

/// <summary>
///     States of the Trial of the Braves levequests automation.
/// </summary>
internal enum LeveAutomationState
{
    /// <summary>
    ///     Automation is not running.
    /// </summary>
    Idle,

    /// <summary>
    ///     Picking the next book levequest that is completed and ready to hand in.
    /// </summary>
    SelectNextLeve,

    /// <summary>
    ///     Teleporting to the aetheryte nearest to the issuing levemete.
    /// </summary>
    Teleporting,

    /// <summary>
    ///     Waiting for vnavmesh to finish building the navmesh of the current zone.
    /// </summary>
    WaitingForNavmesh,

    /// <summary>
    ///     Checking whether the levemete can be reached from here, and how.
    /// </summary>
    ProbingRoute,

    /// <summary>
    ///     Traveling to the issuing levemete.
    /// </summary>
    Traveling,

    /// <summary>
    ///     Talking to the levemete and driving the hand-in dialogs.
    /// </summary>
    HandingIn,

    /// <summary>
    ///     Killing leftover mobs that are attacking the player.
    /// </summary>
    HandlingAggro,

    /// <summary>
    ///     No more levequests can be handed in right now.
    /// </summary>
    Completed,

    /// <summary>
    ///     Automation stopped because of an error.
    /// </summary>
    Errored,
}
