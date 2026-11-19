// work-location-background.exe: no window, ever. What the scheduled task runs (`run`); its log is the settings' log_file.
return WorkLocation.Cli.Run(args, new WorkLocation.CliHost { Out = TextWriter.Null, Err = TextWriter.Null });
