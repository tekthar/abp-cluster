using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Forwarder;

namespace AbpGateway;

/// <summary>
/// Where each request goes: the server's paths (<see cref="GatewayOptions.ServerPaths"/>, <see cref="GatewayOptions.Rewritten"/>),
/// and every other path to the web. A lower order wins, so the web takes what the server's paths leave.
/// </summary>
public static class GatewayRoutes
{
    public const string ServerCluster = "server";
    public const string WebCluster = "web";

    public static IReadOnlyList<RouteConfig> Routes(GatewayOptions options)
    {
        List<RouteConfig> routes = [];
        int n = 0;
        foreach (string path in options.ServerPaths)
        {
            routes.Add(Route($"server-{n++}", path, ServerCluster, order: 1));
        }

        foreach (GatewayRewrite rewrite in options.Rewritten)
        {
            routes.Add(Route($"server-{n++}", rewrite.Path, ServerCluster, order: 1, pathPattern: rewrite.ServerPath));
        }

        routes.Add(Route("web", "/{**rest}", WebCluster, order: 2));
        return routes;
    }

    /// <summary>
    /// The servers: a destination each, or each one behind a name the gateway resolves. With a readiness path each is
    /// checked every <paramref name="healthEvery"/> (two failures and it's left out until it answers), and one two
    /// requests in a row couldn't reach is left out at once (<see cref="FailedConnects"/>). An MCP session stays on its
    /// server (<see cref="McpAffinity"/>).
    /// </summary>
    public static IReadOnlyList<ClusterConfig> Clusters(IReadOnlyList<string> serverUrls, string webUrl, TimeSpan healthEvery, GatewayOptions options) =>
    [
        Cluster(ServerCluster, serverUrls, options.StreamIdle) with
        {
            LoadBalancingPolicy = McpAffinity.PolicyName,
            HealthCheck = new HealthCheckConfig
            {
                Active = options.ReadinessPath is { Length: > 0 } ready
                    ? new ActiveHealthCheckConfig { Enabled = true, Interval = healthEvery, Timeout = TimeSpan.FromSeconds(3), Policy = "ConsecutiveFailures", Path = ready }
                    : null,
                Passive = new PassiveHealthCheckConfig { Enabled = true, Policy = FailedConnects.PolicyName, ReactivationPeriod = FailedConnects.LeftOut },
            },
            Metadata = new Dictionary<string, string> { ["ConsecutiveFailuresHealthPolicy.Threshold"] = "2" },
        },
        Cluster(WebCluster, [webUrl], options.StreamIdle),
    ];

    private static RouteConfig Route(string id, string path, string cluster, int order, string? pathPattern = null)
    {
        // The servers see the public host (a web app builds absolute links from it), plus the usual X-Forwarded-* headers.
        List<IReadOnlyDictionary<string, string>> transforms = [new Dictionary<string, string> { ["RequestHeaderOriginalHost"] = "true" }];
        if (pathPattern is not null)
        {
            transforms.Add(new Dictionary<string, string> { ["PathPattern"] = pathPattern });
        }

        return new RouteConfig
        {
            RouteId = id,
            ClusterId = cluster,
            Order = order,
            Match = new RouteMatch { Path = path },
            Transforms = transforms,
        };
    }

    private static ClusterConfig Cluster(string id, IReadOnlyList<string> urls, TimeSpan streamIdle) => new()
    {
        ClusterId = id,
        Destinations = urls.Select((url, i) => (Key: i == 0 ? "main" : $"main-{i}", Url: url))
            .ToDictionary(d => d.Key, d => new DestinationConfig { Address = d.Url }),
        HttpRequest = new ForwarderRequestConfig { ActivityTimeout = streamIdle },
    };
}
