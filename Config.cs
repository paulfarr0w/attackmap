using System.Text.Json;
using System.Text.Json.Serialization;

namespace AttackMap;

/// <summary>Settings loaded from appsettings.json.</summary>
public sealed class AppConfig
{
    public ImapSettings Imap { get; set; } = new();

    /// <summary>Path to a country-level .mmdb file (MaxMind GeoLite2-Country or DB-IP Lite Country).</summary>
    public string GeoIpDatabase { get; set; } = "GeoLite2-Country.mmdb";

    /// <summary>Where the program keeps its running history (all bans ever seen).</summary>
    public string StateFile { get; set; } = "attackmap-state.json";

    /// <summary>The HTML page to write, normally inside your web server's public folder.</summary>
    public string OutputFile { get; set; } = "attackmap.html";

    /// <summary>Unix permissions for the output file, e.g. 640 for rw-r-----. Ignored on Windows.</summary>
    public int OutputFileMode { get; set; } = 640;

    public string Title { get; set; } = "Who is attacking my server";

    /// <summary>How many rows to show in the "top attacking IPs" table.</summary>
    public int TopIpCount { get; set; } = 25;

    /// <summary>How many countries to show as bars (the rest are listed in the full table).</summary>
    public int TopCountryCount { get; set; } = 15;

    /// <summary>How many IPs to list when you click a country on the map.</summary>
    public int TopIpsPerCountry { get; set; } = 10;

    /// <summary>"light", "dark" or "auto" (follow the visitor's system setting).</summary>
    public string Theme { get; set; } = "light";

    /// <summary>Credit line shown under the page, as some free GeoIP databases require. Plain text.</summary>
    public string GeoIpAttribution { get; set; } = "";
    public string GeoIpAttributionUrl { get; set; } = "";

    /// <summary>IPs to leave out of the stats, e.g. your own address if you ever lock yourself out.</summary>
    public List<string> IgnoreIps { get; set; } = new();

    public static AppConfig Load(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException(
                $"Config file not found: {path}. Copy appsettings.example.json to appsettings.json and fill it in.");

        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };
        var config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path), options)
                     ?? throw new InvalidDataException($"Config file {path} is empty.");

        // Relative paths in the config are taken relative to the config file, so cron's
        // working directory doesn't matter.
        var baseDir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        config.GeoIpDatabase = Path.GetFullPath(config.GeoIpDatabase, baseDir);
        config.StateFile = Path.GetFullPath(config.StateFile, baseDir);
        config.OutputFile = Path.GetFullPath(config.OutputFile, baseDir);

        // Let the password live in an environment variable instead of the file if preferred.
        var envPassword = Environment.GetEnvironmentVariable("ATTACKMAP_IMAP_PASSWORD");
        if (!string.IsNullOrEmpty(envPassword))
            config.Imap.Password = envPassword;

        return config;
    }
}

public sealed class ImapSettings
{
    public string Host { get; set; } = "";
    public int Port { get; set; } = 993;

    /// <summary>MailKit SecureSocketOptions name: Auto, SslOnConnect, StartTls, StartTlsWhenAvailable or None.</summary>
    public string Security { get; set; } = "Auto";

    /// <summary>Only set this for a home mail server with a self-signed certificate.</summary>
    public bool AcceptInvalidCertificate { get; set; }

    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public string Folder { get; set; } = "INBOX";

    /// <summary>Matched against the From header (contains, case-insensitive), e.g. "fail2ban@myserver.com".</summary>
    public string SenderFilter { get; set; } = "";

    /// <summary>Optional extra filter on the subject, e.g. "[Fail2Ban]". Leave empty to skip.</summary>
    public string SubjectFilter { get; set; } = "";
}
