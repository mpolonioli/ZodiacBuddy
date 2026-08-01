namespace ZodiacBuddy.Stages.Atma.Automation;

/// <summary>
///     States of the Trial of the Braves book exchange automation.
/// </summary>
internal enum BookExchangeState
{
    /// <summary>
    ///     Automation is not running.
    /// </summary>
    Idle,

    /// <summary>
    ///     Teleporting to the aetheryte nearest to the book exchange NPC.
    /// </summary>
    Teleporting,

    /// <summary>
    ///     Traveling to the book exchange NPC.
    /// </summary>
    Traveling,

    /// <summary>
    ///     Working through the exchange dialogs to take a new book.
    /// </summary>
    TalkingToNpc,

    /// <summary>
    ///     Dismissing what is left of the exchange dialogue after taking the book.
    /// </summary>
    DismissingDialogue,

    /// <summary>
    ///     A new book was taken.
    /// </summary>
    Completed,

    /// <summary>
    ///     Automation stopped because of an error.
    /// </summary>
    Errored,
}
