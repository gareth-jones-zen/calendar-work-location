using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using CalendarDays;

namespace WorkLocation;

/// <summary>One stretch of a day in your work plan (Graph's workLocationType: office, remote, timeOff), local time.</summary>
public sealed record Segment(DateTime Start, DateTime End, string Location);

/// <summary>A calendar event as read, local time. <see cref="OwnedDay"/> is set when calendar-work-location wrote it: the day it
/// stands for, with the hash of what it wrote.</summary>
public sealed record CalEvent(string Id, string Subject, DateTime Start, DateTime End, bool AllDay, string ShowAs,
    IReadOnlyList<string> Categories, bool Mine, int Attendees, bool Cancelled, string? Response)
{
    public DateOnly? OwnedDay { get; init; }
    public string Hash { get; init; } = "";

    /// <summary>This event as the shared day rules see it.</summary>
    public DayEvent ForRules() => new(Subject, Start, End, AllDay, ShowAs, Categories, Mine, Attendees);
}

public enum Where { None, Leave, Office, Remote, Elsewhere }

public enum Action { Keep, Create, Update, Delete, Skip }

/// <summary>A day: where you are, why, the entry it should have (null for none), the one it has, and what to do.</summary>
public sealed record DayPlan(DateOnly Day, Where Where, string Why, JsonObject? Want, CalEvent? Have, Action Action);

public static class Planner
{
    private static readonly CultureInfo En = CultureInfo.InvariantCulture;

    /// <summary>Every day from first to last: where you are, and what to change. Pure.</summary>
    public static List<DayPlan> Plan(Config config, IReadOnlyList<Segment> workPlan, IReadOnlyList<CalEvent> events,
        DateOnly first, DateOnly last)
    {
        var owned = events.Where(e => e.OwnedDay is not null).GroupBy(e => e.OwnedDay!.Value).ToDictionary(g => g.Key, g => g.ToList());
        var others = events.Where(e => e.OwnedDay is null && !e.Cancelled && e.Response != "declined").ToList();
        var titles = new[] { config.Office.Title, config.Remote.Title };
        var plans = new List<DayPlan>();
        for (var day = first; day <= last; day = day.AddDays(1))
        {
            var (where, why, subject) = Decide(config, workPlan, others, day);
            var want = where switch
            {
                Where.Office => Body(config, config.Office, day, null),
                Where.Remote => Body(config, config.Remote, day, null),
                Where.Elsewhere => Body(config, config.Elsewhere, day, subject),
                _ => null,
            };
            var have = owned.GetValueOrDefault(day) ?? [];
            // An entry of yours already saying the same (made by hand): left alone, and none added.
            if (want is not null && have.Count == 0 && others.Any(e => e.Mine && e.Attendees == 0 && e.AllDay && Covers(e, day)
                    && string.Equals(e.Subject.Trim(), (string)want["subject"]!, StringComparison.OrdinalIgnoreCase)))
            {
                plans.Add(new DayPlan(day, where, why + "; already in by hand", want, null, Action.Skip));
                continue;
            }
            // More than one owned entry for a day (a create that landed twice): keep the first, delete the rest.
            foreach (var extra in have.Skip(1))
                plans.Add(new DayPlan(day, where, "a second entry for the day", null, extra, Action.Delete));
            var current = have.FirstOrDefault();
            var action = (want, current) switch
            {
                (null, null) => Action.Keep,
                (null, _) => Action.Delete,
                (_, null) => Action.Create,
                _ => Differs(current, want) ? Action.Update : Action.Keep,
            };
            plans.Add(new DayPlan(day, where, why, want, current, action));
        }
        return plans;
    }

