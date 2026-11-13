using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Volo.Abp.BlobStoring;
using Volo.Abp.DependencyInjection;

namespace AbpCluster.Storage;

/// <summary>A storage as an admin page shows it: its provider and where it is, without a secret.</summary>
public sealed record StorageLocationDto(string Provider, string Location);

/// <summary>The storage in use, its stores, and the storage being left while files are copied.</summary>
public sealed record StorageDto(StorageLocationDto Current, List<string> Stores, StorageLocationDto? Previous);

/// <summary>A store's test: whether a file could be written, read back the same, and deleted, and how long it took.</summary>
public sealed record StorageTestDto(string Store, bool Ok, int Milliseconds, string? Error);

/// <summary>A check of the stored files: how many the app's tables name, how many storage lacks, and the first few it lacks.</summary>
public sealed record StoredFilesCheckDto(int Total, int Missing, List<string> Named);

/// <summary>
/// The storage for an admin page or a start-up check: where it is, a test of each store, the copy from the storage being
/// left. The app keeps its own endpoints and who may call them.
/// </summary>
public sealed class StorageAdmin(StorageState state, IBlobContainerFactory containers, StorageCopier copier)
{
    public StorageDto Describe() => new(
        new StorageLocationDto(state.Current.Provider, state.Current.Describe()), [.. state.Options.Stores],
        state.Previous is { } previous ? new StorageLocationDto(previous.Provider, previous.Describe()) : null);

    /// <summary>Each store: write a file, read it back, delete it.</summary>
    public async Task<List<StorageTestDto>> TestAsync(CancellationToken cancellationToken)
    {
        List<StorageTestDto> results = [];
        foreach (string store in state.Options.Stores)
        {
            IBlobContainer container = containers.Create(store);
            string name = $"{state.Options.TestFolder}/{Guid.NewGuid():N}";
            byte[] written = Guid.NewGuid().ToByteArray();
            Stopwatch clock = Stopwatch.StartNew();
            try
            {
                await container.SaveAsync(name, new MemoryStream(written), overrideExisting: true, cancellationToken);
                byte[]? read = await container.GetAllBytesOrNullAsync(name, cancellationToken);
                await container.DeleteAsync(name, cancellationToken);
                results.Add(read is not null && read.AsSpan().SequenceEqual(written)
                    ? new StorageTestDto(store, true, (int)clock.ElapsedMilliseconds, null)
                    : new StorageTestDto(store, false, (int)clock.ElapsedMilliseconds, "The file read back isn't the one written."));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                results.Add(new StorageTestDto(store, false, (int)clock.ElapsedMilliseconds, Trim(ex)));
            }
        }

        return results;
    }

    public Task<StorageCopyDto> CopyStatusAsync(CancellationToken cancellationToken) => copier.StatusAsync(cancellationToken);

    public Task<StorageCopyDto> StartCopyAsync(CancellationToken cancellationToken) => copier.StartAsync(cancellationToken);

    /// <summary>The innermost reason, short: a storage's errors are long and wrapped.</summary>
    internal static string Trim(Exception ex)
    {
        Exception inner = ex;
        while (inner.InnerException is not null && inner.Message.Length < 20)
        {
            inner = inner.InnerException;
        }

        return inner.Message.Length > 300 ? inner.Message[..300] : inner.Message;
    }
}

/// <summary>
/// Whether storage has every file the app's tables name (<see cref="IStoredFiles"/>): after a site takes over (the bucket
/// replicates on its own, and rows can arrive before their files), after a restore, or when an admin asks. One run at a
/// time across the site's servers (a cluster lock). The app decides where the outcome goes.
/// </summary>
public sealed class StoredFilesCheck(IRootServiceProvider root, IClusterLocks locks, StorageState state)
{
    /// <summary>How many missing files the outcome names.</summary>
    public const int Named = 5;

    /// <summary>The check, or null when another server is running one.</summary>
    public async Task<StoredFilesCheckDto?> RunAsync(CancellationToken cancellationToken)
    {
        await using IAsyncDisposable? held = await locks.TryTakeAsync(state.Options.CheckLockName, cancellationToken);
        if (held is null)
        {
            return null;
        }

        int total = 0, missing = 0;
        List<string> named = [];
        await using AsyncServiceScope scope = root.GetRequiredService<IServiceScopeFactory>().CreateAsyncScope();
        IBlobContainerFactory containers = scope.ServiceProvider.GetRequiredService<IBlobContainerFactory>();
        foreach (IStoredFiles source in scope.ServiceProvider.GetServices<IStoredFiles>())
        {
            await foreach (StoredFile file in source.ListAsync(cancellationToken))
            {
                total++;
                if (await containers.Create(file.Store).ExistsAsync(file.Name, cancellationToken))
                {
                    continue;
                }

                missing++;
                if (named.Count < Named)
                {
                    named.Add($"{file.Store}/{file.Name}");
                }
            }
        }

        return new StoredFilesCheckDto(total, missing, named);
    }
}
