namespace ZodiacBuddy.Stages.Atma.Automation;

/// <summary>
///     Status of a single book levequest, combining the book's own completion
///     flag with the state of the levequest in the player's journal.
/// </summary>
internal enum LeveSlotStatus
{
    /// <summary>
    ///     The levequest is not in the journal.
    /// </summary>
    NotAccepted,

    /// <summary>
    ///     The levequest is in the journal but its objectives are not done yet.
    /// </summary>
    InProgress,

    /// <summary>
    ///     The levequest failed and has to be retried.
    /// </summary>
    Failed,

    /// <summary>
    ///     The levequest is completed and can be handed in at the levemete.
    /// </summary>
    ReadyToHandIn,

    /// <summary>
    ///     The book has counted the levequest.
    /// </summary>
    HandedIn,
}
