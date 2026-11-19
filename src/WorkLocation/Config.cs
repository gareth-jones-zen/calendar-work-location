using System.Text.RegularExpressions;
using PyCompat;
using Tomlyn;
using Tomlyn.Model;

namespace WorkLocation;

/// <summary>Settings that can't be used, or a request that can't be met: the command line reports the message and
/// exits 2.</summary>
public class UsageException(string message) : Exception(message);

/// <summary>How one kind of day's entry looks: its title, show-as and categories.</summary>
public sealed record EntryStyle(string Title, string ShowAs, IReadOnlyList<string> Categories);

/// <summary>The settings file. See the README and <see cref="Template"/>. Read strictly: an unknown key, or a value of
/// the wrong type, is an error that names it.</summary>
public sealed class Config
{
    public const string EnvConfig = "WORK_LOCATION_CONFIG";

    public static readonly string[] ShowAsValues = ["free", "tentative", "busy", "oof", "workingElsewhere"];

    // Explicit "\n" (a raw literal would take the .cs file's line endings), written with CRLF.
    public const string Template =
        "# work-location settings. See the README.\n" +
        "# base = \"base-config.toml\"        # settings shared with other tools\n" +
        "# login_hint = \"you@example.com\"\n" +
        "# tenant = \"organizations\"          # or your organisation's tenant id\n" +
        "# time_zone = \"GMT Standard Time\"   # default: this PC's\n" +
        "\n" +
        "enabled = true                     # false: runs do nothing (`remove` takes its entries out)\n" +
        "weeks_ahead = 4                    # today and this many weeks on\n" +
        "\n" +
        "[office]\n" +
        "title = \"Working from the office\"\n" +
        "# show_as = \"free\"\n" +
        "# categories = []\n" +
        "\n" +
        "[remote]\n" +
        "title = \"Working from home\"\n" +
        "\n" +
        "[elsewhere]\n" +
        "title = \"At {subject}\"\n" +
        "# subjects = 'conference|summit|expo'   # all-day events with these in the subject\n" +
        "# show_as_signals = [\"workingElsewhere\"]   # and events shown as these\n";

    public static readonly string[] KnownKeys =
    [
        "login_hint", "tenant", "msal_cache", "log_file", "state_file", "time_zone", "enabled", "weeks_ahead",
        "every_minutes", "after_unlock_minutes", "notify", "max_deletes", "body", "office", "remote", "elsewhere", "leave",
    ];

    public required string Path { get; init; }
    public string? LoginHint { get; init; }
    public string Tenant { get; init; } = "organizations";
    public string? MsalCache { get; init; }
    public required string LogFile { get; init; }
    public required string StateFile { get; init; }
    public required TimeZoneInfo TimeZone { get; init; }
    public bool Enabled { get; init; } = true;
    public int WeeksAhead { get; init; } = 4;
    public int EveryMinutes { get; init; } = 15;
    public int? AfterUnlockMinutes { get; init; } = 1;
    public bool Notify { get; init; } = true;
    public int MaxDeletes { get; init; } = 10;
    public string Body { get; init; } = "Written by work-location from your work plan and calendar. Changes made here are overwritten.";
    public required EntryStyle Office { get; init; }
    public required EntryStyle Remote { get; init; }
    public required EntryStyle Elsewhere { get; init; }

    /// <summary>All-day events whose subject matches: elsewhere that day.</summary>
    public Regex? ElsewhereSubjects { get; init; }

    /// <summary>Events shown as one of these: elsewhere that day.</summary>
    public IReadOnlyList<string> ElsewhereShowAs { get; init; } = ["workingElsewhere"];

    /// <summary>Your own entries (no one else invited) matching this are leave.</summary>
    public required Regex LeaveSubjects { get; init; }

    /// <summary>Your own entries in these categories are leave (bank holidays, often).</summary>
    public IReadOnlyList<string> LeaveCategories { get; init; } = ["Holiday"];

    /// <summary>Leave covering at least this many hours of a day makes it a day off (a half day isn't).</summary>
    public double LeaveMinHours { get; init; } = 6;

    public const string DefaultElsewhere = @"\b(conference|summit|expo|exhibition|trade show|off-?site)\b";
    public const string DefaultLeave = @"^\s*(holiday\b|annual leave|bank holiday|on leave|summer holidays?)";

