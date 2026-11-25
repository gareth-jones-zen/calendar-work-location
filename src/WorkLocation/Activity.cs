using System.Globalization;
using ActivityJournal;

namespace WorkLocation;

/// <summary>
/// The shared activity journal (0.8.0): one line for each entry calendar-work-location added, updated or removed, written
/// through background-tools-lib's ActivityJournal to journal_file (by convention %EA_LOCAL%\journal.jsonl), for a
/// "while you were away" view. Only what was sent: never a plan or a dry run; a change whose answer was lost is marked
/// unconfirmed. Writing it never fails the run: a problem is logged.
/// <para><c>what</c> is <c>add</c>, <c>update</c> or <c>remove</c>; <c>by</c> is auto for <c>run</c> (the task, or the same
/// rules run by hand) and hand for <c>remove</c>.</para>
/// </summary>
internal static class Activity
{
    public const string Tool = "calendar-work-location";

    private static readonly CultureInfo En = CultureInfo.InvariantCulture;

    /// <summary>A line for a change a run made to one day. Never throws.</summary>
    public static void Changed(Config config, Func<DateTime> now, System.Action<string> log, DayPlan p, string? item, bool unconfirmed = false)
    {
        var title = p.Action == Action.Delete ? p.Have!.Subject : (string)p.Want!["subject"]!;
        var subject = $"{title}, {p.Day.ToString("ddd d MMM yyyy", En)}";
        string why = p.Why, sensitivity = Sensitivity.Normal;
        if (p.Where is Where.Leave or Where.Elsewhere && p.Why != Planner.SecondEntry)
        {
            // The reason quotes another of your events (the leave entry, the conference): kept with the subject, which a
            // screen shown to others hides, and the why says only what kind of day it is.
            subject += $" ({p.Why})";
            why = p.Where == Where.Elsewhere ? "elsewhere" : p.Why.StartsWith("bank holiday", StringComparison.Ordinal) ? "bank holiday" : "leave";
            sensitivity = Sensitivity.Private;
        }
        else if (p.Where == Where.Elsewhere)
        {
            sensitivity = Sensitivity.Private;   // its title names the event
        }
        Write(config, now, log, new JournalEntry
        {
            Tool = Tool,
            What = p.Action switch { Action.Create => "add", Action.Update => "update", _ => "remove" },
            By = By.Auto,
            Subject = subject,
            Rule = p.Where.ToString().ToLowerInvariant(),
            Why = why,
            Item = item,
            Sensitivity = sensitivity,
            Outcome = unconfirmed ? JournalEntry.Unconfirmed : null,
        });
    }

    /// <summary>A line for an entry `remove` took out. Never throws.</summary>
    public static void Removed(Config config, Func<DateTime> now, System.Action<string> log, CalEvent e) =>
        Write(config, now, log, new JournalEntry
        {
            Tool = Tool,
            What = "remove",
            By = By.Hand,
            Subject = $"{e.Subject}, {e.OwnedDay!.Value.ToString("ddd d MMM yyyy", En)}",
            Rule = "by hand",
            Why = "calendar-work-location remove",
            Item = e.Id,
            // Undone by a run, which writes every day's entry again (at once: `plan` shows what it would do).
            Undo = "calendar-work-location run",
            Sensitivity = e.Subject == config.Office.Title || e.Subject == config.Remote.Title ? Sensitivity.Normal : Sensitivity.Private,
        });

    private static void Write(Config config, Func<DateTime> now, System.Action<string> log, JournalEntry entry)
    {
        if (config.JournalFile is not { } path)
            return;
        try
        {
            new Journal(path, clock: () => new DateTimeOffset(now())).TryWrite(entry, log);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            log($"journal: couldn't write to {path}: {error.GetType().Name}: {error.Message}");
        }
    }
}
