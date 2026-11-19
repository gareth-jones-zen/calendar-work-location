using System.Text.Json.Nodes;

namespace WorkLocation.Tests;

/// <summary>A calendar in memory: a work plan, events, and the writes made to it.</summary>
internal sealed class FakeCalendar : ICalendar
{
    public List<Segment> Plan { get; } = [];
    public List<CalEvent> Items { get; } = [];
    public List<string> Writes { get; } = [];
    private int next;

    public IReadOnlyList<Segment> WorkPlan(DateOnly first, DateOnly last) =>
        Plan.Where(s => s.End > first.ToDateTime(TimeOnly.MinValue) && s.Start < last.AddDays(1).ToDateTime(TimeOnly.MinValue)).ToList();

    public IReadOnlyList<CalEvent> Events(DateOnly first, DateOnly last) =>
        Items.Where(e => e.End > first.ToDateTime(TimeOnly.MinValue) && e.Start < last.AddDays(1).ToDateTime(TimeOnly.MinValue)).ToList();

    public string Create(JsonObject body)
    {
        var id = $"new{++next}";
        Writes.Add($"create {Day(body)} {body["subject"]} {body["showAs"]}");
        Items.Add(Owned(id, body));
        return id;
    }

    public void Update(string id, JsonObject body)
    {
        Writes.Add($"update {Day(body)} {body["subject"]}");
        Items.RemoveAll(e => e.Id == id);
        Items.Add(Owned(id, body));
    }

    public void Delete(string id)
    {
        var e = Items.Single(x => x.Id == id);
        Writes.Add($"delete {e.OwnedDay:yyyy-MM-dd} {e.Subject}");
        Items.Remove(e);
    }

    private static string Day(JsonObject body) => ((string)body["start"]!["dateTime"]!)[..10];

    public static CalEvent Owned(string id, JsonObject body)
    {
        var day = DateOnly.Parse(Day(body));
        return new CalEvent(id, (string)body["subject"]!, day.ToDateTime(TimeOnly.MinValue), day.AddDays(1).ToDateTime(TimeOnly.MinValue), true,
            (string)body["showAs"]!, body["categories"]!.AsArray().Select(c => (string)c!).ToList(), true, 0, false, "organizer")
        { OwnedDay = day, Hash = Planner.Digest(body) };
    }

    public void Day(string date, string location) =>
        Plan.Add(new Segment(DateTime.Parse(date + "T09:00"), DateTime.Parse(date + "T17:00"), location));

    public void Week(string monday, params string[] locations)
    {
        var d = DateOnly.Parse(monday);
        for (var i = 0; i < locations.Length; i++)
            Day(d.AddDays(i).ToString("yyyy-MM-dd"), locations[i]);
    }

    public static CalEvent Event(string subject, string start, string end, bool allDay = false, string showAs = "busy", bool mine = true,
        int attendees = 0, string[]? categories = null) =>
        new($"e{subject.GetHashCode():x}{start}", subject, DateTime.Parse(start), DateTime.Parse(end), allDay, showAs, categories ?? [], mine, attendees, false, "accepted");
}

