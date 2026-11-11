using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Volo.Abp;
using Volo.Abp.DistributedLocking;
using Volo.Abp.Modularity;

namespace AbpCluster.Tests;

/// <summary>An ABP application with <see cref="AbpClusterModule"/>: ABP's own lock is the cluster's.</summary>
public sealed class AbpLockTests
{
    [DependsOn(typeof(AbpClusterModule))]
    public sealed class Host : AbpModule;

    private static async Task<IAbpApplicationWithInternalServiceProvider> StartAsync()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Default"] = ClusterLocksTests.Server })
            .Build();
        IAbpApplicationWithInternalServiceProvider app = await AbpApplicationFactory.CreateAsync<Host>(o => o.Services.ReplaceConfiguration(configuration));
        await app.InitializeAsync();
        return app;
    }

    [Fact]
    public async Task The_module_puts_abps_lock_on_the_cluster()
    {
        using IAbpApplicationWithInternalServiceProvider app = await StartAsync();

        Assert.IsType<ClusterAbpDistributedLock>(app.ServiceProvider.GetRequiredService<IAbpDistributedLock>());
        Assert.Same(app.ServiceProvider.GetRequiredService<ClusterLocks>(), app.ServiceProvider.GetRequiredService<IClusterLocks>());
    }

    [Fact]
    public async Task Abps_lock_is_one_server_at_a_time()
    {
        using IAbpApplicationWithInternalServiceProvider a = await StartAsync();
        using IAbpApplicationWithInternalServiceProvider b = await StartAsync();
        IAbpDistributedLock first = a.ServiceProvider.GetRequiredService<IAbpDistributedLock>();
        IAbpDistributedLock second = b.ServiceProvider.GetRequiredService<IAbpDistributedLock>();
        string name = $"job:{Guid.NewGuid():N}";

        IAbpDistributedLockHandle? held = await first.TryAcquireAsync(name);
        Assert.NotNull(held);
        Assert.Null(await second.TryAcquireAsync(name));

        // Asked with a timeout, the other waits for it.
        Task<IAbpDistributedLockHandle?> waiting = second.TryAcquireAsync(name, TimeSpan.FromSeconds(10));
        await Task.Delay(400);
        await held.DisposeAsync();
        await using IAbpDistributedLockHandle? next = await waiting;
        Assert.NotNull(next);
    }
}
