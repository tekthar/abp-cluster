using Microsoft.Extensions.Options;
using Volo.Abp.DistributedLocking;

namespace AbpCluster;

/// <summary>
/// ABP's distributed lock as the cluster's. ABP's own default locks within one process, so with two servers each
/// module's outbox sender would read and publish the same events, and each server would run the same background jobs.
/// Taken at once, or within the timeout asked for (tried every 200 ms).
/// </summary>
public sealed class ClusterAbpDistributedLock(IClusterLocks locks, IOptions<AbpDistributedLockOptions> options) : IAbpDistributedLock
{
    private static readonly TimeSpan Retry = TimeSpan.FromMilliseconds(200);

    public async Task<IAbpDistributedLockHandle?> TryAcquireAsync(string name, TimeSpan timeout = default, CancellationToken cancellationToken = default)
    {
        string key = "abp:" + options.Value.KeyPrefix + name;
        DateTime until = DateTime.UtcNow + timeout;
        while (true)
        {
            if (await locks.TryTakeAsync(key, cancellationToken) is { } held)
            {
                return new Handle(held);
            }

            if (DateTime.UtcNow + Retry > until)
            {
                return null;
            }

            await Task.Delay(Retry, cancellationToken);
        }
    }

    private sealed class Handle(IAsyncDisposable held) : IAbpDistributedLockHandle
    {
        public ValueTask DisposeAsync() => held.DisposeAsync();
    }
}
