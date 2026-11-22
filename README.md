# calendar-work-location

Puts an all-day entry in your Outlook calendar for each working day, saying where you're working, so colleagues see
it at a glance: **"Working from home"**, **"Working from the office"**, or **"At XConf Europe"** when you're at a
conference. Outlook and Teams' work plan knows office and remote, but people looking at your calendar don't see it,
and it has no "elsewhere"; plenty of people keep entries like these by hand.

```
calendar-work-location plan
```

```
  Mon 23 Nov  remote     Working from home                   add           (work plan: remote)
  Tue 24 Nov  elsewhere  At XConf Europe                     add           (all-day "XConf Europe")
  Wed 25 Nov  remote     Working from home                   unchanged     (work plan: remote)
  Thu 26 Nov  office     Working from the office             update (now "Working from home")  (work plan: office)
  Fri 27 Nov  leave      -                                                 (leave: Holiday)
23/11 to 20/12: 2 to add, 1 to update, 0 to remove, 1 unchanged (nothing changed)
```

## Install

Windows (x64) only; nothing else is needed.

1. Download `calendar-work-location-<version>-win-x64.zip` from [Releases](https://github.com/gareth-jones-zen/calendar-work-location/releases).
2. Unzip it to a folder that will stay put, such as `%LOCALAPPDATA%\Programs\calendar-work-location`.
3. `calendar-work-location config` creates the settings file; edit the titles. `calendar-work-location sign-in` signs you in (once).
4. `calendar-work-location plan` shows what it would do; `calendar-work-location run` does it; `calendar-work-location autostart on` keeps it
   up to date by itself (when `[schedule]` says: every 15 minutes and at logon by default).

**It needs Calendars.ReadWrite** on Microsoft's "Graph Command Line Tools" app; `sign-in` asks for it.

### Upgrading from work-location (0.1.x)

The tool was called `work-location` until 0.2.0. Its calendar entries keep their hidden `work-location.day` property, so
the renamed tool recognises them and never duplicates them. To move an install: `work-location autostart off`; unzip
0.2.0 to `%LOCALAPPDATA%\Programs\calendar-work-location`; point `log_file` and `state_file` at the new names (or move
the files) and run `calendar-work-location plan`, which should show nothing to create or remove (the body text now
names the new tool, so existing entries are updated once); then `calendar-work-location autostart on` and delete the
old folder.

## How each day is decided

Today and the next `weeks_ahead` weeks (default 4), or, if `weeks_ahead` isn't set, to the same date
`calendar_months_ahead` calendar months on (usually shared from a base file with other calendar tools; the work plan
is read 30 days at a time, as Graph refuses more than 31 days in one request), Monday to Friday:

1. **A bank holiday or leave: no entry.** A bank holiday is one of your own entries whose subject matches
   `leave.bank_holiday_subjects` (by default starting "Bank holiday") or that is in a `leave.bank_holiday_categories`
   category (default "Bank holiday", calendar-bank-holidays' category). Leave is your own entries (no one else
   invited) whose subject matches `leave.subjects` (by default starting "Holiday", "Annual leave", "On leave" or
   "Summer holidays") or in a `leave.categories` category (default "Holiday"), covering at least `leave.min_hours`
   (default 6) of the day. A half day off still gets an entry. Your leave entry already says where you are.

2. **Elsewhere: "At {subject}".** An all-day event whose subject matches `elsewhere.subjects` (conference, summit,
   expo, exhibition, trade show, off-site), or else any event shown as *Working elsewhere* (`show_as_signals`). The
   title uses that event's subject, without a second "At" ("At XConf Europe, limited availability" becomes "At XConf
   Europe, limited availability", not "At At ...").
3. **Otherwise your work plan:** any office time that day gives the office entry, else remote time the remote one.
   Days with no work plan get nothing. Your work plan's "time off" stretches (out-of-office blocks such as travel) are
   ignored.

The leave and elsewhere rules are [background-tools-lib](https://github.com/gareth-jones-zen/background-tools-lib)'s
`CalendarDays` (0.1.2 on), shared with [out-of-office](https://github.com/gareth-jones-zen/out-of-office), so the two
agree on what a day is.

### [leave]

The `[leave]` keys are background-tools-lib's shared leave settings (`subjects`, `categories`, `min_hours`,
`half_day`, `bank_holiday_subjects`, `bank_holiday_categories`; 0.6.0 on): see its README, "`[leave]`: the shared leave
settings". With `base = "base-config.toml"`, a base file's `[leave]` can hold them once for every tool that reads
leave (this tool, calendar-out-of-office, meeting-preps), as long as this file has a `[leave]` section, even an empty
one. The only difference here: `categories` defaults to `["Holiday"]`. Settings from before 0.6.0 that list bank
holidays as leave (`"bank holiday"` in `subjects`, `"Bank holiday"` in `categories`) still give the same plan, and the
log suggests moving them.


**It only ever changes its own entries**, which carry a hidden property (the day they stand for, and a hash of what
was written). The calendar is the only record, so an entry deleted by hand is written again, and one edited by hand
is put back. An all-day entry of yours with the same title (made by hand) is left alone, and none is added. Past days
are never touched. Declined and cancelled events don't count.

**Safety:** if your work plan comes back empty for the whole period while entries would be removed, or a run would
remove more than `max_deletes` (default 10) entries, it changes nothing and says why. A run that fails is logged, and
notified on the third failure in a row (a run on waking can find the network not up yet), or at once for a
sign-in or a safety stop; once only, until a run succeeds. A write whose answer is lost is never resent: the next run reads the calendar.

## Commands

```
calendar-work-location plan               each day: where you are, why, and what a run would change
calendar-work-location run [--dry-run]    make the calendar match
calendar-work-location remove [--dry-run] take out its entries from today on
calendar-work-location autostart on       run every few minutes and at logon (off, restart, status)
calendar-work-location sign-in            sign in to Microsoft (once; opens a browser)
calendar-work-location config             create the settings file (if missing) and say where it is
```

**Exit codes:** 0 done; 1 failed, or stopped for safety; 2 settings, arguments or sign-in.

## Settings

`%APPDATA%\calendar-work-location\config.toml` by default (`--config` or `WORK_LOCATION_CONFIG` for another). Read strictly:
an unknown setting, or one of the wrong type, is an error that names it.

```toml
base = "base-config.toml"         # optional: settings shared with other tools (background-tools-lib's BaseSettings)
login_hint = "you@example.com"
tenant = "organizations"          # or your organisation's tenant id
msal_cache = "..."                # optional: another tool's token cache, to share its sign-in
time_zone = "GMT Standard Time"   # optional (a Windows name); default this PC's
log_file = "..."                  # optional; default %LOCALAPPDATA%\calendar-work-location\calendar-work-location.log
state_file = "..."                # optional: only whether runs are failing
enabled = true                    # false: runs do nothing (`remove` takes its entries out)
weeks_ahead = 4                   # today and this many weeks on; wins over calendar_months_ahead
# calendar_months_ahead = 3       # if weeks_ahead isn't set: today to the same date this many calendar months on
                                  # (usually shared from a base file with other calendar tools)
notify = true                     # a notification when runs start failing
max_deletes = 10
body = "Written by calendar-work-location from your work plan and calendar. Changes made here are overwritten."

[office]
title = "Working from the office"
show_as = "free"                  # free (default), tentative, busy, oof or workingElsewhere
categories = []                   # Outlook categories, such as ["Office"]

[remote]
title = "Working from home"

[elsewhere]
title = "At {subject}"
subjects = '\b(conference|summit|expo|exhibition|trade show|off-?site)\b'   # "" for none
show_as_signals = ["workingElsewhere"]

[leave]                           # or in a base file: see [leave] below
subjects = '^\s*(holiday\b|annual leave|on leave|summer holidays?)'
categories = ["Holiday"]
min_hours = 6
bank_holiday_subjects = '^\s*bank holiday\b'
bank_holiday_categories = ["Bank holiday"]

[schedule]                        # when the task runs: see below
every_minutes = 15
```

### [schedule]

When the task runs, in the keys every background-tools-lib tool shares (`every_minutes`, `daily_at`, `hours`, `days`,
`at_logon`, `after_unlock_minutes`, `end_date`): see background-tools-lib's README, "[schedule]: the shared scheduling
settings". This tool's defaults:

```toml
[schedule]
every_minutes = 15
at_logon = true
after_unlock_minutes = 0          # 0: no run after unlocking
```

`autostart on` registers the task from it and records it; `autostart status` says when the settings have changed since
(run `autostart on` again). A run outside `hours` or `days`, or after `end_date`, does nothing. Before 0.5.0,
`every_minutes` and `after_unlock_minutes` were top-level settings: they still work, logged as deprecated, but not
together with `[schedule]`.

**Why "free" by default:** an all-day entry shown as anything else can make you look unavailable all day to people
scheduling with you.

## Building

.NET 11 (see `global.json`), with the shared libraries from
[background-tools-lib](https://github.com/gareth-jones-zen/background-tools-lib) checked out beside this repo:

```
dotnet test -c Release
dotnet publish src/WorkLocation.Console -c Release -r win-x64 -o out    # Native AOT
```

A tag `vX.Y.Z` builds a release (`.github/workflows/release.yml`), against the library commit pinned there.
