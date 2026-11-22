using System.Globalization;
using System.Reflection;
using System.Text.Json.Nodes;
using M365Graph;
using WinBackground;

namespace WorkLocation;

/// <summary>Everything the command line touches outside itself, so tests can give it fakes.</summary>
public sealed class CliHost
{
    public TextWriter Out { get; init; } = Console.Out;
    public TextWriter Err { get; init; } = Console.Error;
    public Func<string, string?> Env { get; init; } = Config.Environment;
    public Func<DateTime> Now { get; init; } = () => DateTime.Now;
    public Func<Config, ICalendar> Calendar { get; init; } = config => new GraphCalendar(GraphCalendar.Client(config), config.TimeZone);
    public Action<Config> SignIn { get; init; } = config =>
        GraphCalendar.Client(config).TokenAsync(GraphCalendar.Scopes, true).GetAwaiter().GetResult();
    public Action<string, IEnumerable<string>> Notify { get; init; } = Toast.Notify;
    public Func<ITask> Task { get; init; } = () => new ScheduledTask(Cli.TaskName,
        "Puts an all-day entry in your calendar for where you're working each day (calendar-work-location run).");
    public string ProgramDir { get; init; } = AppContext.BaseDirectory;
    public bool Lock { get; init; } = true;
}

/// <summary>calendar-work-location: an all-day entry in your calendar for each working day, saying where you're working, from
/// your work plan and calendar. Exit codes: 0 done; 1 failed; 2 settings, arguments or sign-in.</summary>
public static class Cli
{
    public const string Prog = "calendar-work-location";
    public const string TaskName = "calendar-work-location run";
    public const string BackgroundExe = "calendar-work-location-background.exe";

    private const string Usage = "usage: calendar-work-location [-h] [--version] [--config CONFIG] {plan,run,remove,autostart,sign-in,config} ...";

    // Explicit "\n" throughout (a raw literal would take the .cs file's line endings).
    public const string Help =
        Usage + "\n\n" +
        "calendar-work-location: an all-day entry in your Outlook calendar for each working day, saying where you're working\n" +
        "(\"Working from home\", \"Working from the office\", \"At <conference>\"), from your work plan and your calendar.\n" +
        "It changes only the entries it wrote.\n\n" +
        "    calendar-work-location plan               each day: where you are, why, and what a run would change\n" +
        "    calendar-work-location run                make the calendar match (--dry-run: say what, change nothing)\n" +
        "    calendar-work-location remove             take out its entries from today on (--dry-run)\n" +
        "    calendar-work-location autostart on       run every few minutes, at logon and after unlocking (off, restart, status)\n" +
        "    calendar-work-location sign-in            sign in to Microsoft (once; opens a browser)\n" +
        "    calendar-work-location config             create the settings file (if missing) and say where it is\n\n" +
        "options:\n" +
        "  -h, --help       show this help message and exit\n" +
        "  --version        show program's version number and exit\n" +
        "  --config CONFIG  settings file (default %APPDATA%\\calendar-work-location\\config.toml)\n";

    private static readonly string[] Commands = ["plan", "run", "remove", "autostart", "sign-in", "config"];
    private static readonly CultureInfo En = CultureInfo.InvariantCulture;

    public static string Version =>
        typeof(Cli).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

    internal sealed class Options
    {
        public string? Config;
        public string Command = "";
        public List<string> Args = [];
        public bool DryRun;
    }

    private sealed class ArgumentsException(string message) : Exception(message);

    /// <summary>A run that stopped itself rather than write something that looks wrong.</summary>
    public sealed class SafetyStop(string message) : Exception(message);

