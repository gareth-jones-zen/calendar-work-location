using System.Text.Json.Nodes;
using WinBackground;

namespace WorkLocation.Tests;

/// <summary>A scheduled task that records the schedule it was given.</summary>
internal sealed class RecordingTask : ITask
{
    public TaskSchedule? Schedule { get; private set; }
    public string Name => "calendar-work-location run";
    public string Enable(string exe, IReadOnlyList<string> args, string? workingDir) => Enable(exe, args, workingDir, TaskSchedule.Watcher);
    public string Enable(string exe, IReadOnlyList<string> args, string? workingDir, TaskSchedule schedule)
    {
        Schedule = schedule;
        return exe;
    }
    public bool Disable() => true;
    public bool Stop() => true;
    public IReadOnlyDictionary<string, string>? Status() => null;
}

public sealed class ChunkAndAutostartTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "calendar-work-location-chunk-" + Guid.NewGuid().ToString("N"));

    public ChunkAndAutostartTests() => Directory.CreateDirectory(dir);

    public void Dispose()
    {
        try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static JsonObject Occurrence(string? id, DateTime start, DateTime end, string type)
    {
        var o = new JsonObject
        {
            ["start"] = new JsonObject { ["dateTime"] = start.ToString("yyyy-MM-ddTHH:mm:ss.0000000") },
            ["end"] = new JsonObject { ["dateTime"] = end.ToString("yyyy-MM-ddTHH:mm:ss.0000000") },
            ["workLocationType"] = type,
        };
        if (id is not null)
            o["id"] = id;
        return o;
    }

    /// <summary>A fake work-plan view that refuses ranges over 31 days, as Graph does, and serves one office day per
    /// weekday in the range.</summary>
    private static Func<DateOnly, DateOnly, IEnumerable<JsonObject>> StrictGraph(List<(DateOnly From, DateOnly To)> calls) =>
        (from, to) =>
        {
            calls.Add((from, to));
            var span = to.AddDays(1).ToDateTime(TimeOnly.MinValue) - from.ToDateTime(TimeOnly.MinValue);
            if (span > TimeSpan.FromDays(31))
                throw new InvalidOperationException("The maximum allowed range between startDateTime and endDateTime is 31 days.");
            var items = new List<JsonObject>();
            for (var d = from; d <= to; d = d.AddDays(1))
                if (d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday))
                    items.Add(Occurrence($"occ-{d:yyyyMMdd}", d.ToDateTime(new TimeOnly(9, 0)), d.ToDateTime(new TimeOnly(17, 0)), "office"));
            return items;
        };

    [Fact]
    public void Three_months_of_work_plan_is_read_in_pieces_graph_accepts()
    {
        var calls = new List<(DateOnly From, DateOnly To)>();
        var first = new DateOnly(2026, 11, 22);
        var last = first.AddMonths(3);

        var segments = GraphCalendar.ReadInChunks(first, last, GraphCalendar.WorkPlanChunkDays, StrictGraph(calls));

        Assert.True(calls.Count >= 4);
        Assert.Equal(first, calls[0].From);
        Assert.Equal(last, calls[^1].To);
        for (var i = 1; i < calls.Count; i++)
            Assert.Equal(calls[i - 1].To.AddDays(1), calls[i].From);   // they meet, without overlapping or a gap
        var weekdays = Enumerable.Range(0, last.DayNumber - first.DayNumber + 1).Select(first.AddDays)
            .Count(d => d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday));
        Assert.Equal(weekdays, segments.Count);
        Assert.Equal(segments.Count, segments.Distinct().Count());
    }

    [Fact]
    public void A_period_within_one_piece_is_one_request()
    {
        var calls = new List<(DateOnly From, DateOnly To)>();
        var first = new DateOnly(2026, 11, 23);
        GraphCalendar.ReadInChunks(first, first.AddDays(27), GraphCalendar.WorkPlanChunkDays, StrictGraph(calls));
        Assert.Single(calls);
    }

    [Fact]
    public void Reading_all_at_once_is_what_graph_refuses()
    {
        var calls = new List<(DateOnly From, DateOnly To)>();
        var first = new DateOnly(2026, 11, 22);
        Assert.Throws<InvalidOperationException>(() => GraphCalendar.ReadInChunks(first, first.AddMonths(3), 400, StrictGraph(calls)));
    }

    [Fact]
    public void An_occurrence_returned_by_two_pieces_is_kept_once()
    {
        var crossing = new DateTime(2026, 11, 30, 22, 0, 0);
        var join = new DateOnly(2026, 11, 22).AddDays(GraphCalendar.WorkPlanChunkDays - 1);
        IEnumerable<JsonObject> Read(DateOnly from, DateOnly to) =>
        [
            Occurrence("same-id", crossing, crossing.AddHours(4), "remote"),
            Occurrence(null, new DateTime(2026, 12, 1, 9, 0, 0), new DateTime(2026, 12, 1, 17, 0, 0), "office"),
        ];

        var segments = GraphCalendar.ReadInChunks(new DateOnly(2026, 11, 22), join.AddDays(5), GraphCalendar.WorkPlanChunkDays, Read);

        Assert.Equal(2, segments.Count);   // by id, and by start, end and location when there's no id
    }

    private RecordingTask Autostart(string settings)
    {
        var path = Path.Combine(dir, "config.toml");
        File.WriteAllText(path, $"log_file = '{dir}\\wl.log'\nstate_file = '{dir}\\state.json'\n" + settings);
        var task = new RecordingTask();
        var code = Cli.Run(["--config", path, "autostart", "on"], new CliHost
        {
            Out = new StringWriter(), Err = new StringWriter(), Env = _ => null, Lock = false, ProgramDir = dir, Task = () => task,
        });
        Assert.Equal(0, code);
        return task;
    }

    [Fact]
    public void Autostart_has_no_unlock_trigger_by_default()
    {
        var schedule = Autostart("").Schedule!;
        Assert.True(schedule.AtLogon);
        Assert.Equal(TimeSpan.FromMinutes(15), schedule.Every);
        Assert.Null(schedule.AfterUnlock);
    }

    [Fact]
    public void After_unlock_minutes_adds_the_unlock_trigger()
    {
        Assert.Equal(TimeSpan.FromMinutes(2), Autostart("after_unlock_minutes = 2\n").Schedule!.AfterUnlock);
        Assert.Null(Autostart("after_unlock_minutes = 0\n").Schedule!.AfterUnlock);
    }
}
