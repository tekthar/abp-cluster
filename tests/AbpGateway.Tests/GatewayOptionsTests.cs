using AbpGateway;
using Yarp.ReverseProxy.Configuration;

namespace AbpGateway.Tests;

/// <summary>
/// What a product says in <see cref="GatewayOptions"/> reaches YARP's routes and clusters: without options only the common
/// server paths go to the servers, with no readiness check; a product's paths, rewritten paths, readiness path and idle
/// time are its own.
/// </summary>
public class GatewayOptionsTests
{
    [Fact]
    public void Without_options_the_common_paths_go_to_the_servers_and_every_other_to_the_web()
    {
        GatewayOptions options = new();
        IReadOnlyList<RouteConfig> routes = GatewayRoutes.Routes(options);

        Assert.Equal(GatewayOptions.CommonServerPaths, routes.Where(r => r.ClusterId == GatewayRoutes.ServerCluster).Select(r => r.Match.Path));
        RouteConfig web = Assert.Single(routes, r => r.ClusterId == GatewayRoutes.WebCluster);
        Assert.Equal(("/{**rest}", 2), (web.Match.Path, web.Order!.Value));
        Assert.All(routes.Where(r => r.ClusterId == GatewayRoutes.ServerCluster), r => Assert.Equal(1, r.Order));
    }

    [Fact]
    public void Without_a_readiness_path_only_failed_requests_leave_a_server_out()
    {
        ClusterConfig servers = GatewayRoutes.Clusters(["http://a:8080", "http://b:8080"], "http://web:3000", TimeSpan.FromSeconds(5), new GatewayOptions())
            .Single(c => c.ClusterId == GatewayRoutes.ServerCluster);

        Assert.Null(servers.HealthCheck!.Active);
        Assert.Equal(FailedConnects.PolicyName, servers.HealthCheck.Passive!.Policy);
        Assert.Equal(["main", "main-1"], servers.Destinations!.Keys.Order());
    }

    [Fact]
    public void A_products_paths_readiness_and_idle_time_are_its_own()
    {
        GatewayOptions options = GatewayTests.ProductOptions();
        options.StreamIdle = TimeSpan.FromMinutes(5);
        IReadOnlyList<RouteConfig> routes = GatewayRoutes.Routes(options);
        IReadOnlyList<ClusterConfig> clusters = GatewayRoutes.Clusters(["http://a:8080"], "http://web:3000", TimeSpan.FromSeconds(7), options);

        Assert.Contains(routes, r => r.Match.Path == "/plugins-web/{**rest}" && r.ClusterId == GatewayRoutes.ServerCluster);
        RouteConfig hooks = Assert.Single(routes, r => r.Match.Path == "/hooks/git/{id}");
        Assert.Contains(hooks.Transforms!, t => t.TryGetValue("PathPattern", out string? to) && to == "/api/hooks/git/{id}");

        ActiveHealthCheckConfig ready = clusters.Single(c => c.ClusterId == GatewayRoutes.ServerCluster).HealthCheck!.Active!;
        Assert.Equal(("/api/platform/ready", TimeSpan.FromSeconds(7)), (ready.Path, ready.Interval!.Value));
        Assert.All(clusters, c => Assert.Equal(TimeSpan.FromMinutes(5), c.HttpRequest!.ActivityTimeout));
    }
}
