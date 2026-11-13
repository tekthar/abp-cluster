using System.Runtime.CompilerServices;
using Yarp.ReverseProxy.Forwarder;
using Yarp.ReverseProxy.Health;
using Yarp.ReverseProxy.Model;

namespace AbpGateway;

/// <summary>
/// A server that can't be reached is left out at once: after two requests in a row that couldn't be
/// sent to it (refused, timed out), it gets nothing for 10 seconds. By then the readiness check, every few seconds, has
/// left it out too if it's still down, and lets it back as soon as it answers. A rate over a minute would wait for many
/// more failures, each a person's request that failed.
/// </summary>
public sealed class FailedConnects(IDestinationHealthUpdater health) : IPassiveHealthCheckPolicy
{
    public const string PolicyName = "FailedConnects";
    public static readonly TimeSpan LeftOut = TimeSpan.FromSeconds(10);
    private const int InARow = 2;

    private readonly ConditionalWeakTable<DestinationState, Counter> _failures = [];

    public string Name => PolicyName;

    public void RequestProxied(HttpContext context, ClusterState cluster, DestinationState destination)
    {
        Counter counter = _failures.GetValue(destination, _ => new Counter());
        if (context.Features.Get<IForwarderErrorFeature>()?.Error is not (ForwarderError.Request or ForwarderError.RequestTimedOut))
        {
            Interlocked.Exchange(ref counter.Value, 0);
            return;
        }

        if (Interlocked.Increment(ref counter.Value) >= InARow)
        {
            Interlocked.Exchange(ref counter.Value, 0);
            health.SetPassive(cluster, destination, DestinationHealth.Unhealthy, LeftOut);
        }
    }

    private sealed class Counter
    {
        public int Value;
    }
}
