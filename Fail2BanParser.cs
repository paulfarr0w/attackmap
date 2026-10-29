using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace AttackMap;

public sealed record BanInfo(string Ip, string? Jail);

/// <summary>
/// Pulls the banned IP (and jail name) out of fail2ban's notification emails.
///
/// fail2ban's stock mail actions (sendmail, sendmail-whois, mail-whois, sendmail-whois-lines, ...)
/// all send a subject like
///     [Fail2Ban] sshd: banned 203.0.113.7 from myserver
/// and a body starting
///     The IP 203.0.113.7 has just been banned by Fail2Ban after 5 attempts against sshd.
///
/// The subject is tried first because it needs no body download. The whois variants
/// include whois output with many other IP addresses in the body, so we deliberately never
/// fall back to "first IP found in the body".
/// </summary>
public static partial class Fail2BanParser
{
    [GeneratedRegex(@"\[Fail2Ban\]\s*(?<jail>[^:\r\n]+?)\s*:\s*banned\s+(?<ip>[0-9A-Fa-f:.]+)", RegexOptions.IgnoreCase)]
    private static partial Regex SubjectRegex();

    [GeneratedRegex(@"The\s+IP\s+(?<ip>[0-9A-Fa-f:.]+)\s+has\s+just\s+been\s+banned(?:\s+by\s+Fail2Ban)?(?:\s+after\s+\d+\s+attempts\s+against\s+(?<jail>[^\s]+?)\.?(?=\s|$))?", RegexOptions.IgnoreCase)]
    private static partial Regex BodyRegex();

    [GeneratedRegex(@"\[Fail2Ban\][^\r\n]*:\s*(started|stopped)\b", RegexOptions.IgnoreCase)]
    private static partial Regex StartStopRegex();

    public static BanInfo? ParseSubject(string? subject)
    {
        if (string.IsNullOrWhiteSpace(subject)) return null;
        var m = SubjectRegex().Match(subject);
        if (!m.Success) return null;
        var ip = NormaliseIp(m.Groups["ip"].Value);
        return ip is null ? null : new BanInfo(ip, CleanJail(m.Groups["jail"].Value));
    }

    public static BanInfo? ParseBody(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        var m = BodyRegex().Match(body);
        if (!m.Success) return null;
        var ip = NormaliseIp(m.Groups["ip"].Value);
        return ip is null ? null : new BanInfo(ip, CleanJail(m.Groups["jail"].Value));
    }

    /// <summary>True for fail2ban's "jail started/stopped" notices, which carry no attacker.</summary>
    public static bool IsStartStopNotice(string? subject) =>
        !string.IsNullOrEmpty(subject) && StartStopRegex().IsMatch(subject);

    /// <summary>Validates an address and returns it in canonical form, or null if it isn't one.</summary>
    public static string? NormaliseIp(string candidate)
    {
        candidate = candidate.Trim().TrimEnd('.', ',', ';', ':');
        if (!IPAddress.TryParse(candidate, out var addr)) return null;
        // IPAddress.TryParse accepts things like "123" (= 0.0.0.123); insist on a dotted quad for IPv4.
        if (addr.AddressFamily == AddressFamily.InterNetwork && candidate.Count(c => c == '.') != 3) return null;
        if (addr.IsIPv4MappedToIPv6) addr = addr.MapToIPv4();
        return addr.ToString();
    }

    private static string? CleanJail(string jail)
    {
        jail = jail.Trim();
        return jail.Length == 0 ? null : jail;
    }
}
