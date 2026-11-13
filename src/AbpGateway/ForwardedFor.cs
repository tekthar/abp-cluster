using Microsoft.AspNetCore.HttpOverrides;

namespace AbpGateway;

/// <summary>
/// Behind an ingress controller or a load balancer, the visitor's address comes in X-Forwarded-For, and the protocol
/// they used (https where TLS ends at the ingress) in X-Forwarded-Proto. The gateway believes both from loopback and
/// the networks in Gateway:TrustedProxies (CIDR, ';' between), taking the last value of each: the one that proxy added.
/// YARP then sends the servers those values alone, so a caller that isn't trusted can't name its own. The server
/// should believe the gateway's address the same way, and mark its cookies Secure on https.
/// </summary>
public static class ForwardedFor
{
    public static ForwardedHeadersOptions Options(IConfiguration configuration)
    {
        ForwardedHeadersOptions forwarded = new() { ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto };
        foreach (string cidr in (configuration["Gateway:TrustedProxies"] ?? "")
                     .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            forwarded.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(cidr));
        }

        return forwarded;
    }
}
