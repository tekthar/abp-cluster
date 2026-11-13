using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Volo.Abp;
using Volo.Abp.BlobStoring;
using Volo.Abp.DependencyInjection;

namespace AbpCluster.Storage;

/// <summary>
/// A file the app keeps in a store: its name there, and the SHA-256 recorded for it (hex), when one was. A replaceable
/// one may be saved again under its name (a backup keeps each version by its hash).
/// </summary>
public sealed record StoredFile(string Store, string Name, string? Sha256, bool Replaceable = false);

/// <summary>
/// The files a part of the app keeps in storage, listed from its own tables: what the copy takes across when storage
/// changes, what a check after a site takes over looks for, and what a backup can keep. Register one per part
/// (<c>services.AddTransient&lt;IStoredFiles, AttachmentFiles&gt;()</c>).
/// </summary>
public interface IStoredFiles
{
    IAsyncEnumerable<StoredFile> ListAsync(CancellationToken cancellationToken);
}

/// <summary>What happens to the storage that the app may want to keep a record of: a copy that ended.</summary>
public interface IStorageEvents
{
    /// <summary>A copy ended (done or failed), from the storage left to the one in use.</summary>
    Task CopiedAsync(StorageCopyDto done, StorageLocation from, StorageLocation to, CancellationToken cancellationToken);
}

/// <summary>The default record of storage events: the log.</summary>
public sealed class LoggedStorageEvents(ILogger<LoggedStorageEvents> logger) : IStorageEvents
{
    public Task CopiedAsync(StorageCopyDto done, StorageLocation from, StorageLocation to, CancellationToken cancellationToken)
    {
        logger.LogInformation("The copy from {From} to {To} ended {State}: {Copied} copied, {Skipped} already there, {Missing} in neither, {Failed} failed of {Total}.",
            from.Describe(), to.Describe(), done.State, done.Copied, done.Skipped, done.Missing, done.Failed, done.Total);
        return Task.CompletedTask;
    }
}

/// <summary>A file the copy couldn't take across, and why.</summary>
public sealed record StorageCopyProblemDto(string Store, string Name, string Problem);

/// <summary>
/// The copy from the previous storage, as it stands: <c>idle</c>, <c>running</c>, <c>done</c> or <c>failed</c>; how many
/// files it has looked at, copied, found already there, found in neither storage, or couldn't copy; and the first
/// problems.
/// </summary>
public sealed record StorageCopyDto(string State, DateTime? StartedAt, DateTime? FinishedAt, int Total, int Copied, int Skipped, int Missing, int Failed,
    List<StorageCopyProblemDto> Problems);

/// <summary>
/// Copies every stored file (<see cref="IStoredFiles"/>) from the storage being left (<c>{Section}:Previous</c>) to the
/// one in use, on the server it's started on, one run at a time for the whole site (a cluster lock held while it runs).
/// Each file is checked against the hash recorded for it before it's sent (a damaged file isn't copied) and after (what
/// arrived is what was sent). A file already in the storage in use, and matching, is left as it is, so the copy can be
/// run again until nothing's left. How it stands is kept in <c>{schema}.storage_copy</c>, so every server shows the same
/// copy; a copy whose server stopped shows as failed.
/// </summary>
public sealed class StorageCopier(IRootServiceProvider root, StorageState state, IConfiguration configuration, IClusterLocks locks, ILogger<StorageCopier> logger)
{
    private const int MaxProblems = 100;
    private static readonly TimeSpan SaveEvery = TimeSpan.FromSeconds(1);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ClusterStorageOptions _options = state.Options;
    private readonly string? _connectionString = state.Options.ConnectionString is { Length: > 0 } given ? given
        : configuration.GetConnectionString(state.Options.ConnectionStringName) is { Length: > 0 } cs ? cs : null;

    private readonly Lock _lock = new();
    private StorageCopyDto _status = new("idle", null, null, 0, 0, 0, 0, 0, []);
    private DateTime _savedAt;
    private bool _tableMade;

    // The run outlives the request that starts it, so its scopes come from the root, never the request's.
    private IServiceScopeFactory Scopes => root.GetRequiredService<IServiceScopeFactory>();

    /// <summary>The table the copy keeps how it stands in, one row for the site.</summary>
    public static string CreateTableSql(string quotedSchema) => $"""
        CREATE SCHEMA IF NOT EXISTS {quotedSchema};
        CREATE TABLE IF NOT EXISTS {quotedSchema}.storage_copy (
            "Id" smallint PRIMARY KEY DEFAULT 1 CHECK ("Id" = 1), "Status" jsonb NOT NULL, "UpdatedAt" timestamptz NOT NULL DEFAULT now());
        """;