    public static int Run(IReadOnlyList<string> argv, CliHost? host = null)
    {
        host ??= new CliHost();
        Options options;
        try
        {
            var parsed = Parse(argv, host);
            if (parsed is null)
                return 0;
            options = parsed;
        }
        catch (ArgumentsException error)
        {
            host.Err.WriteLine(Usage);
            host.Err.WriteLine($"{Prog}: error: {error.Message}");
            return 2;
        }

        Config? config = null;
        try
        {
            if (options.Command == "config")
            {
                host.Out.WriteLine(WorkLocation.Config.CreateDefault(options.Config ?? host.Env(WorkLocation.Config.EnvConfig), host.Env));
                return 0;
            }
            config = WorkLocation.Config.Load(options.Config, host.Env);
            switch (options.Command)
            {
                case "sign-in":
                    host.SignIn(config);
                    host.Out.WriteLine("Signed in.");
                    return 0;
                case "autostart":
                    return Autostart(config, options.Args[0], host);
            }
            using var held = host.Lock ? Locks.Exclusive("calendar-work-location-" + Locks.Key(config.Path), 300) : null;
            var code = options.Command switch
            {
                "plan" => Apply(config, host, dryRun: true, show: true),
                "run" => Apply(config, host, options.DryRun, show: options.DryRun),
                _ => Remove(config, host, options.DryRun),
            };
            if (options.Command == "run" && !options.DryRun)
                ForgetFailure(config);
            return code;
        }
        catch (Exception error) when (error is UsageException or SignInNeededException)
        {
            host.Err.WriteLine(error.Message);
            if (options.Command == "run" && config is not null && !options.DryRun)
                Failed(config, host, error);
            return 2;
        }
        catch (Exception error)
        {
            host.Err.WriteLine(error is SafetyStop ? error.Message : $"{error.GetType().Name}: {error.Message}");
            if (options.Command == "run" && config is not null && !options.DryRun)
                Failed(config, host, error);
            return 1;
        }
    }

    private static (DateOnly First, DateOnly Last) Period(Config config, CliHost host)
    {
        var today = DateOnly.FromDateTime(host.Now());
        return (today, today.AddDays(Math.Max(1, config.WeeksAhead * 7) - 1));
    }

    private static int Apply(Config config, CliHost host, bool dryRun, bool show)
    {
        var log = dryRun ? (_ => { }) : Logger.Create(config.LogFile, null, clock: host.Now);
        if (!config.Enabled)
        {
            host.Out.WriteLine("Switched off (enabled = false in the settings): nothing done. `calendar-work-location remove` takes its entries out.");
            return 0;
        }
        var (first, last) = Period(config, host);
        var calendar = host.Calendar(config);
        var workPlan = calendar.WorkPlan(first, last);
        var events = calendar.Events(first, last);
        var plans = Planner.Plan(config, workPlan, events, first, last);

        var weekdays = plans.Count(p => p.Day.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday));
        if (workPlan.Count == 0 && weekdays > 0 && plans.Any(p => p.Action == Action.Delete))
            throw new SafetyStop($"Your work plan came back empty for {first:dd/MM} to {last:dd/MM}, so nothing was changed "
                                 + "(it would have removed entries). Set one in Outlook, or turn calendar-work-location off.");
        var deletes = plans.Count(p => p.Action == Action.Delete);
        if (deletes > config.MaxDeletes)
            throw new SafetyStop($"A run would remove {deletes} entries, more than max_deletes ({config.MaxDeletes}), so nothing was "
                                 + "changed. Check `calendar-work-location plan`; raise max_deletes if it's right.");

        if (show)
            foreach (var p in plans)
                host.Out.WriteLine(Line(p, dryRun));

