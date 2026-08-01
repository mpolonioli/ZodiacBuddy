using Dalamud.Plugin.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using ZodiacBuddy.Stages.Atma.Data;

namespace ZodiacBuddy.Stages.Atma.Automation;

/// <summary>
///     Chains the Trial of the Braves automations together: when one step finishes
///     successfully, the next book step that still has work to do and can run is
///     started automatically, wrapping around the book order (Enemies, Dungeons,
///     FATEs, Levequests) so that starting from any step completes the whole book.
///     The user picks the starting step and the rest follow.
/// </summary>
internal sealed class AutomationChainManager : IDisposable
{
    private readonly IReadOnlyList<Step> steps;
    private readonly BookExchangeManager bookExchange;
    private readonly bool[] wasRunning;

    // Completion is watched on the flag itself rather than on a running -> stopped
    // edge: a step whose page is already complete goes from Start to Completed
    // within one framework tick, and the step managers update before this one, so
    // that edge is never observed.
    private readonly bool[] wasCompleted;

    // Steps already attempted in the current chain run. Each page is chained onto
    // at most once per run, so a step that finishes with its page still incomplete
    // (Dungeons with a dungeon AutoDuty skipped, Levequests still to be completed
    // in the field) is not restarted forever as the chain wraps around. Cleared
    // once every automation is idle again, ending the run.
    private readonly bool[] attempted;

    private bool wasExchangeCompleted;

    /// <summary>
    ///     Initializes a new instance of the <see cref="AutomationChainManager" /> class.
    /// </summary>
    /// <param name="enemies">The enemies automation.</param>
    /// <param name="dungeons">The dungeons automation.</param>
    /// <param name="fates">The FATEs automation.</param>
    /// <param name="leves">The levequests automation.</param>
    /// <param name="bookExchange">The book exchange automation.</param>
    public AutomationChainManager(
        AtmaAutomationManager enemies,
        DungeonAutomationManager dungeons,
        FateAutomationManager fates,
        LeveAutomationManager leves,
        BookExchangeManager bookExchange)
    {
        this.bookExchange = bookExchange;

        // The method groups resolve to each manager's single-argument CanStart
        // overload, matching the CanStartCheck delegate signature.
        this.steps =
        [
            new Step("Enemies", enemies, IsEnemiesPageComplete, AtmaAutomationManager.CanStart),
            new Step("Dungeons", dungeons, IsDungeonsPageComplete, DungeonAutomationManager.CanStart),
            new Step("FATEs", fates, IsFatesPageComplete, FateAutomationManager.CanStart),
            new Step("Levequests", leves, IsLevesPageComplete, LeveAutomationManager.CanStart),
        ];
        this.wasRunning = new bool[this.steps.Count];
        this.wasCompleted = new bool[this.steps.Count];
        this.attempted = new bool[this.steps.Count];
        Service.Framework.Update += this.OnUpdate;
    }

    private delegate bool CanStartCheck(out string reason);

    /// <inheritdoc />
    public void Dispose()
    {
        Service.Framework.Update -= this.OnUpdate;
    }

    /// <summary>
    ///     Check whether every page of the active book is complete, so it can be
    ///     traded for a new one. A character carrying no book counts as complete.
    /// </summary>
    /// <returns>Whether the book has nothing left to do.</returns>
    public static bool IsBookComplete()
        => IsEnemiesPageComplete()
           && IsDungeonsPageComplete()
           && IsFatesPageComplete()
           && IsLevesPageComplete();