    private StorageCopyDto Local
    {
        get
        {
            lock (_lock)
            {
                return _status with { Problems = [.. _status.Problems] };
            }
        }
    }

    /// <summary>
    /// The copy as every server sees it: this server's own while it copies, else the site's row (without a database,
    /// this server's own). A row left running by a server that stopped reads as failed: its lock went with it.
    /// </summary>
    public async Task<StorageCopyDto> StatusAsync(CancellationToken cancellationToken)
    {
        StorageCopyDto local = Local;
        if (_connectionString is null || local.State == "running")
        {
            return local;
        }

        StorageCopyDto? saved = await ReadAsync(cancellationToken);
        if (saved is not { State: "running" })
        {
            return saved ?? local;
        }

        await using IAsyncDisposable? held = await locks.TryTakeAsync(_options.CopyLockName, cancellationToken);
        if (held is null)
        {
            return saved;
        }

        StorageCopyDto stopped = saved with
        {
            State = "failed", FinishedAt = DateTime.UtcNow,
            Problems = [.. saved.Problems, new StorageCopyProblemDto("", "", "The server copying stopped before it finished. Start the copy again: what's already copied is skipped.")],
        };
        await SaveAsync(stopped, cancellationToken);
        return stopped;
    }

    /// <summary>Starts a copy unless one is running on any server; refused when there's no previous storage to copy from.</summary>
    public async Task<StorageCopyDto> StartAsync(CancellationToken cancellationToken)
    {
        if (state.Previous is null)
        {
            throw new UserFriendlyException($"There's no previous storage to copy from: {_options.PreviousSection} names the storage being left.");
        }

        IAsyncDisposable? held = await locks.TryTakeAsync(_options.CopyLockName, cancellationToken);
        if (held is null)
        {
            return await StatusAsync(cancellationToken);
        }

        lock (_lock)
        {
            // Without a database every lock is granted: this server's own state keeps it to one run.
            if (_status.State == "running")
            {
                _ = held.DisposeAsync().AsTask();
                return _status with { Problems = [.. _status.Problems] };
            }

            _status = new StorageCopyDto("running", DateTime.UtcNow, null, 0, 0, 0, 0, 0, []);
        }

        await SaveAsync(Local, cancellationToken);
        // Started without the request's ambient state (its unit of work, who's acting): the run outlives the request.
        using (ExecutionContext.SuppressFlow())
        {
            _ = Task.Run(() => RunAsync(held));
        }

        return Local;
    }

    /// <summary>Waits for this server's copy to end (for tests and tools); returns at once when none runs here.</summary>
    internal async Task<StorageCopyDto> WaitAsync(TimeSpan timeout)
    {
        DateTime until = DateTime.UtcNow + timeout;
        while (Local.State == "running" && DateTime.UtcNow < until)
        {
            await Task.Delay(20);
        }

        // The row is saved after the state changes: give it a moment.
        await Task.Delay(50);
        return Local;
    }

    private void Update(Func<StorageCopyDto, StorageCopyDto> change)
    {
        lock (_lock)
        {
            _status = change(_status);
        }
    }

