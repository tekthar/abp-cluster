using Microsoft.AspNetCore.Mvc;
using Volo.Abp;
using Volo.Abp.AspNetCore.Mvc;
using Volo.Abp.Uow;

namespace AbpCluster.Storage.HttpApi;

/// <summary>
/// The storage for an admin page, read only: where it is (the provider and its place, never a key), its stores, the
/// storage being left while files are copied, a test of each store, and the copy. Storage is set in configuration, so
/// nothing here changes it. No transaction is held open around a request: a test writes to the storage, and the copy
/// runs on after its request.
/// </summary>
[Route("")]
[UnitOfWork(isTransactional: false)]
public class StorageController(StorageAdmin storage, IStorageRights rights) : AbpControllerBase
{
    [HttpGet]
    public async Task<StorageDto> Get(CancellationToken cancellationToken)
    {
        await EnsureAsync(cancellationToken);
        return storage.Describe();
    }

    /// <summary>Writes a file to each store, reads it back and deletes it: whether it worked, how long it took, and the storage's own reason when not.</summary>
    [HttpPost("test")]
    public async Task<List<StorageTestDto>> Test(CancellationToken cancellationToken)
    {
        await EnsureAsync(cancellationToken);
        return await storage.TestAsync(cancellationToken);
    }

    /// <summary>The copy from the storage being left, as every server sees it: idle, running, or how the last one went.</summary>
    [HttpGet("copy")]
    public async Task<StorageCopyDto> Copy(CancellationToken cancellationToken)
    {
        await EnsureAsync(cancellationToken);
        return await storage.CopyStatusAsync(cancellationToken);
    }

    /// <summary>
    /// Starts copying every stored file from the storage being left, each checked by its hash, on this server; how it
    /// stands, or the one already running on any server. 409 when no storage is being left.
    /// </summary>
    [HttpPost("copy"), StorageSensitive]
    public async Task<StorageCopyDto> StartCopy(CancellationToken cancellationToken)
    {
        await EnsureAsync(cancellationToken);
        return await storage.StartCopyAsync(cancellationToken);
    }

    private async Task EnsureAsync(CancellationToken cancellationToken)
    {
        if (!await rights.MayManageAsync(cancellationToken))
        {
            throw new UserFriendlyException("Only the site's admins see and test its storage.", StorageErrorCodes.Forbidden);
        }
    }
}
