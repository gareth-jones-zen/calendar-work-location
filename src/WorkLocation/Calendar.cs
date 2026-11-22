using System.Globalization;
using System.Text.Json.Nodes;
using M365Graph;
using WinBackground;

namespace WorkLocation;

/// <summary>What a run reads and writes. GraphCalendar is the real one; tests give a fake.</summary>
public interface ICalendar
{
    /// <summary>Your work plan from first to last (inclusive).</summary>
    IReadOnlyList<Segment> WorkPlan(DateOnly first, DateOnly last);

    /// <summary>Every event from first to last (inclusive), with calendar-work-location's property where it wrote them.</summary>
    IReadOnlyList<CalEvent> Events(DateOnly first, DateOnly last);

    string Create(JsonObject body);
    void Update(string id, JsonObject body);
    void Delete(string id);
}

/// <summary>Your Outlook calendar and work plan, through Microsoft Graph (Calendars.ReadWrite).
///
/// <para><b>An entry is calendar-work-location's only if it carries its extended property</b> (a hidden single-value MAPI
/// property: the day it stands for), plus a second holding a hash of what was written. The calendar is the only
/// record: there's no local list, so a lost state file is harmless and an entry deleted by hand is written again at
/// the next run. Nothing without the property is ever changed or deleted.</para>
///
/// <para><b>Only your own work plan can be read</b>: asked for anyone else's, Graph answers with yours.</para></summary>
public sealed class GraphCalendar(GraphClient graph, TimeZoneInfo zone) : ICalendar
{
    public static readonly string[] Scopes = ["Calendars.ReadWrite"];
    public const string PropertySet = "{6f1d0c2e-9a7b-4d55-b3a1-0c5e8f2d4a17}";   // the tool's own; never change it (named before the tool became calendar-work-location: renaming it would orphan every entry already written)
    public const string DayProp = $"String {PropertySet} Name work-location.day";
    public const string HashProp = $"String {PropertySet} Name work-location.hash";

    private const string Select = "subject,start,end,isAllDay,showAs,categories,isOrganizer,attendees,isCancelled,responseStatus";

    private Dictionary<string, string> Headers => new() { ["Prefer"] = $"outlook.timezone=\"{zone.Id}\"" };

    private string Utc(DateOnly day) =>
        TimeZoneInfo.ConvertTimeToUtc(day.ToDateTime(TimeOnly.MinValue), zone).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

    /// <summary>The longest range, in days, asked of the work-plan view at once. Graph refuses more than 31 days
    /// ("The maximum allowed range between startDateTime and endDateTime is 31 days"); 30 keeps a margin for the hour a
    /// clock change adds to a range measured in UTC.</summary>
    public const int WorkPlanChunkDays = 30;

    public IReadOnlyList<Segment> WorkPlan(DateOnly first, DateOnly last) =>
        ReadInChunks(first, last, WorkPlanChunkDays, (from, to) =>
        {
            var url = $"/me/settings/workHoursAndLocations/occurrencesView(startDateTime='{Utc(from)}',endDateTime='{Utc(to.AddDays(1))}')";
            return graph.PagesAsync(url, Scopes, Headers).ToBlockingEnumerable().OfType<JsonObject>();
        });