    /// <summary>Where you are on a day, and why: a bank holiday or leave first, then a sign in your calendar that you're elsewhere,
    /// then your work plan. The leave and elsewhere rules are background-tools-lib's CalendarDays, shared with
    /// out-of-office.</summary>
    public static (Where Where, string Why, string? Subject) Decide(Config config, IReadOnlyList<Segment> workPlan,
        IReadOnlyList<CalEvent> events, DateOnly day)
    {
        if (DayRules.Weekend(day))
            return (Where.None, "weekend", null);
        var start = day.ToDateTime(TimeOnly.MinValue);
        var end = start.AddDays(1);
        var days = events.Select(e => e.ForRules()).ToList();

        var bank = days.FirstOrDefault(e => DayRules.Covers(e, day) && config.Leave.IsBankHoliday(e));
        if (bank is not null)
            return (Where.Leave, $"bank holiday: {bank.Subject}", null);
        var leave = DayRules.Leave(config.LeaveRules, days, day);
        if (leave.DayOff)
            return (Where.Leave, $"leave: {leave.Entries[0].Subject}", null);

        var away = DayRules.Elsewhere(config.ElsewhereRules, days, day,
            e => string.Equals(e.Subject.Trim(), config.Office.Title, StringComparison.OrdinalIgnoreCase)
                 || string.Equals(e.Subject.Trim(), config.Remote.Title, StringComparison.OrdinalIgnoreCase));
        if (away is not null)
            return (Where.Elsewhere, away.Why, away.Event.Subject);

        var today = workPlan.Where(s => s.Start < end && s.End > start).ToList();
        if (today.Any(s => s.Location == "office"))
            return (Where.Office, "work plan: office", null);
        if (today.Any(s => s.Location == "remote"))
            return (Where.Remote, "work plan: remote", null);
        return (Where.None, "no work plan", null);
    }

    private static bool Covers(CalEvent e, DateOnly day) => DayRules.Covers(e.ForRules(), day);

    /// <summary>The title for an elsewhere day: the template with {subject}, without a leading "At " the subject already
    /// has ("At XConf Europe, limited availability" doesn't become "At At ...").</summary>
    public static string Title(string template, string? subject)
    {
        if (subject is null)
            return template;
        var s = subject.Trim();
        if (template.TrimStart().StartsWith("At {subject}", StringComparison.OrdinalIgnoreCase) && s.StartsWith("At ", StringComparison.OrdinalIgnoreCase))
            s = s[3..].TrimStart();
        return template.Replace("{subject}", s, StringComparison.Ordinal);
    }

    /// <summary>What the day's entry should be: all day, no reminder, no one invited.</summary>
    public static JsonObject Body(Config config, EntryStyle style, DateOnly day, string? subject) => new()
    {
        ["subject"] = Title(style.Title, subject),
        ["start"] = new JsonObject { ["dateTime"] = day.ToString("yyyy-MM-dd", En) + "T00:00:00", ["timeZone"] = config.TimeZone.Id },
        ["end"] = new JsonObject { ["dateTime"] = day.AddDays(1).ToString("yyyy-MM-dd", En) + "T00:00:00", ["timeZone"] = config.TimeZone.Id },
        ["isAllDay"] = true,
        ["showAs"] = style.ShowAs,
        ["categories"] = new JsonArray(style.Categories.Select(c => (JsonNode?)JsonValue.Create(c)).ToArray()),
        ["sensitivity"] = "normal",
        ["isReminderOn"] = false,
        ["body"] = new JsonObject { ["contentType"] = "text", ["content"] = config.Body },
    };

    /// <summary>16 hex characters of the SHA-256 of the body's JSON: what was written, to tell when it changes.</summary>
    public static string Digest(JsonObject body) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(body.ToJsonString())))[..16];

    /// <summary>Whether an entry needs rewriting: what it should say changed, or someone edited its title or made it
    /// not all day (the body says edits are overwritten).</summary>
    public static bool Differs(CalEvent have, JsonObject want) =>
        have.Hash != Digest(want) || have.Subject != (string)want["subject"]! || !have.AllDay
        || !string.Equals(have.ShowAs, (string)want["showAs"]!, StringComparison.OrdinalIgnoreCase);
}
