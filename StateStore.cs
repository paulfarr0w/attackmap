using System.Text.Json;
using System.Text.Json.Serialization;

namespace AttackMap;

/// <summary>One ban taken from one fail2ban email.</summary>
public sealed record BanEvent(
    string Ip,
    DateTimeOffset Time,
    string? Jail,
    string CountryCode,
    string CountryName);

/// <summary>
/// The all-time history. Kept as JSON next to the program so each run only has to read
/// new emails, and old emails can be deleted from the mailbox without losing the stats.
/// </summary>
public sealed class AppState
{
    /// <summary>Message-IDs already counted, so no email is ever counted twice.</summary>
    public HashSet<string> ProcessedMessages { get; set; } = new();

    /// <summary>The mailbox folder's UIDVALIDITY and highest UID seen, so a run only asks for newer mail.</summary>
    public uint UidValidity { get; set; }
    public uint LastUid { get; set; }

    public List<BanEvent> Events { get; set; } = new();
}

public static class StateStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static AppState Load(string path)
    {
        if (!File.Exists(path)) return new AppState();
        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<AppState>(stream, Options) ?? new AppState();
    }

    public static void Save(string path, AppState state)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        // Write to a temp file and swap it in, so a crash mid-write can't corrupt the history.
        var temp = path + ".tmp";
        using (var stream = File.Create(temp))
            JsonSerializer.Serialize(stream, state, Options);
        File.Move(temp, path, overwrite: true);
    }
}
