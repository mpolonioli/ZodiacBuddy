namespace ZodiacBuddy.Stages.Atma.Automation;

/// <summary>
///     States of the Trial of the Braves FATEs automation.
/// </summary>
internal enum FateAutomationState
{
    /// <summary>
    ///     Automation is not running.
    /// </summary>
    Idle,

    /// <summary>
    ///     Picking the next incomplete FATE of the current book.
    /// </summary>
    SelectNextFate,

    /// <summary>
    ///     Teleporting to the aetheryte nearest to the current FATE.
    /// </summary>
    Teleporting,

    /// <summary>
    ///     Waiting for vnavmesh to finish building the navmesh of the current zone.
    /// </summary>
    WaitingForNavmesh,

    /// <summary>
    ///     During the initial reconnaissance sweep: checking whether the current
    ///     FATE is already up in this zone before committing to wait for it.
    /// </summary>
    ScoutingCheck,

    /// <summary>
    ///     Checking whether the current travel goal (spawn point or an active FATE)
    ///     can be reached from here, and how.
    /// </summary>
    ProbingRoute,

    /// <summary>
    ///     Traveling to the current travel goal (spawn point or an active FATE).
    /// </summary>
    Traveling,

    /// <summary>
    ///     Waiting at the spawn location for the FATE to appear.
    /// </summary>
    WaitingForFate,

    /// <summary>
    ///     Talking to the NPC that starts the FATE.
    /// </summary>
    TalkingToStartNpc,

    /// <summary>
    ///     Traveling into the active FATE's area.
    /// </summary>
    EnteringFate,

    /// <summary>
    ///     Applying level sync inside the FATE.
    /// </summary>
    SyncingLevel,

    /// <summary>
    ///     Fighting the FATE's enemies (and picking up collectables) until it ends.
    /// </summary>
    Fighting,

    /// <summary>
    ///     Handing collected items over to the FATE's collector NPC.
    /// </summary>
    HandingIn,

    /// <summary>
    ///     Waiting shortly after a FATE ended to see whether the book counted it.
    /// </summary>
    ResolvingOutcome,

    /// <summary>
    ///     Killing leftover mobs that are attacking the player.
    /// </summary>
    HandlingAggro,

    /// <summary>
    ///     The FATEs page of the book is complete.
    /// </summary>
    Completed,

    /// <summary>
    ///     Automation stopped because of an error.
    /// </summary>
    Errored,
}
