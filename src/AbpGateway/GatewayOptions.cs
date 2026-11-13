namespace AbpGateway;

/// <summary>
/// What a product tells its gateway (<see cref="GatewayApp.Build(string[], GatewayOptions?, Action{Microsoft.AspNetCore.Builder.WebApplicationBuilder}?)"/>):
/// which paths are its server's, which public paths reach the server under another path, and where the server says it's
/// ready. Where the servers and the web are is configuration (<c>Gateway:Server</c>, <c>Gateway:Web</c>), so one build
/// runs anywhere.
/// </summary>
public sealed class GatewayOptions
{
    /// <summary>
    /// The paths most ABP sites' servers answer: their API, OpenIddict's endpoints and discovery, and an MCP endpoint
    /// (AbpMcp's). <see cref="ServerPaths"/> starts with these.
    /// </summary>
    public static readonly IReadOnlyList<string> CommonServerPaths =
    [
        "/api/{**rest}",
        "/connect/{**rest}",
        "/.well-known/{**rest}",
        "/mcp",
        "/mcp/{**rest}",
    ];

    /// <summary>The server's paths, kept as they are (YARP route patterns). Every other path goes to the web.</summary>
    public IList<string> ServerPaths { get; } = [.. CommonServerPaths];

    /// <summary>Public paths that reach the server under another path, such as a webhook's address under the API.</summary>
    public IList<GatewayRewrite> Rewritten { get; } = [];

    /// <summary>
    /// The path a server answers 2xx on once it's ready for requests, checked every <c>Gateway:HealthCheckSeconds</c>
    /// (5): two failures and the server is left out until it answers again. Null: no readiness check, so only requests
    /// that couldn't be sent leave a server out (<see cref="FailedConnects"/>).
    /// </summary>
    public string? ReadinessPath { get; set; }

    /// <summary>The server when <c>Gateway:Server</c> isn't set.</summary>
    public string DefaultServer { get; set; } = "http://localhost:5100";

    /// <summary>The web when <c>Gateway:Web</c> isn't set.</summary>
    public string DefaultWeb { get; set; } = "http://localhost:3000";

    /// <summary>How long a request may send nothing, as server-sent events do between events, before the gateway gives up.</summary>
    public TimeSpan StreamIdle { get; set; } = TimeSpan.FromMinutes(30);
}

/// <summary>A public path (<paramref name="Path"/>) that reaches the server as <paramref name="ServerPath"/> (YARP's PathPattern).</summary>
public sealed record GatewayRewrite(string Path, string ServerPath);
