using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using AbpGateway;

namespace AbpGateway.Tests;

/// <summary>
/// Which address the server is given as the visitor's (X-Forwarded-For), and which protocol (X-Forwarded-Proto): those a
/// trusted ingress passes on, never ones a caller names itself. Kestrel listens on loopback only here, so each request
/// says in <c>X-Test-Peer</c> which address it stands for: an ingress in the pod network, or a caller on the internet.
/// </summary>
public sealed class ForwardedForTests : IAsyncLifetime
{
    private const string Ingress = "10.42.0.7";
    private WebApplication _server = null!;

    public async Task InitializeAsync() => _server = await GatewayTests.StartEcho("server");

    public async Task DisposeAsync() => await _server.DisposeAsync();

    [Theory]
    [InlineData("203.0.113.7")]
    [InlineData("198.51.100.9")]
    public async Task Behind_a_trusted_ingress_the_server_gets_each_visitors_address(string visitor)
    {
        Assert.Equal(visitor, await ForwardedFor("10.42.0.0/16", peer: Ingress, header: visitor));
    }

    [Fact]
    public async Task An_address_the_visitor_wrote_before_the_ingress_added_theirs_is_dropped()
    {
        Assert.Equal("203.0.113.7", await ForwardedFor("10.42.0.0/16", peer: Ingress, header: "192.0.2.1, 203.0.113.7"));
    }

    [Fact]
    public async Task A_caller_outside_the_trusted_networks_cannot_name_its_own_address()
    {
        Assert.Equal("203.0.113.50", await ForwardedFor("10.42.0.0/16", peer: "203.0.113.50", header: "192.0.2.1"));
    }

    [Fact]
    public async Task Without_trusted_proxies_an_ingress_is_the_visitor_as_before()
    {
        Assert.Equal(Ingress, await ForwardedFor(null, peer: Ingress, header: "203.0.113.7"));
    }

    [Fact]
    public async Task Several_networks_and_a_dual_stack_listeners_mapped_addresses_are_trusted()
    {
        // Kestrel in a container listens on [::]:8080, so an IPv4 ingress connects as ::ffff:a.b.c.d.
        Assert.Equal("203.0.113.7", await ForwardedFor("10.42.0.0/16; 172.16.0.0/12", peer: "::ffff:172.18.0.3", header: "203.0.113.7"));
    }

    [Fact]
    public async Task Behind_a_trusted_ingress_ending_TLS_the_server_is_told_https()
    {
        GatewayTests.Echo echo = await Through("10.42.0.0/16", peer: Ingress, forwardedFor: "203.0.113.7", forwardedProto: "https");
        Assert.Equal("https", echo.ForwardedProto);
        Assert.Equal("203.0.113.7", echo.ForwardedFor);
    }

    [Fact]
    public async Task A_caller_outside_the_trusted_networks_cannot_say_it_used_https()
    {
        GatewayTests.Echo echo = await Through("10.42.0.0/16", peer: "203.0.113.50", forwardedFor: "192.0.2.1", forwardedProto: "https");
        Assert.Equal("http", echo.ForwardedProto);
    }

    [Fact]
    public async Task An_ingress_that_doesnt_say_the_protocol_leaves_the_gateways_own()
    {
        GatewayTests.Echo echo = await Through("10.42.0.0/16", peer: Ingress, forwardedFor: "203.0.113.7");
        Assert.Equal("http", echo.ForwardedProto);
    }

    /// <summary>
    /// The browser's protections: HSTS only over HTTPS (here, as a trusted ingress says), nosniff and a
    /// referrer policy always, the safe CSP enforced and the full one reported only.
    /// </summary>
    [Fact]
    public async Task Every_answer_carries_the_browsers_protections_and_HSTS_only_over_https()
    {
        HttpResponseMessage plain = await RawThrough("10.0.0.0/8", "10.0.0.5", "198.51.100.7", null);
        Assert.False(plain.Headers.Contains("Strict-Transport-Security"));
        Assert.Equal("nosniff", plain.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal(SecurityHeaders.Enforced, plain.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Contains("script-src 'self'", plain.Headers.GetValues("Content-Security-Policy-Report-Only").Single(), StringComparison.Ordinal);

        HttpResponseMessage secure = await RawThrough("10.0.0.0/8", "10.0.0.5", "198.51.100.7", "https");
        Assert.StartsWith("max-age=31536000", secure.Headers.GetValues("Strict-Transport-Security").Single(), StringComparison.Ordinal);
    }

    /// <summary>The X-Forwarded-For the server gets, for one request from <paramref name="peer"/> through a gateway trusting <paramref name="trusted"/>.</summary>
    private async Task<string> ForwardedFor(string? trusted, string peer, string header) =>
        (await Through(trusted, peer, header)).ForwardedFor;

    /// <summary>What the server gets, for one request from <paramref name="peer"/> through a gateway trusting <paramref name="trusted"/>.</summary>
    private async Task<GatewayTests.Echo> Through(string? trusted, string peer, string forwardedFor, string? forwardedProto = null)
    {
        HttpResponseMessage response = await RawThrough(trusted, peer, forwardedFor, forwardedProto);
        return (await response.Content.ReadFromJsonAsync<GatewayTests.Echo>())!;
    }

    /// <summary>The gateway's answer itself, for one request as above.</summary>
    private async Task<HttpResponseMessage> RawThrough(string? trusted, string peer, string forwardedFor, string? forwardedProto)
    {
        await using WebApplication gateway = GatewayApp.Build([], builder =>
        {
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Configuration["Gateway:Server"] = GatewayTests.Address(_server);
            builder.Configuration["Gateway:Web"] = GatewayTests.Address(_server);
            builder.Configuration["Gateway:TrustedProxies"] = trusted;
            builder.Services.AddSingleton<IStartupFilter, PeerFromHeader>();
        });
        await gateway.StartAsync();

        using HttpClient client = new() { BaseAddress = new Uri(GatewayTests.Address(gateway)) };
        using HttpRequestMessage request = new(HttpMethod.Post, "/api/guard/session");
        request.Headers.Add("X-Test-Peer", peer);
        request.Headers.Add("X-Forwarded-For", forwardedFor);
        if (forwardedProto is not null)
        {
            request.Headers.Add("X-Forwarded-Proto", forwardedProto);
        }

        HttpResponseMessage response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await response.Content.LoadIntoBufferAsync();
        return response;
    }

    /// <summary>Runs before the gateway's own pipeline: the connection comes from the address in <c>X-Test-Peer</c>.</summary>
    private sealed class PeerFromHeader : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((http, go) =>
            {
                http.Connection.RemoteIpAddress = IPAddress.Parse(http.Request.Headers["X-Test-Peer"].ToString());
                http.Request.Headers.Remove("X-Test-Peer");
                return go(http);
            });
            next(app);
        };
    }
}