    private async Task<StorageCopyDto?> ReadAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using NpgsqlConnection connection = new(_connectionString);
            await connection.OpenAsync(cancellationToken);
            await using NpgsqlCommand read = new($"""SELECT "Status"::text FROM {_options.QuotedSchema}.storage_copy WHERE "Id" = 1""", connection);
            return await read.ExecuteScalarAsync(cancellationToken) is string text ? JsonSerializer.Deserialize<StorageCopyDto>(text, Json) : null;
        }
        catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.UndefinedTable or PostgresErrorCodes.InvalidSchemaName)
        {
            // No copy has run against this database yet.
            return null;
        }
    }

    /// <summary>Keeps how the copy stands for the other servers; a failure to keep it never stops the copy.</summary>
    private async Task SaveAsync(StorageCopyDto status, CancellationToken cancellationToken)
    {
        if (_connectionString is null)
        {
            return;
        }

        try
        {
            await using NpgsqlConnection connection = new(_connectionString);
            await connection.OpenAsync(cancellationToken);
            if (!_tableMade)
            {
                await using NpgsqlCommand make = new(CreateTableSql(_options.QuotedSchema), connection);
                await make.ExecuteNonQueryAsync(cancellationToken);
                _tableMade = true;
            }

            await using NpgsqlCommand save = new($"""
                INSERT INTO {_options.QuotedSchema}.storage_copy ("Id", "Status", "UpdatedAt") VALUES (1, $1::jsonb, now())
                ON CONFLICT ("Id") DO UPDATE SET "Status" = EXCLUDED."Status", "UpdatedAt" = now()
                """, connection)
            {
                Parameters = { new NpgsqlParameter { Value = JsonSerializer.Serialize(status, Json) } },
            };
            await save.ExecuteNonQueryAsync(cancellationToken);
            _savedAt = DateTime.UtcNow;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "How the storage copy stands couldn't be kept for the other servers");
        }
    }

    private void Problem(StoredFile file, string problem) => Update(s => s with
    {
        Problems = s.Problems.Count < MaxProblems ? [.. s.Problems, new StorageCopyProblemDto(file.Store, file.Name, problem)] : s.Problems,
    });

    private async Task RunAsync(IAsyncDisposable held)
    {
        await using (held)
        {
            try
            {
                await using AsyncServiceScope scope = Scopes.CreateAsyncScope();
                IBlobContainerFactory containers = scope.ServiceProvider.GetRequiredService<IBlobContainerFactory>();
                foreach (IStoredFiles source in scope.ServiceProvider.GetServices<IStoredFiles>())
                {
                    await foreach (StoredFile file in source.ListAsync(CancellationToken.None))
                    {
                        Update(s => s with { Total = s.Total + 1 });
                        string outcome = await CopyAsync(containers, file);
                        Update(s => outcome switch
                        {
                            "copied" => s with { Copied = s.Copied + 1 },
                            "skipped" => s with { Skipped = s.Skipped + 1 },
                            "missing" => s with { Missing = s.Missing + 1 },
                            _ => s with { Failed = s.Failed + 1 },
                        });
                        if (DateTime.UtcNow - _savedAt >= SaveEvery)
                        {
                            await SaveAsync(Local, CancellationToken.None);
                        }
                    }
                }

                Update(s => s with { State = "done", FinishedAt = DateTime.UtcNow });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Copying files from the previous storage failed");
                Update(s => s with { State = "failed", FinishedAt = DateTime.UtcNow, Problems = [.. s.Problems, new StorageCopyProblemDto("", "", ex.Message)] });
            }

            // Kept while the lock is still held, so no other server reads this run as stopped.
            await SaveAsync(Local, CancellationToken.None);
        }

        try
        {
            await using AsyncServiceScope scope = Scopes.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<IStorageEvents>().CopiedAsync(Local, state.Previous!, state.Current, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "The end of the storage copy couldn't be recorded");
        }
    }

    /// <summary>One file across: <c>copied</c>, <c>skipped</c> (already there and matching), <c>missing</c> (in neither), or <c>failed</c>.</summary>
    private async Task<string> CopyAsync(IBlobContainerFactory containers, StoredFile file)
    {
        IBlobContainer current = containers.Create(file.Store);
        IBlobContainer previous = containers.Create(file.Store + StorageState.PreviousSuffix);
        try
        {
            string? there = await HashAsync(await current.GetOrNullAsync(file.Name));
            if (there is not null && (file.Sha256 is null || Same(there, file.Sha256)))
            {
                return "skipped";
            }

            await using Stream? source = await previous.GetOrNullAsync(file.Name);
            if (source is null)
            {
                Problem(file, there is null ? "It's in neither storage." : "It's in the storage in use, but doesn't match its hash, and the previous storage hasn't got it.");
                return there is null ? "missing" : "failed";
            }

            await using FileStream buffer = StorageTemp.Create(_options, "copy");
            await source.CopyToAsync(buffer);
            buffer.Position = 0;
            string sent = Convert.ToHexString(await SHA256.HashDataAsync(buffer));
            if (file.Sha256 is not null && !Same(sent, file.Sha256))
            {
                Problem(file, "The previous storage's copy doesn't match the hash recorded for it, so it wasn't copied.");
                return "failed";
            }

            buffer.Position = 0;
            await current.SaveAsync(file.Name, buffer, overrideExisting: true);
            if (await HashAsync(await current.GetOrNullAsync(file.Name)) is not { } arrived || !Same(arrived, sent))
            {
                Problem(file, "What arrived isn't what was sent.");
                return "failed";
            }

            return "copied";
        }
        catch (Exception ex)
        {
            Problem(file, ex.Message.Length > 300 ? ex.Message[..300] : ex.Message);
            return "failed";
        }
    }

    private static async Task<string?> HashAsync(Stream? stream)
    {
        if (stream is null)
        {
            return null;
        }

        await using (stream)
        {
            return Convert.ToHexString(await SHA256.HashDataAsync(stream));
        }
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
