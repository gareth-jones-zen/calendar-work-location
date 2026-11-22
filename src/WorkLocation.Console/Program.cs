// calendar-work-location.exe. Output is Unicode to a console and UTF-8 when redirected (see ConsoleText).
return WorkLocation.Cli.Run(args, new WorkLocation.CliHost { Out = WinBackground.ConsoleText.Out, Err = WinBackground.ConsoleText.Error });