    private static bool IsEnemiesPageComplete()
    {
        if (!TryGetActiveBook(out var book))
        {
            return true;
        }

        for (var i = 0; i < book.Enemies.Length; i++)
        {
            if (AtmaAutomationManager.GetMonsterProgress(i) < book.Enemies[i].RequiredKills)
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsDungeonsPageComplete()
    {
        if (!TryGetActiveBook(out var book))
        {
            return true;
        }

        return Enumerable.Range(0, book.Dungeons.Length).All(AtmaAutomationManager.IsDungeonComplete);
    }

    private static bool IsFatesPageComplete()
    {
        if (!TryGetActiveBook(out var book))
        {
            return true;
        }

        return Enumerable.Range(0, book.Fates.Length).All(AtmaAutomationManager.IsFateComplete);
    }

    private static bool IsLevesPageComplete()
    {
        if (!TryGetActiveBook(out var book))
        {
            return true;
        }

        return Enumerable.Range(0, book.Leves.Length).All(AtmaAutomationManager.IsLeveComplete);
    }

    private static bool TryGetActiveBook(out BraveBook book)
    {
        var bookId = AtmaAutomationManager.GetActiveBookId();
        if (bookId == 0)
        {
            book = default;
            return false;
        }

        book = BraveBook.GetValue(bookId);
        return true;
    }

    private void OnUpdate(IFramework framework)
    {
        try
        {
            var chaining = Service.Configuration.AtmaAutomation.ChainAutomations;
            var anyRunning = this.CheckBookExchange(chaining);

            for (var i = 0; i < this.steps.Count; i++)
            {
                var automation = this.steps[i].Automation;
                var running = automation.IsRunning;
                var completed = automation.IsCompleted;
                anyRunning |= running;

                var startedNow = !this.wasRunning[i] && running;
                var completedNow = completed && !this.wasCompleted[i];
                this.wasRunning[i] = running;
                this.wasCompleted[i] = completed;

                // Whether started by the user or by the chain, a step counts as
                // attempted for this run so the wrap-around never revisits it. A
                // step that started and completed between two ticks is only ever
                // seen completed, so that counts as an attempt too.
                if (startedNow || completedNow)
                {
                    this.attempted[i] = true;
                }

                if (!completedNow)
                {
                    continue;
                }

                // Nothing left to chain onto in this book: when every page is
                // done, the run can continue into a new book instead of ending.
                anyRunning |= (chaining && this.AdvanceFrom(i)) || this.TryStartBookExchange();
            }

            // The run is over once nothing is left running; forget the attempts so
            // the next Start press begins a fresh pass over the whole book.
            if (!anyRunning)
            {
                Array.Clear(this.attempted);
            }
        }
        catch (Exception ex)
        {
            Service.PluginLog.Error(ex, "Exception while chaining Trial of the Braves automations.");
        }
    }

    /// <summary>
    ///     Watch the book exchange: once it has taken a new book, the chain starts
    ///     over on the first step of that book.
    /// </summary>
    /// <param name="chaining">Whether the steps are chained together.</param>
    /// <returns>Whether the exchange or the step it started is running.</returns>
    private bool CheckBookExchange(bool chaining)
    {
        var completed = this.bookExchange.IsCompleted;
        var completedNow = completed && !this.wasExchangeCompleted;
        this.wasExchangeCompleted = completed;

        if (!completedNow || !chaining)
        {
            return this.bookExchange.IsRunning;
        }

        // The new book has everything left to do, so none of its steps counts as
        // attempted yet.
        Array.Clear(this.attempted);
        Log("A new book was taken; starting over on it.");
        return this.StartNextStep(0, this.steps.Count);
    }

    /// <summary>
    ///     Travel to G'jusana for a new book, once every page of the current one is
    ///     complete and nothing is left to chain onto.
    /// </summary>
    /// <returns>Whether the book exchange was started.</returns>
    private bool TryStartBookExchange()
    {
        if (!Service.Configuration.AtmaAutomation.ChainBooks
            || this.bookExchange.IsRunning
            || this.steps.Any(s => s.Automation.IsRunning))
        {
            return false;
        }

        // A step can finish with work left in the book (levequests still to be
        // completed in the field); that book is not ready to be replaced.
        if (!IsBookComplete())
        {
            return false;
        }

        if (!BookExchangeManager.CanStart(out var reason))
        {
            Log($"Not taking a new book: {reason}");
            return false;
        }

        Log("The book is complete; taking a new one.");
        this.bookExchange.Start();
        return this.bookExchange.IsRunning;
    }

    /// <summary>
    ///     Start the next book step after a completed one, wrapping around the order
    ///     and skipping steps already attempted this run, already complete, or unable
    ///     to start.
    /// </summary>
    /// <param name="completedIndex">The step that just finished.</param>
    /// <returns>Whether a following step was started.</returns>
    private bool AdvanceFrom(int completedIndex)
    {
        Log($"{this.steps[completedIndex].Name} finished.");
        return this.StartNextStep(completedIndex + 1, this.steps.Count - 1);
    }

    /// <summary>
    ///     Start the first step of the given range that still has work to do and
    ///     can run, walking the steps in circular order so starting mid-book still
    ///     comes back around to the earlier steps.
    /// </summary>
    /// <param name="firstIndex">Step to consider first.</param>
    /// <param name="count">How many steps to consider from there.</param>
    /// <returns>Whether a step was started.</returns>
    private bool StartNextStep(int firstIndex, int count)
    {
        // Every automation drives the character; never chain onto one while any is
        // somehow still running.
        if (this.steps.Any(s => s.Automation.IsRunning))
        {
            return false;
        }

        for (var offset = 0; offset < count; offset++)
        {
            var j = (firstIndex + offset) % this.steps.Count;
            var step = this.steps[j];
            if (this.attempted[j] || step.IsPageComplete())
            {
                continue;
            }

            if (!step.CanStart(out var reason))
            {
                // Mark it attempted so the wrap does not keep retrying a step that
                // cannot run (e.g. AutoDuty missing for the dungeons).
                this.attempted[j] = true;
                Log($"Skipping {step.Name}: {reason}");
                continue;
            }

            Log($"Starting {step.Name}.");
            this.attempted[j] = true;
            step.Automation.Start();

            // Start has its own guards; if it declined, fall through to the next step.
            if (step.Automation.IsRunning)
            {
                return true;
            }

            Log($"{step.Name} did not start; trying the next step.");
        }

        return false;
    }

    private static void Log(string message)
    {
        Service.PluginLog.Information($"[AutomationChain] {message}");
    }

    private sealed record Step(
        string Name,
        IBookAutomation Automation,
        Func<bool> IsPageComplete,
        CanStartCheck CanStart);
}
