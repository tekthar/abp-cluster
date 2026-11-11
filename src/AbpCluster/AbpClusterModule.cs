using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Volo.Abp.DistributedLocking;
using Volo.Abp.Modularity;

namespace AbpCluster;

/// <summary>
/// The cluster's locks (<see cref="IClusterLocks"/>), and ABP's distributed lock on them
/// (<see cref="ClusterAbpDistributedLock"/>) in place of ABP's default, which locks within one process.
/// </summary>
[DependsOn(typeof(AbpDistributedLockingAbstractionsModule))]
public sealed class AbpClusterModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context) =>
        context.Services.Replace(ServiceDescriptor.Singleton<IAbpDistributedLock, ClusterAbpDistributedLock>());
}
