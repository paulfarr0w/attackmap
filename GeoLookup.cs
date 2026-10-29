using MaxMind.Db;
using MaxMind.GeoIP2;

namespace AttackMap;

public sealed record CountryInfo(string Code, string Name);

/// <summary>Offline IP-to-country lookup using a MaxMind-format .mmdb file.</summary>
public sealed class GeoLookup : IDisposable
{
    public static readonly CountryInfo Unknown = new("??", "Unknown");

    private readonly DatabaseReader _reader;
    private readonly Dictionary<string, CountryInfo> _cache = new();

    public GeoLookup(string databasePath)
    {
        if (!File.Exists(databasePath))
            throw new FileNotFoundException(
                $"GeoIP database not found: {databasePath}. See README for where to download a free one.");

        // Each run is one short-lived process, so there's nothing to gain from the default
        // MemoryMapped mode's cross-process sharing. It also relies on a named OS-level sync
        // object under /tmp that a locked-down service account may not be able to open
        // ("The system cannot open the device or file specified"). Loading the (small,
        // country-level) database straight into managed memory avoids that entirely.
        _reader = new DatabaseReader(databasePath, FileAccessMode.Memory);
    }

    public CountryInfo Lookup(string ip)
    {
        if (_cache.TryGetValue(ip, out var cached)) return cached;

        var result = Unknown;
        try
        {
            if (_reader.TryCountry(ip, out var response) && response is not null)
            {
                // Prefer where the address is used; fall back to where it is registered.
                var code = response.Country.IsoCode ?? response.RegisteredCountry.IsoCode;
                var name = response.Country.Name ?? response.RegisteredCountry.Name ?? code;
                if (!string.IsNullOrEmpty(code))
                    result = new CountryInfo(code.ToUpperInvariant(), name ?? code);
            }
        }
        catch (Exception)
        {
            // A malformed address or an odd database record shouldn't stop the run.
        }

        _cache[ip] = result;
        return result;
    }

    public void Dispose() => _reader.Dispose();
}
