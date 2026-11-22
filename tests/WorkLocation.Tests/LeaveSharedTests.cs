using WorkLocation;

namespace WorkLocation.Tests;

/// <summary>[leave] read through background-tools-lib's shared LeaveSettings (0.6.0): bank holidays recognised as their
/// own kind of day, the EA's older settings (bank holidays given as leave) giving the same plan, and a base file's
/// [leave] supplying the shared keys.</summary>
public sealed class LeaveSharedTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "calendar-work-location-leave-" + Guid.NewGuid().ToString("N"));
    private readonly FakeCalendar calendar = new();

    private const string Common = """
        time_zone = "GMT Standard Time"
        weeks_ahead = 1
        [office]
        title = "Working from SBP"
        [remote]
        title = "Working from home"
        """;

    // The EA's settings before the shared [leave] (bank holidays given as leave subjects and categories).
    private const string OldLeave = """

        [leave]
        subjects = '^\s*(holiday\b|annual leave|bank holiday|on leave|summer holidays?)'
        categories = ["Bank holiday"]
        min_hours = 6
        """;

    // The shared keys, as they'll be in a base file.
    private const string SharedLeave = """

        [leave]
        subjects = '^\s*(holiday\b|annual leave|on leave|summer holidays?)'
        categories = []
        min_hours = 6
        half_day = '\(half day\)'
        bank_holiday_subjects = '^\s*bank holiday\b'
        bank_holiday_categories = ["Bank holiday"]
        """;

    public LeaveSharedTests() => Directory.CreateDirectory(dir);

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

    private void Week()
    {
        calendar.Week("2026-11-23", "remote", "office", "remote", "office", "remote");
        // calendar-bank-holidays writes an all-day entry and a 09:00-17:00 block, both in the "Bank holiday" category.
        calendar.Items.Add(FakeCalendar.Event("Christmas Day", "2026-11-24T00:00", "2026-11-25T00:00", allDay: true, showAs: "free", categories: ["Bank holiday"]));
        calendar.Items.Add(FakeCalendar.Event("Bank holiday", "2026-11-24T09:00", "2026-11-24T17:00", showAs: "oof", categories: ["Bank holiday"]));
        // A bank holiday known only by its subject.
        calendar.Items.Add(FakeCalendar.Event("Bank holiday (Boxing Day)", "2026-11-25T00:00", "2026-11-26T00:00", allDay: true, showAs: "oof"));
        // A day of leave, as People First writes it (timed, 00:00-23:59).
        calendar.Items.Add(FakeCalendar.Event("Holiday (Principal Solutions Architect)", "2026-11-26T00:00", "2026-11-26T23:59", showAs: "oof"));
    }

    private (string[] Writes, string Plan, string Log) Run(string settings, string? baseFile = null)
    {
        Week();
        var path = Path.Combine(dir, "config.toml");
        var log = Path.Combine(dir, "wl.log");
        var head = $"log_file = '{log}'\nstate_file = '{Path.Combine(dir, "state.json")}'\n";
        if (baseFile is not null)
        {
            File.WriteAllText(Path.Combine(dir, "base.toml"), baseFile);
            head = "base = \"base.toml\"\n" + head;
        }
        File.WriteAllText(path, head + settings);
        var output = new StringWriter { NewLine = "\n" };
        var errors = new StringWriter { NewLine = "\n" };
        var host = new CliHost
        {
            Out = output, Err = errors, Env = _ => null, Now = () => new DateTime(2026, 11, 23, 8, 0, 0), Calendar = _ => calendar,
            Lock = false, Notify = (_, _) => { },
        };
        Assert.True(Cli.Run(["--config", path, "plan"], host) == 0, errors.ToString());
        var plan = output.ToString();
        Assert.True(Cli.Run(["--config", path, "run"], host) == 0, errors.ToString());
        return ([.. calendar.Writes], plan, File.Exists(log) ? File.ReadAllText(log) : "");
    }

    [Fact]
    public void Bank_holidays_by_category_or_subject_and_a_day_of_leave_get_no_entry()
    {
        var (writes, plan, _) = Run(Common + SharedLeave);
        Assert.Equal(["create 2026-11-23 Working from home free", "create 2026-11-27 Working from home free"], writes);
        Assert.Contains("bank holiday: Christmas Day", plan);
        Assert.Contains("bank holiday: Bank holiday (Boxing Day)", plan);
        Assert.Contains("leave: Holiday (Principal Solutions Architect)", plan);
    }

    [Fact]
    public void The_older_settings_with_bank_holidays_as_leave_give_the_same_plan_and_a_note()
    {
        var (writes, _, log) = Run(Common + OldLeave);
        Assert.Equal(["create 2026-11-23 Working from home free", "create 2026-11-27 Working from home free"], writes);
        Assert.Contains("still name bank holidays", log);
    }

    [Fact]
    public void A_base_files_leave_supplies_the_shared_keys()
    {
        var (writes, _, log) = Run(Common + "\n[leave]\n", "[leave]" + SharedLeave.Replace("\n[leave]", ""));
        Assert.Equal(["create 2026-11-23 Working from home free", "create 2026-11-27 Working from home free"], writes);
        Assert.DoesNotContain("still name bank holidays", log);
    }

    [Fact]
    public void An_unknown_leave_setting_is_an_error()
    {
        var path = Path.Combine(dir, "config.toml");
        File.WriteAllText(path, Common + "\n[leave]\nhalf_days = 'x'\n");
        var errors = new StringWriter();
        var code = Cli.Run(["--config", path, "plan"], new CliHost { Out = new StringWriter(), Err = errors, Env = _ => null, Calendar = _ => calendar, Lock = false });
        Assert.NotEqual(0, code);
        Assert.Contains("half_days", errors.ToString());
    }
}
