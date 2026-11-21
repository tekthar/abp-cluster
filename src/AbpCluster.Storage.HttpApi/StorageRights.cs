using Microsoft.Extensions.Options;

namespace AbpCluster.Storage.HttpApi;

/// <summary>
/// Who may see the storage, test it and start a copy: the site's admins, as the host decides. A host gives its own
/// (registered after the module's), reading the signed-in caller its own way: ABP's <c>ICurrentUser</c>, a permission,
/// AbpGuard's ability.
/// </summary>
public interface IStorageRights
{
    Task<bool> MayManageAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The default: whoever passes the host's <see cref="StorageHttpOptions.Policy"/>, which then decided. With no policy
/// set it refuses everyone, so a host that names neither a policy nor rights of its own hands the storage to no one.
/// </summary>
internal sealed class PolicyStorageRights(IOptions<StorageHttpOptions> options) : IStorageRights
{
    public Task<bool> MayManageAsync(CancellationToken cancellationToken) => Task.FromResult(options.Value.Policy is { Length: > 0 });
}
