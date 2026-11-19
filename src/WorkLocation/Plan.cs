using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace WorkLocation;

/// <summary>One stretch of a day in your work plan (Graph's workLocationType: office, remote, timeOff), local time.</summary>
public sealed record Segment(DateTime Start, DateTime End, string Location);

/// <summary>A calendar event as read, local time. <see cref="OwnedDay"/> is set when work-location wrote it: the day it
/// stands for, with the hash of what it wrote.</summary>
public sealed record CalEvent(string Id, string Subject, DateTime Start, DateTime End, bool AllDay, string ShowAs,
    IReadOnlyList<string> Categories, bool Mine, int Attendees, bool Cancelled, string? Response)
{
    public DateOnly? OwnedDay { get; init; }
    public string Hash { get; init; } = "";
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

    /// <summary>Where you are on a day, and why: leave first, then a sign in your calendar that you're elsewhere,
    /// then your work plan.</summary>
    public static (Where Where, string Why, string? Subject) Decide(Config config, IReadOnlyList<Segment> workPlan,
        IReadOnlyList<CalEvent> events, DateOnly day)
    {
        if (day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            return (Where.None, "weekend", null);
        var start = day.ToDateTime(TimeOnly.MinValue);
        var end = start.AddDays(1);

        var leave = events.Where(e => e.Mine && e.Attendees == 0 && Covers(e, day)
                                      && (config.LeaveSubjects.IsMatch(e.Subject) || e.Categories.Any(c => config.LeaveCategories.Contains(c, StringComparer.OrdinalIgnoreCase))))
            .ToList();
        var leaveHours = Hours(leave, start, end);
        if (leaveHours >= config.LeaveMinHours - 1e-9 || leave.Any(e => e.AllDay))
            return (Where.Leave, $"leave: {leave[0].Subject}", null);

        var allDay = events.FirstOrDefault(e => e.AllDay && Covers(e, day) && config.ElsewhereSubjects?.IsMatch(e.Subject) == true);
        if (allDay is not null)
            return (Where.Elsewhere, $"all-day \"{allDay.Subject}\"", allDay.Subject);
        var shown = events.FirstOrDefault(e => Covers(e, day) && config.ElsewhereShowAs.Contains(e.ShowAs, StringComparer.OrdinalIgnoreCase)
                                               && !string.Equals(e.Subject.Trim(), config.Office.Title, StringComparison.OrdinalIgnoreCase)
                                               && !string.Equals(e.Subject.Trim(), config.Remote.Title, StringComparison.OrdinalIgnoreCase));
        if (shown is not null)
            return (Where.Elsewhere, $"\"{shown.Subject}\" shown as {shown.ShowAs}", shown.Subject);

        var today = workPlan.Where(s => s.Start < end && s.End > start).ToList();
        if (today.Any(s => s.Location == "office"))
            return (Where.Office, "work plan: office", null);
        if (today.Any(s => s.Location == "remote"))
            return (Where.Remote, "work plan: remote", null);
        return (Where.None, "no work plan", null);
    }

    private static bool Covers(CalEvent e, DateOnly day)
    {
        var start = day.ToDateTime(TimeOnly.MinValue);
        return e.Start < start.AddDays(1) && e.End > start;
    }

    private static double Hours(IEnumerable<CalEvent> events, DateTime start, DateTime end)
    {
        // Merge overlaps, so two leave entries over the same hours count once.
        double total = 0;
        DateTime? spanStart = null, spanEnd = null;
        foreach (var e in events.Select(e => (S: e.Start < start ? start : e.Start, E: e.End > end ? end : e.End)).Where(x => x.E > x.S).OrderBy(x => x.S))
        {
            if (spanEnd is null || e.S > spanEnd)
            {
                if (spanEnd is not null)
                    total += (spanEnd.Value - spanStart!.Value).TotalHours;
                (spanStart, spanEnd) = (e.S, e.E);
            }
            else if (e.E > spanEnd)
                spanEnd = e.E;
        }
        if (spanEnd is not null)
            total += (spanEnd.Value - spanStart!.Value).TotalHours;
        return total;
    }

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
