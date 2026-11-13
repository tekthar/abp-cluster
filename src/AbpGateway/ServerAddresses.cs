using Microsoft.Extensions.Primitives;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.ServiceDiscovery;

namespace AbpGateway;

/// <summary>
/// The servers' addresses, resolved from a name every few seconds, and patient when there are none
/// yet. A headless Service has no address at all until a server pod is ready, on a first start or a long upgrade, and
/// YARP's resolver then fails the whole configuration, so the gateway would crash and back off for minutes. Here a name
/// that doesn't resolve gives the addresses it had last (none on a first start: requests are answered 503), and is
/// tried again in a few seconds.
/// </summary>
public sealed class ServerAddresses(IDestinationResolver dns, ILogger<ServerAddresses> logger) : IDestinationResolver
{
    public static readonly TimeSpan RetryAfter = TimeSpan.FromSeconds(2);

    private readonly Dictionary<string, IReadOnlyDictionary<string, DestinationConfig>> _last = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();

    public async ValueTask<ResolvedDestinationCollection> ResolveDestinationsAsync(IReadOnlyDictionary<string, DestinationConfig> destinations,
        CancellationToken cancellationToken)
    {
        string names = string.Join(",", destinations.OrderBy(d => d.Key, StringComparer.Ordinal).Select(d => $"{d.Key}={d.Value.Address}"));
        try
        {
            ResolvedDestinationCollection resolved = await dns.ResolveDestinationsAsync(destinations, cancellationToken);
            lock (_lock)
            {
                _last[names] = resolved.Destinations;
            }

            return resolved;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            IReadOnlyDictionary<string, DestinationConfig> kept;
            lock (_lock)
            {
                kept = _last.GetValueOrDefault(names) ?? new Dictionary<string, DestinationConfig>();
            }

            logger.LogWarning("{Names} didn't resolve ({Error}); {Count} addresses kept, tried again in {Seconds} s",
                string.Join(", ", destinations.Values.Select(d => d.Address)), ex.GetBaseException().Message, kept.Count, RetryAfter.TotalSeconds);
            return new ResolvedDestinationCollection(kept, new CancellationChangeToken(new CancellationTokenSource(RetryAfter).Token));
        }
    }
}
