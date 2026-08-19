namespace ZodiacBuddy.Stages.Animus.Automation;

/// <summary>
///     States of the Alexandrite treasure map automation.
/// </summary>
internal enum AlexandriteAutomationState
{
    /// <summary>
    ///     Automation is not running.
    /// </summary>
    Idle,

    /// <summary>
    ///     Working out what the run needs next: a map, a dig or a trip to the vendor.
    /// </summary>
    DecidingNextStep,

    /// <summary>
    ///     Teleporting to Mor Dhona, where the maps are sold.
    /// </summary>
    TeleportingToVendor,

    /// <summary>
    ///     Traveling to the map vendor.
    /// </summary>
    TravelingToVendor,

    /// <summary>
    ///     Working through the vendor's dialogs to buy a mysterious map.
    /// </summary>
    BuyingMap,

    /// <summary>
    ///     Dismissing what is left of the vendor's dialogue after a purchase.
    /// </summary>
    DismissingDialogue,

    /// <summary>
    ///     Deciphering the mysterious map carried in the inventory.
    /// </summary>
    Deciphering,

    /// <summary>
    ///     Teleporting to the aetheryte nearest to the dig spot.
    /// </summary>
    TeleportingToSpot,

    /// <summary>
    ///     Traveling to the dig spot the open map points at.
    /// </summary>
    TravelingToSpot,

    /// <summary>
    ///     Digging up the treasure coffer at the marked spot.
    /// </summary>
    Digging,

    /// <summary>
    ///     Opening the treasure coffer, before and after the enemies it spawns.
    /// </summary>
    OpeningCoffer,

    /// <summary>
    ///     Killing whatever is attacking, then going back to what was interrupted.
    /// </summary>
    Fighting,

    /// <summary>
    ///     Every map of the run is farmed.
    /// </summary>
    Completed,

    /// <summary>
    ///     Automation stopped because of an error.
    /// </summary>
    Errored,
}
