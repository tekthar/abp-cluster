using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.WebUtilities;
using Yarp.ReverseProxy.LoadBalancing;
using Yarp.ReverseProxy.Model;
using Yarp.ReverseProxy.Transforms;
using Yarp.ReverseProxy.Transforms.Builder;

namespace AbpGateway;

/// <summary>
/// Keeps an AI agent's MCP session on the server that started it: a session lives in that server's
/// memory, and another answers "Session not found". The gateway puts the server's address, encoded, before the session
/// id it hands the agent (<c>{server}.{id}</c>), sends every request carrying it to that server, and gives the server the
/// id as it made it. Any gateway pod can, since the key is the server's own address rather than something one gateway
/// learned. A key naming no server that's up is ignored: the request goes to any, which starts the agent afresh.
/// Requests without a session are spread over the servers, the less busy of two picked at random.
/// </summary>
public sealed partial class McpAffinity : ILoadBalancingPolicy
{
    public const string PolicyName = "McpSession";
    public const string Header = "Mcp-Session-Id";

    string ILoadBalancingPolicy.Name => PolicyName;

    public DestinationState? PickDestination(HttpContext context, ClusterState cluster, IReadOnlyList<DestinationState> availableDestinations)
    {
        if (availableDestinations.Count == 0)
        {
            return null;
        }

        if (Split(context.Request.Headers[Header].ToString()) is { } session)
        {
            foreach (DestinationState destination in availableDestinations)
            {
                if (Key(destination.Model.Config.Address) == session.Key)
                {
                    return destination;
                }
            }
        }

        if (availableDestinations.Count == 1)
        {
            return availableDestinations[0];
        }

        int first = Random.Shared.Next(availableDestinations.Count);
        int second = Random.Shared.Next(availableDestinations.Count - 1);
        second += second >= first ? 1 : 0;
        DestinationState a = availableDestinations[first], b = availableDestinations[second];
        return a.ConcurrentRequestCount <= b.ConcurrentRequestCount ? a : b;
    }

    /// <summary>A server's key: its address's host and port, base64url (no dots, so the first dot ends it).</summary>
    public static string Key(string address)
    {
        Uri uri = new(address);
        return WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes($"{uri.Host.ToLowerInvariant()}:{uri.Port}"));
    }

    /// <summary>The id the agent is given: the server's key, a dot, and the server's own id.</summary>
    public static string Join(string address, string id) => $"{Key(address)}.{id}";

    /// <summary>A session id the gateway gave, split into the server's key and the server's own id; null for any other.</summary>
    public static (string Key, string Id)? Split(string? value)
    {
        int dot = value?.IndexOf('.', StringComparison.Ordinal) ?? -1;
        if (value is null || dot <= 0 || dot == value.Length - 1)
        {
            return null;
        }

        string key = value[..dot];
        try
        {
            // Only a key that's an address (host:port): an id of the server's own with a dot in it is left alone.
            return Authority().IsMatch(Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(key))) ? (key, value[(dot + 1)..]) : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>On the server's routes: the server gets its own id, and the agent gets the id with the server's key before it.</summary>
    public static void Transforms(TransformBuilderContext context)
    {
        if (context.Cluster?.ClusterId != GatewayRoutes.ServerCluster)
        {
            return;
        }

        context.AddRequestTransform(transform =>
        {
            if (Split(transform.HttpContext.Request.Headers[Header].ToString()) is { } session)
            {
                transform.ProxyRequest.Headers.Remove(Header);
                transform.ProxyRequest.Headers.TryAddWithoutValidation(Header, session.Id);
            }

            return ValueTask.CompletedTask;
        });
        context.AddResponseTransform(transform =>
        {
            if (transform.ProxyResponse?.Headers.TryGetValues(Header, out IEnumerable<string>? ids) == true
                && ids.FirstOrDefault() is { Length: > 0 } id
                && transform.HttpContext.GetReverseProxyFeature().ProxiedDestination is { } destination)
            {
                transform.HttpContext.Response.Headers[Header] = Join(destination.Model.Config.Address, id);
            }

            return ValueTask.CompletedTask;
        });
    }

    // A timeout, as every pattern here has, so no header can hold a thread.
    private const int TimeoutMilliseconds = 100;

    [GeneratedRegex(@"^(\[[0-9a-f:.]+\]|[a-z0-9.-]+):[0-9]{1,5}$", RegexOptions.None, TimeoutMilliseconds)]
    private static partial Regex Authority();
}