        var counts = new Dictionary<Action, int>();
        foreach (var p in plans.OrderBy(p => p.Action == Action.Delete ? 0 : p.Action == Action.Update ? 1 : 2))
        {
            if (p.Action is Action.Keep or Action.Skip)
                continue;
            counts[p.Action] = counts.GetValueOrDefault(p.Action) + 1;
            if (dryRun)
                continue;
            switch (p.Action)
            {
                case Action.Delete:
                    calendar.Delete(p.Have!.Id);
                    log($"removed {p.Day:yyyy-MM-dd} \"{p.Have.Subject}\" ({p.Why})");
                    break;
                case Action.Update:
                    calendar.Update(p.Have!.Id, p.Want!);
                    log($"updated {p.Day:yyyy-MM-dd} \"{p.Want!["subject"]}\" ({p.Why})");
                    break;
                case Action.Create:
                    calendar.Create(p.Want!);
                    log($"added {p.Day:yyyy-MM-dd} \"{p.Want!["subject"]}\" ({p.Why})");
                    break;
            }
        }
        var summary = $"{first:dd/MM} to {last:dd/MM}: {counts.GetValueOrDefault(Action.Create)} to add, "
                      + $"{counts.GetValueOrDefault(Action.Update)} to update, {counts.GetValueOrDefault(Action.Delete)} to remove, "
                      + $"{plans.Count(p => p.Action == Action.Keep && p.Have is not null)} unchanged";
        if (dryRun)
            host.Out.WriteLine(summary + " (nothing changed)");
        else
        {
            host.Out.WriteLine(summary.Replace(" to add", " added").Replace(" to update", " updated").Replace(" to remove", " removed"));
            log(summary.Replace(" to add", " added").Replace(" to update", " updated").Replace(" to remove", " removed"));
        }
        return 0;
    }

    internal static string Line(DayPlan p, bool dryRun)
    {
        var what = p.Want is { } w ? (string)w["subject"]! : "-";
        var doing = p.Action switch
        {
            Action.Create => dryRun ? "add" : "added",
            Action.Update => dryRun ? $"update (now \"{p.Have!.Subject}\")" : "updated",
            Action.Delete => dryRun ? $"remove \"{p.Have!.Subject}\"" : "removed",
            Action.Skip => "leave alone",
            _ => p.Have is null ? "" : "unchanged",
        };
        return $"  {p.Day.ToString("ddd dd MMM", En)}  {p.Where.ToString().ToLowerInvariant(),-9}  {Clip(what, 34),-34}  {doing,-12}  ({p.Why})".TrimEnd();
    }

    private static string Clip(string text, int width) => text.Length <= width ? text : text[..(width - 1)] + "…";

    private static int Remove(Config config, CliHost host, bool dryRun)
    {
        var log = dryRun ? (_ => { }) : Logger.Create(config.LogFile, null, clock: host.Now);
        var first = DateOnly.FromDateTime(host.Now());
        var calendar = host.Calendar(config);
        var owned = calendar.Events(first, first.AddDays(7 * 53)).Where(e => e.OwnedDay is not null).OrderBy(e => e.Start).ToList();
        foreach (var e in owned)
        {
            host.Out.WriteLine($"  {e.OwnedDay!.Value.ToString("ddd dd MMM", En)}  {e.Subject}");
            if (!dryRun)
            {
                calendar.Delete(e.Id);
                log($"removed {e.OwnedDay:yyyy-MM-dd} \"{e.Subject}\" (remove)");
            }
        }
        host.Out.WriteLine(owned.Count == 0 ? "None of its entries from today on." : $"{owned.Count} {(dryRun ? "to remove (nothing changed)" : "removed")}.");
        return 0;
    }

    /// <summary>A run that failed: logged, and notified once until a run succeeds again.</summary>
    private static void Failed(Config config, CliHost host, Exception error)
    {
        try
        {
            Logger.Create(config.LogFile, null, clock: host.Now)($"run failed: {error.GetType().Name}: {error.Message}");
            // Notified on the third failure in a row (0.1.1), as the other tools do: a run on waking can find the network
            // not up yet (19 Nov 2026, 21:54: "Couldn't find login.microsoftonline.com"), and the next run is fine. A sign-in
            // or a safety stop won't clear by itself, so those are notified at once. Once only, until a run succeeds.
            var state = LoadState(config.StateFile);
            var failures = (state["failures"]?.GetValue<int>() ?? 0) + 1;
            state["failures"] = failures;
            if (state["notified"] is null && (failures >= 3 || error is SignInNeededException or SafetyStop))
            {
                if (config.Notify)
                    host.Notify("calendar-work-location couldn't run", [error.Message, $"Log: {config.LogFile}"]);
                state["notified"] = host.Now().ToString("yyyy-MM-ddTHH:mm:ss", En);
            }
            SaveState(config.StateFile, state);
        }
        catch (Exception)
        {
            // nothing more can be done: the exit code says it failed
        }
    }

    private static void ForgetFailure(Config config)
    {
        var state = LoadState(config.StateFile);
        if (state.Remove("failures") | state.Remove("notified") | state.Remove("failing"))
            SaveState(config.StateFile, state);
    }

    internal static JsonObject LoadState(string path)
    {
        try
        {
            return File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? [] : [];
        }
        catch (Exception error) when (error is IOException or System.Text.Json.JsonException)
        {
            return [];
        }
    }

    internal static void SaveState(string path, JsonObject state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, state.ToJsonString());
        File.Move(temp, path, true);
    }

    private static int Autostart(Config config, string action, CliHost host)
    {
        var task = host.Task();
        switch (action)
        {
            case "on":
                var background = Path.Combine(host.ProgramDir, BackgroundExe);
                var exe = File.Exists(background) ? background : Path.Combine(host.ProgramDir, "calendar-work-location.exe");
                var schedule = new TaskSchedule
                {
                    AtLogon = true,
                    Every = TimeSpan.FromMinutes(config.EveryMinutes),
                    AfterUnlock = config.AfterUnlockMinutes is { } u ? TimeSpan.FromMinutes(u) : null,
                    KeepRunning = false,
                    TimeLimit = TimeSpan.FromMinutes(5),
                };
                var line = task.Enable(exe, ["--config", config.Path, "run"], Path.GetDirectoryName(config.Path), schedule);
                host.Out.WriteLine($"Autostart is on: '{TaskName}' runs every {config.EveryMinutes} minutes, at logon" +
                                   (config.AfterUnlockMinutes is { } m ? $" and {m} minute(s) after each unlock" : "") +
                                   $", and has run now.\n  {line}");
                return 0;
            case "off":
                host.Out.WriteLine(task.Disable() ? "Autostart is off." : "Autostart was already off.");
                return 0;
            case "restart":
                try
                {
                    host.Out.WriteLine(task.Restart() ? $"Started '{TaskName}' now." : "Autostart is off, so there's nothing to restart.");
                }
                catch (InvalidOperationException error)
                {
                    host.Err.WriteLine(error.Message);
                    return 1;
                }
                return 0;
            default:
                var info = task.Status();
                if (info is null)
                    host.Out.WriteLine("Autostart is off. `calendar-work-location autostart on` turns it on.");
                else
                {
                    host.Out.WriteLine($"Autostart is on ('{TaskName}').");
                    foreach (var name in new[] { "state", "last_run", "last_result", "next_run", "command" })
                        host.Out.WriteLine($"  {name.Replace('_', ' '),-12} {info.GetValueOrDefault(name, "")}");
                }
                return 0;
        }
    }

    private static Options? Parse(IReadOnlyList<string> argv, CliHost host)
    {
        var options = new Options();
        var i = 0;
        for (; i < argv.Count && argv[i].StartsWith('-'); i++)
        {
            var arg = argv[i];
            if (arg is "-h" or "--help")
            {
                host.Out.Write(Help);
                return null;
            }
            if (arg == "--version")
            {
                host.Out.WriteLine(Version);
                return null;
            }
            if (arg == "--config")
            {
                if (++i >= argv.Count)
                    throw new ArgumentsException("argument --config: expected one argument");
                options.Config = argv[i];
            }
            else if (arg.StartsWith("--config=", StringComparison.Ordinal))
                options.Config = arg["--config=".Length..];
            else
                throw new ArgumentsException($"unrecognized arguments: {arg}");
        }
        if (i >= argv.Count)
            throw new ArgumentsException("the following arguments are required: command");
        options.Command = argv[i++];
        if (!Commands.Contains(options.Command))
            throw new ArgumentsException($"argument command: invalid choice: '{options.Command}' (choose from {string.Join(", ", Commands.Select(c => $"'{c}'"))})");
        for (; i < argv.Count; i++)
        {
            var arg = argv[i];
            switch (options.Command, arg)
            {
                case (_, "-h" or "--help"):
                    host.Out.Write(Help);
                    return null;
                case ("run" or "remove", "--dry-run"):
                    options.DryRun = true;
                    break;
                default:
                    if (arg.StartsWith('-') && arg != "-")
                        throw new ArgumentsException($"unrecognized arguments: {arg}");
                    options.Args.Add(arg);
                    break;
            }
        }
        var max = options.Command == "autostart" ? 1 : 0;
        if (options.Command == "autostart" && options.Args.Count == 0)
            throw new ArgumentsException("the following arguments are required: action");
        if (options.Args.Count > max)
            throw new ArgumentsException($"unrecognized arguments: {string.Join(" ", options.Args.Skip(max))}");
        if (options.Command == "autostart" && options.Args[0] is not ("on" or "off" or "restart" or "status"))
            throw new ArgumentsException($"argument action: invalid choice: '{options.Args[0]}' (choose from 'on', 'off', 'restart', 'status')");
        return options;
    }
}