public sealed class WorkLocationTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "work-location-tests-" + Guid.NewGuid().ToString("N"));
    private readonly FakeCalendar calendar = new();
    private DateTime now = new(2026, 11, 23, 8, 0, 0);   // a Monday

    private const string Settings = """
        time_zone = "GMT Standard Time"
        weeks_ahead = 1
        [office]
        title = "Working from SBP"
        categories = ["SBP"]
        [remote]
        title = "Working from home"
        [elsewhere]
        subjects = '\b(conference|xconf)\b'
        """;

    public WorkLocationTests() => Directory.CreateDirectory(dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(dir, true);
        }
        catch (IOException)
        {
        }
    }

    private (int Code, string Out, string Err) Run(string settings, params string[] args)
    {
        var path = Path.Combine(dir, "config.toml");
        File.WriteAllText(path, $"log_file = '{dir}\\wl.log'\nstate_file = '{dir}\\state.json'\n" + settings);
        var output = new StringWriter { NewLine = "\n" };
        var errors = new StringWriter { NewLine = "\n" };
        var notes = new List<string>();
        var code = Cli.Run(["--config", path, .. args], new CliHost
        {
            Out = output, Err = errors, Env = _ => null, Now = () => now, Calendar = _ => calendar, Lock = false,
            Notify = (title, _) => notes.Add(title),
        });
        return (code, output.ToString() + string.Concat(notes.Select(n => $"NOTIFIED {n}\n")), errors.ToString());
    }

    [Fact]
    public void Each_working_day_gets_an_entry_from_the_work_plan()
    {
        calendar.Week("2026-11-23", "remote", "office", "remote", "office", "remote");
        calendar.Plan.Add(new Segment(DateTime.Parse("2026-11-24T07:00"), DateTime.Parse("2026-11-24T08:00"), "timeOff"));   // travel
        var (code, output, error) = Run(Settings, "run");
        Assert.True(code == 0, error);
        Assert.Equal([
            "create 2026-11-23 Working from home free", "create 2026-11-24 Working from SBP free", "create 2026-11-25 Working from home free",
            "create 2026-11-26 Working from SBP free", "create 2026-11-27 Working from home free",
        ], calendar.Writes);
        Assert.Contains("5 added, 0 updated, 0 removed", output);
        Assert.Equal(["SBP"], calendar.Items.Single(e => e.OwnedDay == new DateOnly(2026, 11, 24)).Categories);

        calendar.Writes.Clear();
        Assert.Equal(0, Run(Settings, "run").Code);   // nothing changed: nothing written
        Assert.Empty(calendar.Writes);
    }

    [Fact]
    public void A_changed_plan_updates_the_entry_and_leave_removes_it()
    {
        calendar.Week("2026-11-23", "remote", "office", "remote", "office", "remote");
        Run(Settings, "run");
        calendar.Writes.Clear();
        calendar.Plan.RemoveAll(s => s.Start.Day == 25);
        calendar.Day("2026-11-25", "office");
        calendar.Items.Add(FakeCalendar.Event("Holiday (Principal Solutions Architect)", "2026-11-27T00:00", "2026-11-27T23:59"));
        Run(Settings, "run");
        Assert.Equal(["delete 2026-11-27 Working from home", "update 2026-11-25 Working from SBP"], calendar.Writes);
    }

    [Fact]
    public void Half_a_day_off_still_gets_an_entry_and_a_bank_holiday_does_not()
    {
        calendar.Week("2026-11-23", "remote", "office", "remote", "office", "remote");
        calendar.Items.Add(FakeCalendar.Event("Holiday (half day) (Principal Solutions Architect)", "2026-11-23T13:00", "2026-11-23T17:00"));
        calendar.Items.Add(FakeCalendar.Event("Christmas Day", "2026-11-24T00:00", "2026-11-25T00:00", allDay: true, showAs: "oof", categories: ["Holiday"]));
        calendar.Items.Add(FakeCalendar.Event("Dan Clark annual leave", "2026-11-25T00:00", "2026-11-26T00:00", allDay: true, showAs: "free"));   // someone else's
        calendar.Items.Add(FakeCalendar.Event("Kurt Holiday - All Day", "2026-11-26T00:00", "2026-11-27T00:00", allDay: true, mine: false, attendees: 3));
        Run(Settings, "run");
        Assert.Equal(["2026-11-23", "2026-11-25", "2026-11-26", "2026-11-27"],
            calendar.Writes.Select(w => w.Split(' ')[1]));
    }

    [Fact]
    public void A_conference_or_working_elsewhere_says_where()
    {
        calendar.Week("2026-11-23", "remote", "office", "remote", "office", "remote");
        calendar.Items.Add(FakeCalendar.Event("XConf Europe", "2026-11-24T00:00", "2026-11-25T00:00", allDay: true, showAs: "free"));
        calendar.Items.Add(FakeCalendar.Event("At XConf Europe, limited availability", "2026-11-24T08:00", "2026-11-24T18:00", showAs: "workingElsewhere"));
        calendar.Items.Add(FakeCalendar.Event("At the Nominet offices", "2026-11-26T09:00", "2026-11-26T17:00", showAs: "workingElsewhere"));
        Run(Settings, "run");
        Assert.Contains("create 2026-11-24 At XConf Europe free", calendar.Writes);   // the all-day event wins
        Assert.Contains("create 2026-11-26 At the Nominet offices free", calendar.Writes);   // not "At At ..."
    }

    [Fact]
    public void Entries_made_by_hand_are_left_alone_and_never_duplicated()
    {
        calendar.Week("2026-11-23", "remote", "office", "remote", "office", "remote");
        calendar.Items.Add(FakeCalendar.Event("Working from home", "2026-11-23T00:00", "2026-11-24T00:00", allDay: true, showAs: "workingElsewhere"));
        var (_, output, _) = Run(Settings, "plan");
        Assert.Contains("leave alone", output.Split('\n').First(l => l.Contains("Mon 23 Nov")));
        Assert.Contains("remote", output.Split('\n').First(l => l.Contains("Mon 23 Nov")));   // not "At Working from home"
        Run(Settings, "run");
        Assert.DoesNotContain(calendar.Writes, w => w.Contains("2026-11-23"));
        Assert.Contains(calendar.Items, e => e.Subject == "Working from home" && e.OwnedDay is null);
    }

    [Fact]
    public void An_entry_edited_by_hand_is_put_back()
    {
        calendar.Week("2026-11-23", "remote");
        Run(Settings, "run");
        var e = calendar.Items.Single(x => x.OwnedDay is not null);
        calendar.Items.Remove(e);
        calendar.Items.Add(e with { Subject = "WFH" });
        calendar.Writes.Clear();
        Run(Settings, "run");
        Assert.Equal(["update 2026-11-23 Working from home"], calendar.Writes);
    }

    [Fact]
    public void Show_as_and_titles_come_from_the_settings_and_switching_off_does_nothing()
    {
        calendar.Week("2026-11-23", "remote");
        Run(Settings.Replace("title = \"Working from home\"", "title = \"WFH\"\nshow_as = \"workingElsewhere\""), "run");
        Assert.Equal(["create 2026-11-23 WFH workingElsewhere"], calendar.Writes);
        calendar.Writes.Clear();
        calendar.Day("2026-11-24", "office");
        var (code, output, _) = Run("enabled = false\n" + Settings, "run");
        Assert.Equal(0, code);
        Assert.Contains("Switched off", output);
        Assert.Empty(calendar.Writes);
    }

    [Fact]
    public void An_empty_work_plan_or_too_many_removals_stops_the_run()
    {
        calendar.Week("2026-11-23", "remote", "office", "remote", "office", "remote");
        Run(Settings, "run");
        calendar.Plan.Clear();
        calendar.Writes.Clear();
        var (code, output, error) = Run(Settings, "run");
        Assert.Equal(1, code);
        Assert.Contains("work plan came back empty", error);
        Assert.Contains("NOTIFIED work-location couldn't run", output);
        Assert.Empty(calendar.Writes);

        calendar.Week("2026-11-23", "remote", "office", "remote", "office", "remote");
        for (var d = 23; d <= 27; d++)
            calendar.Items.Add(FakeCalendar.Event("Holiday (x)", $"2026-11-{d}T00:00", $"2026-11-{d}T23:59"));
        (code, _, error) = Run("max_deletes = 3\n" + Settings, "run");
        Assert.Equal(1, code);
        Assert.Contains("more than max_deletes (3)", error);
        Assert.Empty(calendar.Writes);
    }

    [Fact]
    public void Weeks_ahead_sets_the_period_and_remove_takes_out_only_its_own_entries()
    {
        calendar.Week("2026-11-23", "remote", "office", "remote", "office", "remote");
        calendar.Week("2026-11-30", "remote", "office", "remote", "office", "remote");
        Run(Settings.Replace("weeks_ahead = 1", "weeks_ahead = 2"), "run");
        Assert.Equal(10, calendar.Writes.Count);
        calendar.Items.Add(FakeCalendar.Event("Working from SBP", "2026-12-08T00:00", "2026-12-09T00:00", allDay: true));
        calendar.Writes.Clear();
        var (code, output, _) = Run(Settings, "remove");
        Assert.Equal(0, code);
        Assert.Contains("10 removed", output);
        Assert.Single(calendar.Items);   // the hand-made one stays
    }

    [Theory]
    [InlineData("weeks_ahead = \"4\"", "weeks_ahead should be a whole number")]
    [InlineData("colour = \"blue\"", "unknown setting 'colour'")]
    [InlineData("[office]\nshow_as = \"maybe\"", "office.show_as should be one of")]
    [InlineData("[elsewhere]\nsubjects = '('", "elsewhere.subjects isn't a valid pattern")]
    [InlineData("[remote]\ncolour = 1", "[remote] has an unknown setting 'colour'")]
    public void Settings_are_checked_strictly(string settings, string message)
    {
        var (code, _, error) = Run(settings, "plan");
        Assert.Equal(2, code);
        Assert.Contains(message, error);
    }

    [Fact]
    public void Stamped_bodies_carry_the_day_and_hash()
    {
        var config = Config.Load(WriteSettings(Settings), _ => null);
        var body = Planner.Body(config, config.Office, new DateOnly(2026, 11, 24), null);
        var stamped = GraphCalendar.Stamped(body);
        var props = stamped["singleValueExtendedProperties"]!.AsArray();
        Assert.Equal("2026-11-24", (string)props[0]!["value"]!);
        Assert.Equal(Planner.Digest(body), (string)props[1]!["value"]!);
        Assert.Equal("2026-11-25T00:00:00", (string)body["end"]!["dateTime"]!);
        Assert.False((bool)body["isReminderOn"]!);
        var read = GraphCalendar.Read(new JsonObject
        {
            ["id"] = "x", ["subject"] = "Working from SBP", ["isAllDay"] = true, ["showAs"] = "free",
            ["start"] = new JsonObject { ["dateTime"] = "2026-11-24T00:00:00.0000000" }, ["end"] = new JsonObject { ["dateTime"] = "2026-11-25T00:00:00.0000000" },
            ["attendees"] = new JsonArray(new JsonObject { ["type"] = "resource" }), ["isOrganizer"] = true,
            ["singleValueExtendedProperties"] = props.DeepClone(),
        });
        Assert.Equal(new DateOnly(2026, 11, 24), read.OwnedDay);
        Assert.Equal(0, read.Attendees);   // a room isn't a person
        Assert.False(Planner.Differs(read, body));
    }

    private string WriteSettings(string text)
    {
        var path = Path.Combine(dir, "plain.toml");
        File.WriteAllText(path, text);
        return path;
    }
}
