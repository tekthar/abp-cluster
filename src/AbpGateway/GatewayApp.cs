using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Yarp.ReverseProxy.Health;
using Yarp.ReverseProxy.LoadBalancing;
using Yarp.ReverseProxy.ServiceDiscovery;

namespace AbpGateway;

/// <summary>
/// A site's front door: one origin, its servers and its web behind it. It only routes: authentication, limits and
/// everything else stay in the servers. <c>Gateway:Server</c> and <c>Gateway:Web</c> say where they are. With several
/// servers, <c>Gateway:Server</c> names each (<c>,</c> between), or a name every server answers to with
/// <c>Gateway:ResolveServers</c> (a Kubernetes headless Service), resolved every 5 seconds.
/// <code>
/// WebApplication app = GatewayApp.Build(args, new GatewayOptions { ReadinessPath = "/api/ready" });
/// app.Run();
/// </code>
/// </summary>
public static class GatewayApp
{
    /// <summary>A gateway with the common server paths and no readiness check; <paramref name="configure"/> sees the builder first.</summary>
    public static WebApplication Build(string[] args, Action<WebApplicationBuilder>? configure = null) => Build(args, null, configure);

    /// <summary>A gateway for these <paramref name="options"/>; <paramref name="configure"/> sees the builder first (a test's URLs, more services).</summary>
    public static WebApplication Build(string[] args, GatewayOptions? options, Action<WebApplicationBuilder>? configure = null)
    {
        options ??= new GatewayOptions();
        WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
        configure?.Invoke(builder);
        string[] servers = (builder.Configuration["Gateway:Server"] ?? options.DefaultServer)
            .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string web = builder.Configuration["Gateway:Web"] ?? options.DefaultWeb;
        TimeSpan healthEvery = TimeSpan.FromSeconds(Math.Max(1, builder.Configuration.GetValue("Gateway:HealthCheckSeconds", 5)));

        // Uploads are limited by the servers, which know what each is for, not here.
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Limits.MaxRequestBodySize = null);
        IReverseProxyBuilder proxy = builder.Services.AddReverseProxy()
            .LoadFromMemory(GatewayRoutes.Routes(options), GatewayRoutes.Clusters(servers, web, healthEvery, options))
            .AddTransforms(McpAffinity.Transforms);
        builder.Services.AddSingleton<ILoadBalancingPolicy, McpAffinity>();
        builder.Services.AddSingleton<IPassiveHealthCheckPolicy, FailedConnects>();
        if (builder.Configuration.GetValue("Gateway:ResolveServers", false))
        {
            // YARP's DNS resolver, kept patient while the name has no address yet (ServerAddresses).
            proxy.AddDnsDestinationResolver(dns => dns.RefreshPeriod = TimeSpan.FromSeconds(5));
            ServiceDescriptor resolver = builder.Services.Last(d => d.ServiceType == typeof(IDestinationResolver));
            builder.Services.RemoveAll<IDestinationResolver>();
            builder.Services.AddSingleton<IDestinationResolver>(services => new ServerAddresses(
                (IDestinationResolver)(resolver.ImplementationFactory?.Invoke(services) ?? ActivatorUtilities.CreateInstance(services, resolver.ImplementationType!)),
                services.GetRequiredService<ILogger<ServerAddresses>>()));
        }

        WebApplication app = builder.Build();
        // The visitor's address from a trusted ingress's X-Forwarded-For (ForwardedFor), before YARP passes it on.
        app.UseForwardedHeaders(ForwardedFor.Options(builder.Configuration));
        // HSTS (over HTTPS, as the forwarded headers say), nosniff, a referrer policy and the content security policy.
        app.UseSecurityHeaders(builder.Configuration);
        app.MapGet("/_gateway/health", () => Results.Ok(new { status = "ok" }));
        app.MapReverseProxy();
        return app;
    }
}
