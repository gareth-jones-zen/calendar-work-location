using ActivityJournal;

namespace WorkLocation.Tests;

/// <summary>0.8.0: each entry a run adds, updates or removes is a line in the shared activity journal (background-tools-lib's
/// ActivityJournal); never a plan or a dry run, and writing it never fails the run. Every name here is made up.</summary>
public sealed class JournalTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "calendar-work-location-journal-" + Guid.NewGuid().ToString("N"));
    private readonly FakeCalendar calendar = new();
    private readonly DateTime now = new(2026, 11, 23, 8, 0, 0);   // a Monday

    private const string Settings = """
        time_zone = "GMT Standard Time"
        weeks_ahead = 1
        [office]
        title = "Working from the office"
        [remote]
        title = "Working from home"
        [elsewhere]
        subjects = '\b(conference|examplecon)\b'
        """;

    public JournalTests() => Directory.CreateDirectory(dir);

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

    private string JournalPath => Path.Combine(dir, "state", "journal.jsonl");

    private IReadOnlyList<JournalEntry> Lines() => File.Exists(JournalPath) ? new Journal(JournalPath).Read() : [];

    private (int Code, string Err) Run(params string[] args) => RunWith($"journal_file = '{dir}\\state\\journal.jsonl'\n", args);

    private (int Code, string Err) RunWith(string extra, params string[] args)
    {
        var path = Path.Combine(dir, "config.toml");
        File.WriteAllText(path, $"log_file = '{dir}\\wl.log'\nstate_file = '{dir}\\state.json'\n" + extra + Settings);
        var errors = new StringWriter();
        var code = Cli.Run(["--config", path, .. args], new CliHost
        {
            Out = new StringWriter(), Err = errors, Env = _ => null, Now = () => now, Calendar = _ => calendar, Lock = false,
            Notify = (_, _) => { },
        });
        return (code, errors.ToString());
    }

    [Fact]
    public void A_run_journals_what_it_added_updated_and_removed_and_a_plan_nothing()
    {
        calendar.Week("2026-11-23", "remote", "office");
        Assert.Equal(0, Run("plan").Code);
        Assert.Equal(0, Run("run", "--dry-run").Code);
        Assert.False(File.Exists(JournalPath));

        Assert.Equal(0, Run("run").Code);
        var lines = Lines();
        Assert.Equal(2, lines.Count);
        var first = lines[0];
        Assert.Equal("calendar-work-location", first.Tool);
        Assert.Equal("add", first.What);
        Assert.Equal(By.Auto, first.By);
        Assert.Equal("Working from home, Mon 23 Nov 2026", first.Subject);
        Assert.Equal("remote", first.Rule);
        Assert.Equal("work plan: remote", first.Why);
        Assert.Equal("new1", first.Item);
        Assert.Equal(Sensitivity.Normal, first.Sensitivity);
        Assert.Null(first.Undo);
        Assert.Null(first.Outcome);

        // The plan changes on Tuesday, and Monday becomes leave.
        calendar.Plan.Clear();
        calendar.Week("2026-11-23", "remote", "remote");
        calendar.Items.Add(FakeCalendar.Event("Annual leave", "2026-11-23T00:00", "2026-11-24T00:00", allDay: true, showAs: "oof"));
        Assert.Equal(0, Run("run").Code);
        lines = Lines();
        Assert.Equal(4, lines.Count);
        var removed = lines.Single(l => l.What == "remove");
        Assert.Equal("Working from home, Mon 23 Nov 2026 (leave: Annual leave)", removed.Subject);
        Assert.Equal("leave", removed.Why);
        Assert.Equal("leave", removed.Rule);
        Assert.Equal(Sensitivity.Private, removed.Sensitivity);
        Assert.Equal("new1", removed.Item);
        var updated = lines.Single(l => l.What == "update");
        Assert.Equal("Working from home, Tue 24 Nov 2026", updated.Subject);
        Assert.Equal("new2", updated.Item);

        // Nothing to change: no more lines.
        Assert.Equal(0, Run("run").Code);
        Assert.Equal(4, Lines().Count);
    }

    [Fact]
    public void An_elsewhere_day_is_private_and_its_why_only_says_the_kind_of_day()
    {
        calendar.Week("2026-11-23", "remote");
        calendar.Items.Add(FakeCalendar.Event("ExampleCon 2026", "2026-11-23T00:00", "2026-11-24T00:00", allDay: true, showAs: "free"));
        Run("run");
        var line = Assert.Single(Lines());
        Assert.Equal("add", line.What);
        Assert.StartsWith("At ExampleCon 2026, Mon 23 Nov 2026 (", line.Subject);
        Assert.Equal("elsewhere", line.Why);
        Assert.Equal(Sensitivity.Private, line.Sensitivity);
    }

    [Fact]
    public void Remove_is_by_hand_with_run_as_its_undo()
    {
        calendar.Week("2026-11-23", "office");
        Run("run");
        Run("remove", "--dry-run");
        Assert.Single(Lines());

        Assert.Equal(0, Run("remove").Code);
        var line = Lines()[1];
        Assert.Equal("remove", line.What);
        Assert.Equal(By.Hand, line.By);
        Assert.Equal("by hand", line.Rule);
        Assert.Equal("Working from the office, Mon 23 Nov 2026", line.Subject);
        Assert.Equal("calendar-work-location run", line.Undo);
        Assert.Equal(Sensitivity.Normal, line.Sensitivity);
    }

    [Fact]
    public void A_create_whose_answer_is_lost_is_journalled_unconfirmed()
    {
        calendar.Week("2026-11-23", "remote");
        calendar.CreateFail = new M365Graph.GraphDeliveryUnknownException("POST", "/me/events", new TaskCanceledException("timed out"));
        Assert.Equal(1, Run("run").Code);
        var line = Assert.Single(Lines());
        Assert.Equal("add", line.What);
        Assert.Equal(JournalEntry.Unconfirmed, line.Outcome);
        Assert.Null(line.Item);
    }

    [Fact]
    public void Without_journal_file_nothing_is_written_and_a_bad_journal_never_fails_the_run()
    {
        calendar.Week("2026-11-23", "remote");
        Assert.Equal(0, RunWith("", "run").Code);
        Assert.Empty(Directory.GetFiles(dir, "*.jsonl", SearchOption.AllDirectories));

        calendar.Items.Clear();
        Directory.CreateDirectory(JournalPath);   // a folder where the file should be
        Assert.Equal(0, Run("run").Code);
        Assert.Single(calendar.Items);
        Assert.Contains("journal: couldn't write", File.ReadAllText(Path.Combine(dir, "wl.log")));
    }

    [Fact]
    public void Journal_file_comes_from_a_base_with_its_path_variables()
    {
        File.WriteAllText(Path.Combine(dir, "base-config.toml"), $"journal_file = '%SHARED%/journal.jsonl'\n[paths]\nSHARED = '{dir}\\shared'\n");
        var path = Path.Combine(dir, "based.toml");
        File.WriteAllText(path, $"base = 'base-config.toml'\nlog_file = '{dir}\\wl.log'\nstate_file = '{dir}\\state.json'\n");
        var config = Config.Load(path, _ => null);
        Assert.Equal(Path.Combine(dir, "shared", "journal.jsonl"), Path.GetFullPath(config.JournalFile!));
    }
}
