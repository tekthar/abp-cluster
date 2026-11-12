using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;

namespace AbpCluster.Sites;

/// <summary>
/// The key an active site signs its state with, which every site has from the database they shared. The default makes
/// it in the sites' schema on the first start; a host that keeps its keys protected elsewhere gives its own.
/// </summary>
public interface ISiteSigningKey
{
    Task<byte[]> KeyAsync(CancellationToken cancellationToken);
}

/// <summary>
/// An active site's signature on its state: an HMAC with the installation's signing key. A site stands down only for a
/// state signed with it, and signed within <see cref="FreshFor"/>.
/// </summary>
public static class SiteSignature
{
    /// <summary>How old a signature may be (clocks between sites drift a little).</summary>
    public static readonly TimeSpan FreshFor = TimeSpan.FromMinutes(2);

    public static SiteStateDto Sign(SiteStateDto state, byte[] key, DateTimeOffset now)
    {
        SiteStateDto dated = state with { SignedAt = now, Signature = null };
        return dated with { Signature = Compute(dated, key) };
    }

    /// <summary>Whether the state was signed with the key, lately.</summary>
    public static bool Verify(SiteStateDto state, byte[] key, DateTimeOffset now) =>
        state is { SignedAt: { } at, Signature: { } signature }
        && (now - at).Duration() <= FreshFor
        && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(signature), Encoding.ASCII.GetBytes(Compute(state, key)));

    private static string Compute(SiteStateDto state, byte[] key)
    {
        string payload = string.Join('\n',
            state.Site ?? "", state.State, state.Active?.Site ?? "", state.Active?.Epoch.ToString(CultureInfo.InvariantCulture) ?? "",
            state.Active?.Since.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture) ?? "",
            state.SignedAt?.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture) ?? "");
        return Convert.ToHexStringLower(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(payload)));
    }
}

/// <summary>
/// Reads another site's state at its address (the address, then <see cref="SitesOptions.StatePath"/>): no redirects, a
/// few seconds at most. The address is a site admin's; nothing but this one path is ever asked of it.
/// </summary>
public static class SiteStatusClient
{
    private static readonly HttpClient Http = new(new SocketsHttpHandler { AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(3) })
    {
        Timeout = TimeSpan.FromSeconds(5),
    };

    /// <summary>The site's state, or why it couldn't be read.</summary>
    public static async Task<(SiteStateDto? State, string? Error)> ReadAsync(string address, string statePath, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(address.TrimEnd('/') + "/" + statePath.TrimStart('/'), UriKind.Absolute, out Uri? url) || url.Scheme is not ("http" or "https"))
        {
            return (null, "The address isn't an http or https address.");
        }

        try
        {
            using HttpResponseMessage response = await Http.GetAsync(url, cancellationToken);
            // Active answers 200, every other state 503, both with the state.
            if (response.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.ServiceUnavailable))
            {
                return (null, $"It answered {(int)response.StatusCode}.");
            }

            return (await response.Content.ReadFromJsonAsync<SiteStateDto>(cancellationToken), null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or NotSupportedException)
        {
            if (ex is TaskCanceledException && cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            return (null, ex.Message);
        }
    }
}

/// <summary>
/// The operators' break-glass key, as servers hold it: its SHA-256 in hex. Taken as people make it: alone, in either case,
/// or as <c>sha256sum</c> prints it (the hash, then "  -" or a file name), with quotes around it or not.
/// </summary>
public static class BreakGlassKeyHash
{
    /// <summary>The hash's 32 bytes, or null when the value isn't one.</summary>
    public static byte[]? Parse(string? value)
    {
        string? first = value?.Trim().Trim('"', '\'').Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return first is { Length: 64 } hex && hex.All(char.IsAsciiHexDigit) ? Convert.FromHexString(hex) : null;
    }
}

/// <summary>
/// Checks a break-glass key, in constant time: five wrong keys from an address in a quarter of an hour, and that address
/// waits. Without a hash, nothing takes a key.
/// </summary>
public sealed class BreakGlass(string? hash)
{
    private readonly ConcurrentDictionary<string, (int Count, DateTime Since)> _wrong = new(StringComparer.Ordinal);
    private readonly byte[]? _hash = BreakGlassKeyHash.Parse(hash);

    /// <summary>Set, but not a hash this server can use: it takes no key, as if none were set.</summary>
    public bool Unreadable => _hash is null && !string.IsNullOrWhiteSpace(hash);

    public bool Set => _hash is not null;

    /// <summary>Null when the key is right; else why not.</summary>
    public BreakGlassRefusal? Check(string? key, string? from)
    {
        if (_hash is null)
        {
            return BreakGlassRefusal.NoKey;
        }

        string address = from ?? "";
        if (_wrong.TryGetValue(address, out var wrong) && wrong.Count >= 5 && DateTime.UtcNow - wrong.Since < TimeSpan.FromMinutes(15))
        {
            return BreakGlassRefusal.TooMany;
        }

        byte[] given = SHA256.HashData(Encoding.UTF8.GetBytes(key ?? ""));
        if (CryptographicOperations.FixedTimeEquals(given, _hash))
        {
            _wrong.TryRemove(address, out _);
            return null;
        }

        _wrong.AddOrUpdate(address, (1, DateTime.UtcNow), (_, w) => DateTime.UtcNow - w.Since < TimeSpan.FromMinutes(15) ? (w.Count + 1, w.Since) : (1, DateTime.UtcNow));
        return BreakGlassRefusal.Wrong;
    }
}

public enum BreakGlassRefusal
{
    /// <summary>This server has no break-glass key.</summary>
    NoKey,

    /// <summary>Not the key.</summary>
    Wrong,

    /// <summary>Too many wrong keys from that address lately.</summary>
    TooMany,
}

/// <summary>
/// A fingerprint of the settings every site must share: each site says it with its state, and the sites overview flags
/// one that differs. Secrets go in only hashed, and the fingerprint is a hash of it all, so it says only whether they match.
/// </summary>
public static class SiteFingerprint
{
    public static string Of(IConfiguration configuration, SitesOptions options)
    {
        if (options.Fingerprint is { } own)
        {
            return own(configuration);
        }

        static string Hash(string? value) => string.IsNullOrEmpty(value) ? "" : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
        string all = string.Join('\n',
            options.SharedSettings.Select(key => $"{key}={(configuration[key] ?? "").TrimEnd('/')}")
                .Concat(options.SharedSecrets.Select(key => $"{key}#{Hash(configuration[key])}")));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(all)))[..16];
    }
}
