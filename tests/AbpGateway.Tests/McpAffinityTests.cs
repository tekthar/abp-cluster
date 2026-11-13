using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using AbpGateway;

namespace AbpGateway.Tests;

/// <summary>
/// The gateway in front of several servers: requests spread over them, a server that stops or isn't
/// ready is left out, and an MCP session stays on the server that started it, which gets its own id back.
/// </summary>
public sealed class McpAffinityTests : IAsyncLifetime
{
    private readonly List<FakeServer> _servers = [];
    private WebApplication _gateway = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        foreach (string name in new[] { "A", "B", "C" })
        {
            _servers.Add(await FakeServer.StartAsync(name));
        }

        _gateway = GatewayApp.Build([], GatewayTests.ProductOptions(), builder =>
        {
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Configuration["Gateway:Server"] = string.Join(", ", _servers.Select(s => GatewayTests.Address(s.App)));
            builder.Configuration["Gateway:Web"] = GatewayTests.Address(_servers[0].App);
            builder.Configuration["Gateway:HealthCheckSeconds"] = "1";
        });
        await _gateway.StartAsync();
        _client = new HttpClient { BaseAddress = new Uri(GatewayTests.Address(_gateway)) };
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _gateway.DisposeAsync();
        foreach (FakeServer server in _servers)
        {
            await server.App.DisposeAsync();
        }
    }

    private async Task<(HttpStatusCode Status, Answer? Answer, string? SessionHeader)> McpAsync(string? session)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, "/mcp") { Content = JsonContent.Create(new { jsonrpc = "2.0", id = 1, method = "ping" }) };
        if (session is not null)
        {
            request.Headers.TryAddWithoutValidation(McpAffinity.Header, session);
        }

        using HttpResponseMessage response = await _client.SendAsync(request);
        string? header = response.Headers.TryGetValues(McpAffinity.Header, out IEnumerable<string>? values) ? values.First() : null;
        return (response.StatusCode, response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<Answer>() : null, header);
    }

    [Fact]
    public async Task A_session_stays_on_the_server_that_started_it_and_that_server_gets_its_own_id()
    {
        (_, Answer? started, string? session) = await McpAsync(null);
        Assert.NotNull(session);
        Assert.Equal($"session-{started!.Who}", McpAffinity.Split(session)!.Value.Id);

        for (int i = 0; i < 30; i++)
        {
            (HttpStatusCode status, Answer? answer, _) = await McpAsync(session);
            Assert.Equal(HttpStatusCode.OK, status);
            Assert.Equal(started.Who, answer!.Who);
            Assert.Equal($"session-{started.Who}", answer.Session);
        }
    }

    [Fact]
    public async Task Requests_without_a_session_spread_over_the_servers()
    {
        HashSet<string> answered = [];
        for (int i = 0; i < 60; i++)
        {
            answered.Add((await _client.GetFromJsonAsync<Answer>("/api/projects"))!.Who);
        }

        Assert.True(answered.Count >= 2, $"only {string.Join(", ", answered)} answered");
    }

    [Fact]
    public async Task A_stopped_servers_sessions_go_to_another_which_starts_them_afresh()
    {
        (_, Answer? started, string? session) = await McpAsync(null);
        FakeServer gone = _servers.Single(s => s.Name == started!.Who);
        await gone.App.StopAsync();

        // The health checks leave it out within a few seconds; until then its requests may fail.
        Answer? elsewhere = null;
        for (int i = 0; i < 50 && elsewhere is null; i++)
        {
            (HttpStatusCode status, Answer? answer, _) = await McpAsync(session);
            elsewhere = status == HttpStatusCode.OK ? answer : null;
            await Task.Delay(elsewhere is null ? 200 : 0);
        }

        Assert.NotNull(elsewhere);
        Assert.NotEqual(started!.Who, elsewhere!.Who);
        Assert.Equal($"session-{started.Who}", elsewhere.Session); // a real server says "Session not found", and the agent starts again
    }

    [Fact]
    public async Task A_server_that_stops_costs_at_most_two_failed_requests()
    {
        // The readiness check is slowed down here, so only failed connections can leave the server out.
        await using WebApplication slow = GatewayApp.Build([], GatewayTests.ProductOptions(), builder =>
        {
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Configuration["Gateway:Server"] = string.Join(", ", _servers.Select(s => GatewayTests.Address(s.App)));
            builder.Configuration["Gateway:Web"] = GatewayTests.Address(_servers[0].App);
            builder.Configuration["Gateway:HealthCheckSeconds"] = "600";
        });
        await slow.StartAsync();
        using HttpClient client = new() { BaseAddress = new Uri(GatewayTests.Address(slow)) };
        await _servers[2].App.StopAsync();

        int failed = 0;
        for (int i = 0; i < 60; i++)
        {
            failed += (await client.GetAsync("/api/projects")).IsSuccessStatusCode ? 0 : 1;
        }

        Assert.InRange(failed, 1, 2);
    }

    [Fact]
    public async Task A_server_that_is_not_ready_gets_nothing_until_it_is()
    {
        FakeServer busy = _servers[1];
        busy.Ready = false;
        await Task.Delay(TimeSpan.FromSeconds(3.5));
        for (int i = 0; i < 40; i++)
        {
            Assert.NotEqual(busy.Name, (await _client.GetFromJsonAsync<Answer>("/api/projects"))!.Who);
        }

        busy.Ready = true;
        HashSet<string> answered = [];
        for (int i = 0; i < 100 && !answered.Contains(busy.Name); i++)
        {
            answered.Add((await _client.GetFromJsonAsync<Answer>("/api/projects"))!.Who);
            await Task.Delay(50);
        }

        Assert.Contains(busy.Name, answered);
    }

    [Fact]
    public async Task A_key_that_names_no_server_is_ignored_and_another_id_with_a_dot_is_left_alone()
    {
        string forged = McpAffinity.Join("http://10.99.99.99:8080", "made-up");
        (HttpStatusCode status, Answer? answer, _) = await McpAsync(forged);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("made-up", answer!.Session);

        (status, answer, _) = await McpAsync("not.a-key-of-ours");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("not.a-key-of-ours", answer!.Session);
    }

    [Theory]
    [InlineData("http://10.42.0.17:8080", "8c1f2e")]
    [InlineData("http://[fd00::1]:8080", "abc-def_123")]
    [InlineData("http://site-server:80", "x.y")]
    public void A_joined_id_splits_back_into_its_servers_key_and_the_servers_id(string address, string id)
    {
        (string key, string back) = McpAffinity.Split(McpAffinity.Join(address, id))!.Value;
        Assert.Equal(McpAffinity.Key(address), key);
        Assert.Equal(id, back);
        Assert.DoesNotContain('.', key);
    }

    internal sealed record Answer(string Who, string? Session);

    /// <summary>A server: ready unless told otherwise; on <c>/mcp</c> it starts a session when none is given, and says which it got.</summary>
    private sealed class FakeServer
    {
        public required string Name { get; init; }
        public required WebApplication App { get; init; }
        public volatile bool Ready = true;

        public static async Task<FakeServer> StartAsync(string name)
        {
            WebApplicationBuilder builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            WebApplication app = builder.Build();
            FakeServer server = new() { Name = name, App = app };
            app.Run(async http =>
            {
                if (http.Request.Path == "/api/platform/ready")
                {
                    http.Response.StatusCode = server.Ready ? 200 : 503;
                    return;
                }

                string? session = http.Request.Headers[McpAffinity.Header].ToString() is { Length: > 0 } given ? given : null;
                if (http.Request.Path == "/mcp" && session is null)
                {
                    http.Response.Headers[McpAffinity.Header] = $"session-{name}";
                }

                await http.Response.WriteAsJsonAsync(new Answer(name, session));
            });
            await app.StartAsync();
            return server;
        }
    }
}
