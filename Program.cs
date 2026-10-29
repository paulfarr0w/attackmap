namespace AttackMap;

public static class Program
{
    private const string Usage = """
        attackmap - turns fail2ban ban emails into a web page of who is attacking you.

        Usage: attackmap [--config <file>] [--rebuild] [--rescan] [--list-folders]

          --config <file>  Settings file (default: appsettings.json next to the program)
          --rebuild        Don't check mail; just regenerate the page from the saved history
          --rescan         Forget the saved history and re-read every matching email from scratch
          --list-folders   Connect and print every IMAP folder's full name, to help set Imap.Folder
        """;

    public static int Main(string[] args)
    {
        string? configPath = null;
        bool rebuildOnly = false, rescan = false, listFolders = false;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--config" when i + 1 < args.Length: configPath = args[++i]; break;
                case "--rebuild": rebuildOnly = true; break;
                case "--rescan": rescan = true; break;
                case "--list-folders": listFolders = true; break;
                case "-h" or "--help": Console.WriteLine(Usage); return 0;
                default: Console.Error.WriteLine($"Unknown option: {args[i]}\n\n{Usage}"); return 2;
            }
        }

        configPath ??= Path.Combine(AppContext.BaseDirectory, "appsettings.json");

        try
        {
            var config = AppConfig.Load(configPath);

            if (listFolders)
            {
                MailFetcher.ListFolders(config, Log);
                return 0;
            }

            // Stop two cron runs overlapping if a mail server is slow.
            using var lockFile = TryLock(config.StateFile + ".lock");
            if (lockFile is null)
            {
                Log("Another run is still in progress; exiting.");
                return 0;
            }

            var state = rescan ? new AppState() : StateStore.Load(config.StateFile);

            if (!rebuildOnly)
            {
                using var geo = new GeoLookup(config.GeoIpDatabase);
                var stats = MailFetcher.FetchNewBans(config, state, geo, Log);
                StateStore.Save(config.StateFile, state);
                Log($"Checked mail: {stats.New} new email(s), {stats.Bans} ban(s) added, {stats.Skipped} skipped. " +
                    $"History now holds {state.Events.Count} ban(s).");
            }

            var html = ReportBuilder.Build(state, config, DateTimeOffset.Now);
            WriteAtomically(config.OutputFile, html);
            Log($"Wrote {config.OutputFile}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} ERROR: {ex.Message}");
            return 1;
        }
    }

    private static void Log(string message) =>
        Console.WriteLine($"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} {message}");

    private static FileStream? TryLock(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static void WriteAtomically(string path, string contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // Same folder as the target, so the swap is an instant rename and visitors never see half a page.
        var temp = Path.Combine(Path.GetDirectoryName(path)!, "." + Path.GetFileName(path) + ".tmp");
        File.WriteAllText(temp, contents);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        File.Move(temp, path, overwrite: true);
    }
}
