namespace ZodiacBuddy.Stages.Atma.Automation;

/// <summary>
///     Common surface of a Trial of the Braves book automation, so the steps can
///     be chained together uniformly by the <see cref="AutomationChainManager" />.
/// </summary>
internal interface IBookAutomation
{
    /// <summary>
    ///     Gets a value indicating whether the automation is currently running.
    /// </summary>
    bool IsRunning { get; }

    /// <summary>
    ///     Gets a value indicating whether the automation finished its page
    ///     successfully (as opposed to being idle, stopped or errored).
    /// </summary>
    bool IsCompleted { get; }

    /// <summary>
    ///     Start working through the current book page of this automation.
    /// </summary>
    void Start();
}