    /// <summary>The work plan from first to last (inclusive), read as consecutive whole-day ranges of at most
    /// <paramref name="chunkDays"/> days, so a long period never asks Graph for more than it allows. The ranges meet at
    /// midnight without overlapping; an occurrence returned by two of them (one crossing midnight) is kept once, by its
    /// id, or by its start, end and location when it has none.</summary>
    public static IReadOnlyList<Segment> ReadInChunks(DateOnly first, DateOnly last, int chunkDays,
        Func<DateOnly, DateOnly, IEnumerable<JsonObject>> read)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(chunkDays, 1);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var segments = new List<Segment>();
        for (var from = first; from <= last; from = from.AddDays(chunkDays))
        {
            var end = from.AddDays(chunkDays - 1);
            var to = end < last ? end : last;
            foreach (var o in read(from, to))
            {
                var segment = new Segment(Time(o["start"]), Time(o["end"]), Text(o, "workLocationType") ?? "unknown");
                var key = Text(o, "id") ?? $"{segment.Start:O}|{segment.End:O}|{segment.Location}";
                if (seen.Add(key))
                    segments.Add(segment);
            }
        }
        return segments;
    }

    public IReadOnlyList<CalEvent> Events(DateOnly first, DateOnly last)
    {
        var expand = Uri.EscapeDataString($"singleValueExtendedProperties($filter=id eq '{DayProp}' or id eq '{HashProp}')");
        var url = $"/me/calendarView?startDateTime={Utc(first)}&endDateTime={Utc(last.AddDays(1))}&$select={Select}&$expand={expand}&$top=250";
        return graph.PagesAsync(url, Scopes, Headers).ToBlockingEnumerable().OfType<JsonObject>().Select(Read).ToList();
    }

    public string Create(JsonObject body)
    {
        var reply = graph.RequestAsync("/me/events", HttpMethod.Post, Stamped(body), Scopes, Headers).GetAwaiter().GetResult();
        return Text(reply, "id") ?? "";
    }

    public void Update(string id, JsonObject body) =>
        graph.RequestAsync($"/me/events/{Uri.EscapeDataString(id)}", HttpMethod.Patch, Stamped(body), Scopes, Headers).GetAwaiter().GetResult();

    public void Delete(string id) =>
        graph.RequestAsync($"/me/events/{Uri.EscapeDataString(id)}", HttpMethod.Delete, null, Scopes, Headers).GetAwaiter().GetResult();

    /// <summary>The body with calendar-work-location's properties: the day, and the hash of what's written.</summary>
    public static JsonObject Stamped(JsonObject body)
    {
        var output = (JsonObject)body.DeepClone();
        var day = ((string)body["start"]!["dateTime"]!)[..10];
        output["singleValueExtendedProperties"] = new JsonArray(
            new JsonObject { ["id"] = DayProp, ["value"] = day },
            new JsonObject { ["id"] = HashProp, ["value"] = Planner.Digest(body) });
        return output;
    }

    internal static CalEvent Read(JsonObject o)
    {
        var props = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (o["singleValueExtendedProperties"] is JsonArray list)
            foreach (var p in list.OfType<JsonObject>())
                if (Text(p, "id") is { } id)
                    props[id] = Text(p, "value") ?? "";
        DateOnly? owned = props.TryGetValue(DayProp, out var d)
                          && DateOnly.TryParseExact(d, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) ? day : null;
        return new CalEvent(Text(o, "id") ?? "", Text(o, "subject") ?? "", Time(o["start"]), Time(o["end"]),
            Flag(o, "isAllDay"), Text(o, "showAs") ?? "unknown",
            (o["categories"] as JsonArray)?.Select(c => c?.GetValue<string>() ?? "").Where(c => c.Length > 0).ToList() ?? [],
            o["isOrganizer"] is not JsonValue v || !v.TryGetValue<bool>(out var organiser) || organiser,
            (o["attendees"] as JsonArray)?.OfType<JsonObject>().Count(a => Text(a, "type") != "resource") ?? 0,
            Flag(o, "isCancelled"),
            (o["responseStatus"] as JsonObject) is { } r ? Text(r, "response") : null)
        {
            OwnedDay = owned,
            Hash = props.GetValueOrDefault(HashProp, ""),
        };
    }

    internal static DateTime Time(JsonNode? node)
    {
        var text = (node as JsonObject) is { } o ? Text(o, "dateTime") : null;
        return text is null ? default : DateTime.Parse(text[..Math.Min(19, text.Length)], CultureInfo.InvariantCulture, DateTimeStyles.None);
    }

    internal static string? Text(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static bool Flag(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue<bool>(out var b) && b;

    public static GraphClient Client(Config config) => new(new GraphClientOptions
    {
        CachePath = config.MsalCache ?? M365Graph.Graph.DefaultCachePath,
        Tenant = config.Tenant,
        LoginHint = config.LoginHint,
        Scopes = Scopes,
        SignInHint = "At a console, run: calendar-work-location sign-in",
        Log = Logger.Create(config.LogFile),
    });
}