    public static string? Environment(string name) =>
        string.IsNullOrEmpty(name) || name.Contains('\0') ? null : System.Environment.GetEnvironmentVariable(name);

    public static string Local(Func<string, string?>? env = null)
    {
        env ??= Environment;
        var local = env("LOCALAPPDATA");
        return PyPath.Pure(System.IO.Path.Combine(string.IsNullOrEmpty(local) ? PyPath.Home(env) : local, "work-location"));
    }

    public static string DefaultPath(Func<string, string?>? env = null)
    {
        env ??= Environment;
        var appData = env("APPDATA");
        return PyPath.Pure(System.IO.Path.Combine(string.IsNullOrEmpty(appData) ? PyPath.Home(env) : appData, "work-location", "config.toml"));
    }

    public static Config Load(string? path = null, Func<string, string?>? env = null)
    {
        env ??= Environment;
        var given = !string.IsNullOrEmpty(path) ? path : env(EnvConfig) is { Length: > 0 } fromEnv ? fromEnv : DefaultPath(env);
        var full = PyPath.Resolve(given);
        if (!File.Exists(full))
            throw new UsageException($"No settings file at {full}. Run `work-location config` to create one.");
        TomlTable raw;
        try
        {
            var text = PyStr.ReadText(full);
            if (text.StartsWith('﻿'))
                text = text[1..];
            var read = BaseSettings.Read(text, full, env, KnownKeys);
            raw = read.Raw;
            env = read.Env;
        }
        catch (TomlException error)
        {
            throw new UsageException($"{full}: {error.Message.TrimEnd()}");
        }
        catch (FormatException error)
        {
            throw new UsageException(error.Message.TrimEnd());
        }
        catch (Exception error) when (error is not (IOException or UnauthorizedAccessException or UsageException))
        {
            throw new UsageException($"{full}: {error.Message.TrimEnd()}");
        }
        foreach (var key in raw.Keys)
            if (!KnownKeys.Contains(key))
                throw new UsageException($"{full}: unknown setting '{key}' (known: {string.Join(", ", KnownKeys)}).");

        var folder = System.IO.Path.GetDirectoryName(full)!;
        var expandWith = env;
        string PathSetting(string value)
        {
            var expanded = PyPath.ExpandVars(PyPath.ExpandUser(value, expandWith), expandWith);
            return PyPath.IsAbsolute(expanded) ? PyPath.Pure(expanded) : PyPath.ResolveAgainst(folder, expanded);
        }
        UsageException Bad(string message) => new($"{full}: {message}");
        void Strict(TomlTable table, string where, params string[] keys)
        {
            foreach (var key in table.Keys)
                if (!keys.Contains(key))
                    throw Bad($"[{where}] has an unknown setting '{key}' (known: {string.Join(", ", keys)}).");
        }
        string? Text(TomlTable table, string key, string where) =>
            table.TryGetValue(key, out var v) ? v as string is { Length: > 0 } s ? s : throw Bad($"{where} should be text, and not empty.") : null;
        bool Flag(string key, bool fallback) =>
            raw.TryGetValue(key, out var v) ? v as bool? ?? throw Bad($"{key} should be true or false.") : fallback;
        int Whole(string key, int fallback, int min, int max) =>
            raw.TryGetValue(key, out var v) ? v is long n && n >= min && n <= max ? (int)n : throw Bad($"{key} should be a whole number from {min} to {max}.") : fallback;
        IReadOnlyList<string>? Strings(TomlTable table, string key, string where) =>
            table.TryGetValue(key, out var v)
                ? v is TomlArray a && a.All(x => x is string) ? a.Select(x => (string)x!).ToList() : throw Bad($"{where} should be a list of text.")
                : null;
        Regex Pattern(string text, string where)
        {
            try
            {
                return new Regex(text, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            }
            catch (ArgumentException error)
            {
                throw Bad($"{where} isn't a valid pattern: {error.Message}");
            }
        }
        TomlTable Section(string name)
        {
            if (!raw.TryGetValue(name, out var v))
                return [];
            return v as TomlTable ?? throw Bad($"{name} should be a [{name}] section.");
        }
        EntryStyle Style(string name, TomlTable table, string title, params string[] extra)
        {
            Strict(table, name, ["title", "show_as", "categories", .. extra]);
            var showAs = Text(table, "show_as", $"{name}.show_as") ?? "free";
            if (!ShowAsValues.Contains(showAs))
                throw Bad($"{name}.show_as should be one of {string.Join(", ", ShowAsValues)}.");
            return new EntryStyle(Text(table, "title", $"{name}.title") ?? title, showAs, Strings(table, "categories", $"{name}.categories") ?? []);
        }

        TimeZoneInfo zone;
        var zoneName = Text(raw, "time_zone", "time_zone");
        try
        {
            zone = zoneName is null ? TimeZoneInfo.Local : TimeZoneInfo.FindSystemTimeZoneById(zoneName);
        }
        catch (Exception error) when (error is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            throw Bad($"time_zone '{zoneName}' isn't a time zone this PC knows (a Windows name, such as \"GMT Standard Time\").");
        }

        var elsewhere = Section("elsewhere");
        var leave = Section("leave");
        Strict(leave, "leave", "subjects", "categories", "min_hours");
        var minHours = leave.TryGetValue("min_hours", out var mh)
            ? mh switch { long l and >= 0 and <= 24 => l, double d and >= 0 and <= 24 => d, _ => throw Bad("leave.min_hours should be a number of hours, 0 to 24.") }
            : 6;
        var signals = Strings(elsewhere, "show_as_signals", "elsewhere.show_as_signals") ?? ["workingElsewhere"];
        foreach (var s in signals)
            if (!ShowAsValues.Contains(s))
                throw Bad($"elsewhere.show_as_signals: '{s}' isn't one of {string.Join(", ", ShowAsValues)}.");
        var subjects = elsewhere.TryGetValue("subjects", out var sv)   // "" turns it off
            ? sv as string ?? throw Bad("elsewhere.subjects should be text (a pattern), or \"\" for none.")
            : DefaultElsewhere;

        return new Config
        {
            Path = full,
            LoginHint = Text(raw, "login_hint", "login_hint"),
            Tenant = Text(raw, "tenant", "tenant") ?? "organizations",
            MsalCache = Text(raw, "msal_cache", "msal_cache") is { } cache ? PathSetting(cache) : null,
            LogFile = Text(raw, "log_file", "log_file") is { } log ? PathSetting(log) : PyPath.Pure(System.IO.Path.Combine(Local(env), "work-location.log")),
            StateFile = Text(raw, "state_file", "state_file") is { } state ? PathSetting(state) : PyPath.Pure(System.IO.Path.Combine(Local(env), "state.json")),
            TimeZone = zone,
            Enabled = Flag("enabled", true),
            WeeksAhead = Whole("weeks_ahead", 4, 0, 52),
            EveryMinutes = Whole("every_minutes", 15, 1, 1440),
            AfterUnlockMinutes = raw.ContainsKey("after_unlock_minutes") ? Whole("after_unlock_minutes", 1, 0, 60) is var u && u > 0 ? u : null : 1,
            Notify = Flag("notify", true),
            MaxDeletes = Whole("max_deletes", 10, 0, 1000),
            Body = Text(raw, "body", "body") ?? "Written by work-location from your work plan and calendar. Changes made here are overwritten.",
            Office = Style("office", Section("office"), "Working from the office"),
            Remote = Style("remote", Section("remote"), "Working from home"),
            Elsewhere = Style("elsewhere", elsewhere, "At {subject}", "subjects", "show_as_signals"),
            ElsewhereSubjects = subjects.Length == 0 ? null : Pattern(subjects, "elsewhere.subjects"),
            ElsewhereShowAs = signals,
            LeaveSubjects = Pattern(Text(leave, "subjects", "leave.subjects") ?? DefaultLeave, "leave.subjects"),
            LeaveCategories = Strings(leave, "categories", "leave.categories") ?? ["Holiday"],
            LeaveMinHours = minHours,
        };
    }

    public static string CreateDefault(string? path, Func<string, string?>? env = null)
    {
        var full = PyPath.Resolve(!string.IsNullOrEmpty(path) ? path : DefaultPath(env));
        if (!File.Exists(full))
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
            File.WriteAllText(full, Template.Replace("\n", "\r\n"), new System.Text.UTF8Encoding(false));
        }
        return full;
    }
}
