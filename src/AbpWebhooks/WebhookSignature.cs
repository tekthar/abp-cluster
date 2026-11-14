using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace AbpWebhooks;

/// <summary>
/// How a delivery is signed, and how a receiver checks it: <c>X-Webhook-Signature: sha256=</c> the HMAC-SHA256 of
/// <c>{timestamp}.{body}</c> with the webhook's secret, in lowercase hex, beside <c>X-Webhook-Timestamp</c> (Unix seconds).
/// </summary>
public static class WebhookSignature
{
    /// <summary>The HMAC-SHA256 of "{timestamp}.{body}" with the secret, in lowercase hex.</summary>
    public static string Sign(string secret, string timestamp, string body) =>
        Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{timestamp}.{body}"))).ToLowerInvariant();

    /// <summary>The signature header's value: <c>sha256=</c> and the signature.</summary>
    public static string Header(string secret, string timestamp, string body) => "sha256=" + Sign(secret, timestamp, body);

    /// <summary>
    /// A receiver's check: the header is the body's signature with the secret, compared in constant time, and the
    /// timestamp is within <paramref name="tolerance"/> of now (five minutes when not given), so an old delivery can't be
    /// sent again by someone who saw it.
    /// </summary>
    public static bool Verify(string secret, string timestamp, string body, string? header, DateTimeOffset? now = null, TimeSpan? tolerance = null)
    {
        if (header is null || !long.TryParse(timestamp, NumberStyles.None, CultureInfo.InvariantCulture, out long seconds))
        {
            return false;
        }

        DateTimeOffset at = DateTimeOffset.FromUnixTimeSeconds(seconds);
        if ((now ?? DateTimeOffset.UtcNow) - at is var age && age.Duration() > (tolerance ?? TimeSpan.FromMinutes(5)))
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Header(secret, timestamp, body)), Encoding.ASCII.GetBytes(header));
    }
}

/// <summary>A delivery's id for an event and a webhook, the same however often the event is heard.</summary>
public static class WebhookIds
{
    /// <summary>A name-based UUID (version 8) from SHA-256 of the event's id and the webhook's.</summary>
    public static Guid Delivery(Guid eventId, Guid webhookId)
    {
        Span<byte> both = stackalloc byte[32];
        eventId.TryWriteBytes(both[..16], bigEndian: true, out _);
        webhookId.TryWriteBytes(both[16..], bigEndian: true, out _);
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(both, hash);
        hash[6] = (byte)((hash[6] & 0x0F) | 0x80);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        return new Guid(hash[..16], bigEndian: true);
    }
}
