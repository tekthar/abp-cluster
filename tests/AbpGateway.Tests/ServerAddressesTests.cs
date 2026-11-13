using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using AbpGateway;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.ServiceDiscovery;

namespace AbpGateway.Tests;

/// <summary>
/// The servers' name before any server is ready: a headless Service has no address then, and the
/// gateway must wait for one rather than crash.
/// </summary>
public sealed class ServerAddressesTests
{
    private static readonly Dictionary<string, DestinationConfig> Servers = new() { ["main"] = new() { Address = "http://site-server-pods:8080" } };

    /// <summary>Answers as the DNS resolver would: the addresses given, or the failure of a name with none.</summary>
    private sealed class Dns : IDestinationResolver
    {
        public Dictionary<string, DestinationConfig>? Answer { get; set; }

        public ValueTask<ResolvedDestinationCollection> ResolveDestinationsAsync(IReadOnlyDictionary<string, DestinationConfig> destinations, CancellationToken cancellationToken) =>
            Answer is { } answer
                ? ValueTask.FromResult(new ResolvedDestinationCollection(answer, null))
                : throw new InvalidOperationException("Failed to resolve host 'site-server-pods'", new SocketException((int)SocketError.HostNotFound));
    }

    [Fact]
    public async Task No_address_yet_is_an_empty_cluster_tried_again_soon_and_a_lost_name_keeps_the_last_addresses()
    {
        Dns dns = new();
        ServerAddresses addresses = new(dns, NullLogger<ServerAddresses>.Instance);

        ResolvedDestinationCollection first = await addresses.ResolveDestinationsAsync(Servers, CancellationToken.None);
        Assert.Empty(first.Destinations);
        Assert.NotNull(first.ChangeToken);
        Assert.False(first.ChangeToken!.HasChanged);
        await Task.Delay(ServerAddresses.RetryAfter + TimeSpan.FromMilliseconds(500));
        Assert.True(first.ChangeToken.HasChanged);

        dns.Answer = new() { ["main[10.42.0.7]"] = new() { Address = "http://10.42.0.7:8080", Host = "site-server-pods" } };
        Assert.Equal(["main[10.42.0.7]"], (await addresses.ResolveDestinationsAsync(Servers, CancellationToken.None)).Destinations.Keys);

        // A blip in the cluster's DNS: the pods it had are kept, not dropped.
        dns.Answer = null;
        Assert.Equal(["main[10.42.0.7]"], (await addresses.ResolveDestinationsAsync(Servers, CancellationToken.None)).Destinations.Keys);
    }

    [Fact]
    public async Task A_gateway_whose_servers_name_has_no_address_yet_starts_and_answers_503()
    {
        await using WebApplication gateway = GatewayApp.Build([], builder =>
        {
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Configuration["Gateway:Server"] = "http://site-server-pods.invalid:8080";
            builder.Configuration["Gateway:ResolveServers"] = "true";
            builder.Configuration["Gateway:Web"] = "http://127.0.0.1:9";
        });
        await gateway.StartAsync();

        using HttpClient client = new() { BaseAddress = new Uri(GatewayTests.Address(gateway)) };
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/_gateway/health")).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/api/platform/ready")).StatusCode);
    }
}
