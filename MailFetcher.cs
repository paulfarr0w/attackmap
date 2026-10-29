using System.Net;
using System.Text.RegularExpressions;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;

namespace AttackMap;

public sealed record FetchStats(int Matched, int New, int Bans, int Skipped);

/// <summary>Reads new fail2ban emails over IMAP and adds their bans to the history. Never changes the mailbox.</summary>
public static class MailFetcher
{
    public static FetchStats FetchNewBans(AppConfig config, AppState state, GeoLookup geo, Action<string> log)
    {
        var s = config.Imap;
        if (string.IsNullOrWhiteSpace(s.Host)) throw new InvalidOperationException("Imap.Host is not set in the config.");
        if (string.IsNullOrWhiteSpace(s.SenderFilter)) throw new InvalidOperationException("Imap.SenderFilter is not set in the config.");

        var ignore = new HashSet<string>(config.IgnoreIps.Select(i => Fail2BanParser.NormaliseIp(i) ?? i));

        using var client = new ImapClient();
        if (s.AcceptInvalidCertificate)
            client.ServerCertificateValidationCallback = (_, _, _, _) => true;

        var security = Enum.TryParse<SecureSocketOptions>(s.Security, ignoreCase: true, out var opt)
            ? opt
            : throw new InvalidOperationException($"Imap.Security '{s.Security}' is not one of: Auto, SslOnConnect, StartTls, StartTlsWhenAvailable, None.");

        client.Connect(s.Host, s.Port, security);
        client.Authenticate(s.Username, s.Password);

        var folder = string.IsNullOrWhiteSpace(s.Folder) || s.Folder.Equals("INBOX", StringComparison.OrdinalIgnoreCase)
            ? client.Inbox
            : client.GetFolder(s.Folder);
        folder.Open(FolderAccess.ReadOnly);

        // If the server renumbered the folder, start from the beginning (Message-IDs still prevent double counting).
        if (folder.UidValidity != state.UidValidity)
        {
            state.UidValidity = folder.UidValidity;
            state.LastUid = 0;
        }

        SearchQuery query = SearchQuery.FromContains(s.SenderFilter);
        if (!string.IsNullOrWhiteSpace(s.SubjectFilter))
            query = query.And(SearchQuery.SubjectContains(s.SubjectFilter));
        if (state.LastUid > 0)
            query = query.And(SearchQuery.Uids(new UniqueIdRange(new UniqueId(state.LastUid + 1), UniqueId.MaxValue)));

        var uids = folder.Search(query).Where(u => u.Id > state.LastUid).ToList();
        int newCount = 0, bans = 0, skipped = 0;

        if (uids.Count > 0)
        {
            var summaries = folder.Fetch(uids, new FetchRequest(
                MessageSummaryItems.UniqueId | MessageSummaryItems.Envelope | MessageSummaryItems.InternalDate));

            foreach (var summary in summaries.OrderBy(m => m.UniqueId.Id))
            {
                var uid = summary.UniqueId;
                var env = summary.Envelope;
                var key = !string.IsNullOrWhiteSpace(env?.MessageId)
                    ? "mid:" + env.MessageId
                    : $"uid:{folder.UidValidity}:{uid.Id}";

                if (state.ProcessedMessages.Contains(key))
                {
                    state.LastUid = Math.Max(state.LastUid, uid.Id);
                    continue;
                }
                newCount++;

                var subject = env?.Subject;
                var ban = Fail2BanParser.ParseSubject(subject);
                if (ban is null && !Fail2BanParser.IsStartStopNotice(subject))
                {
                    // Subject didn't match the usual format: download the message and look in the body.
                    var message = folder.GetMessage(uid);
                    ban = Fail2BanParser.ParseBody(message.TextBody ?? HtmlToText(message.HtmlBody));
                }

                if (ban is null)
                {
                    skipped++;
                    log($"  skipped (no ban found): {subject}");
                }
                else if (!ignore.Contains(ban.Ip))
                {
                    var when = env?.Date ?? summary.InternalDate ?? DateTimeOffset.UtcNow;
                    var country = geo.Lookup(ban.Ip);
                    state.Events.Add(new BanEvent(ban.Ip, when, ban.Jail, country.Code, country.Name));
                    bans++;
                }

                state.ProcessedMessages.Add(key);
                state.LastUid = Math.Max(state.LastUid, uid.Id);
            }
        }

        client.Disconnect(true);
        return new FetchStats(uids.Count, newCount, bans, skipped);
    }

    private static string? HtmlToText(string? html) =>
        html is null ? null : WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]+>", " "));
}
