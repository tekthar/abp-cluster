using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using AbpGateway;

namespace AbpGateway.Tests;

/// <summary>
/// The gateway in front of two stand-ins for the .NET server and the web, each answering with what it received: which
/// one a path reaches, under which path, and with which headers; that streams and big uploads go through. The product
/// here adds paths of its own and two rewritten ones, as a plugin host and a git host's webhooks would.
/// </summary>
public sealed class GatewayTests : IAsyncLifetime
{
    /// <summary>A product's options: the common paths, its plugins' assets, two public paths under the API, and readiness.</summary>
    internal static GatewayOptions ProductOptions()
    {
        GatewayOptions options = new() { ReadinessPath = "/api/platform/ready" };
        options.ServerPaths.Add("/plugins-web/{**rest}");
        options.ServerPaths.Add("/plugins-mobile/{**rest}");
        options.Rewritten.Add(new GatewayRewrite("/hooks/git/{id}", "/api/hooks/git/{id}"));
        options.Rewritten.Add(new GatewayRewrite("/scans/{id}", "/api/repositories/{id}/scan"));
        return options;
    }

    private WebApplication _server = null!;
    private WebApplication _web = null!;
    private WebApplication _gateway = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _server = await StartEcho("server");
        _web = await StartEcho("web");
        _gateway = GatewayApp.Build([], ProductOptions(), builder =>
        {
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Configuration["Gateway:Server"] = Address(_server);
            builder.Configuration["Gateway:Web"] = Address(_web);
        });
        await _gateway.StartAsync();
        _client = new HttpClient { BaseAddress = new Uri(Address(_gateway)), Timeout = TimeSpan.FromMinutes(2) };
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _gateway.DisposeAsync();
        await _server.DisposeAsync();
        await _web.DisposeAsync();
    }

    [Theory]
    [InlineData("/", "web", "/")]
    [InlineData("/projects/PAY/board", "web", "/projects/PAY/board")]
    [InlineData("/_next/static/chunks/app.js", "web", "/_next/static/chunks/app.js")]
    [InlineData("/apps/acme.reports/cases", "web", "/apps/acme.reports/cases")]
    [InlineData("/api/projects", "server", "/api/projects")]
    [InlineData("/api/records/PAY-4/comments", "server", "/api/records/PAY-4/comments")]
    [InlineData("/connect/token", "server", "/connect/token")]
    [InlineData("/connect/revoke", "server", "/connect/revoke")]
    [InlineData("/.well-known/openid-configuration", "server", "/.well-known/openid-configuration")]
    [InlineData("/mcp", "server", "/mcp")]
    [InlineData("/mcp/_discover", "server", "/mcp/_discover")]
    [InlineData("/plugins-web/acme.reports/1.2.0/remoteEntry.js", "server", "/plugins-web/acme.reports/1.2.0/remoteEntry.js")]
    [InlineData("/plugins-mobile/acme.reports/1.2.0/remoteEntry.bundle", "server", "/plugins-mobile/acme.reports/1.2.0/remoteEntry.bundle")]
    [InlineData("/hooks/git/6c249f7a", "server", "/api/hooks/git/6c249f7a")]
    [InlineData("/scans/91365199", "server", "/api/repositories/91365199/scan")]
    // Signing in, and allowing an app, are the server's too; the web's own endpoints are under /_web.
    [InlineData("/api/guard/session", "server", "/api/guard/session")]
    [InlineData("/connect/authorize", "server", "/connect/authorize")]
    [InlineData("/_web/i18n/refresh", "web", "/_web/i18n/refresh")]
    [InlineData("/oauth/consent", "web", "/oauth/consent")]
    public async Task Each_path_reaches_its_server(string path, string who, string arrivedAs)
    {
        Echo echo = await Get(path);
        Assert.Equal(who, echo.Who);
        Assert.Equal(arrivedAs, echo.Path);
    }

    [Fact]
    public async Task The_query_goes_along()
    {
        Echo echo = await Get("/api/query/saved?projectKey=PAY&take=5");
        Assert.Equal("?projectKey=PAY&take=5", echo.Query);
    }

    [Fact]
    public async Task Credentials_go_through_untouched_and_the_public_host_is_kept()
    {
        using HttpRequestMessage request = new(HttpMethod.Get, "/api/guard/me");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "a-phone's-token");
        request.Headers.Add("Cookie", "site_session=abc");
        HttpResponseMessage response = await _client.SendAsync(request);
        Echo echo = (await response.Content.ReadFromJsonAsync<Echo>())!;

        Assert.Equal("server", echo.Who);
        Assert.Equal("Bearer a-phone's-token", echo.Authorization);
        Assert.Equal("site_session=abc", echo.Cookie);
        Assert.Equal(new Uri(Address(_gateway)).Authority, echo.Host);
        Assert.False(string.IsNullOrEmpty(echo.ForwardedFor), "the visitor's address is passed on");
    }

    [Fact]
    public async Task A_stream_arrives_as_it_is_sent()
    {
        using HttpRequestMessage request = new(HttpMethod.Get, "/api/events");
        using HttpResponseMessage response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        await using Stream stream = await response.Content.ReadAsStreamAsync();
        using StreamReader reader = new(stream);

        // The stand-in sends one event, then waits two seconds before the next: the first must arrive before that.
        System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        string? first = await reader.ReadLineAsync();
        Assert.Equal("data: first", first);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(1.5), $"the first event waited {clock.Elapsed} for the rest");
    }

    [Fact]
    public async Task An_upload_bigger_than_kestrels_default_goes_through()
    {
        byte[] body = new byte[40 * 1024 * 1024];
        Random.Shared.NextBytes(body);
        using ByteArrayContent content = new(body);
        HttpResponseMessage response = await _client.PostAsync("/api/records/PAY-4/attachments", content);
        Echo echo = (await response.Content.ReadFromJsonAsync<Echo>())!;
        Assert.Equal("server", echo.Who);
        Assert.Equal(body.Length, echo.BodyLength);
    }

    [Fact]
    public async Task The_gateway_says_it_is_up()
    {
        HttpResponseMessage response = await _client.GetAsync("/_gateway/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private async Task<Echo> Get(string path)
    {
        HttpResponseMessage response = await _client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<Echo>())!;
    }

    internal static string Address(WebApplication app) =>
        app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();

    /// <summary>A stand-in that answers every request with what it received; <c>/api/events</c> streams instead.</summary>
    internal static async Task<WebApplication> StartEcho(string who)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Limits.MaxRequestBodySize = null);
        WebApplication app = builder.Build();
        app.Run(async http =>
        {
            if (http.Request.Path == "/api/events")
            {
                http.Response.ContentType = "text/event-stream";
                await http.Response.WriteAsync("data: first\n\n");
                await http.Response.Body.FlushAsync();
                await Task.Delay(TimeSpan.FromSeconds(2));
                await http.Response.WriteAsync("data: second\n\n");
                return;
            }

            long length = 0;
            byte[] buffer = new byte[81920];
            int read;
            while ((read = await http.Request.Body.ReadAsync(buffer)) > 0)
            {
                length += read;
            }

            await http.Response.WriteAsJsonAsync(new Echo(
                who,
                http.Request.Path.Value ?? "",
                http.Request.QueryString.Value ?? "",
                http.Request.Headers.Host.ToString(),
                http.Request.Headers.Authorization.ToString(),
                http.Request.Headers.Cookie.ToString(),
                http.Request.Headers["X-Forwarded-For"].ToString(),
                http.Request.Headers["X-Forwarded-Proto"].ToString(),
                length));
        });
        await app.StartAsync();
        return app;
    }

    internal sealed record Echo(string Who, string Path, string Query, string Host, string Authorization, string Cookie, string ForwardedFor, string ForwardedProto, long BodyLength);
}
