using System.Data;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Volo.Abp.DependencyInjection;

namespace AbpCluster;

/// <summary>
/// A lock every server of a site shares, so one server at a time does some work. A worker's round tries it and skips
/// this time when another server holds it; work that must happen (a server's start, work a person asked for) waits its
/// turn. A server that stops releases its locks with its connections.
/// </summary>
public interface IClusterLocks
{
    /// <summary>The lock, or null when another server holds it now (a worker's round skips this time).</summary>
    Task<IAsyncDisposable?> TryTakeAsync(string name, CancellationToken cancellationToken);

    /// <summary>The lock once no other server holds it; cancelling stops the wait.</summary>
    Task<IAsyncDisposable> TakeAsync(string name, CancellationToken cancellationToken);
}

/// <summary>Where the servers' locks live.</summary>
public sealed class AbpClusterOptions
{
    /// <summary>The database whose locks the servers share. Unset: the connection string named <see cref="ConnectionStringName"/>.</summary>
    public string? ConnectionString { get; set; }

    /// <summary>The connection string to read when <see cref="ConnectionString"/> is unset. "Default".</summary>
    public string ConnectionStringName { get; set; } = "Default";

    /// <summary>
    /// Put before every lock's name, for products that share one database and must never share a lock ("my-product:").
    /// Empty by default: locks are already the database's own.
    /// </summary>
    public string KeyPrefix { get; set; } = "";
}

/// <summary>
/// The default locks: a PostgreSQL advisory lock (<c>pg_advisory_lock</c> on the name's 64-bit hash), on a connection
/// of its own, held while the work runs. While it's held, the session is named <c>lock {name}</c> in
/// <c>pg_stat_activity</c>, so an admin can see which server holds what. A connection goes back to the pool holding no
/// lock: let go of, or every lock of its session undone when something failed after the lock may have come. Without a
/// connection string (a test host) every lock is granted: one process is a cluster of one.
/// </summary>
public sealed class ClusterLocks : IClusterLocks, ISingletonDependency
{
    private readonly string? _connectionString;
    private readonly string _prefix;

    public ClusterLocks(IConfiguration configuration, IOptions<AbpClusterOptions> options, ILogger<ClusterLocks> logger)
    {
        AbpClusterOptions o = options.Value;
        _connectionString = o.ConnectionString is { Length: > 0 } given ? given : configuration.GetConnectionString(o.ConnectionStringName);
        _prefix = o.KeyPrefix;
        if (string.IsNullOrEmpty(_connectionString))
        {
            logger.LogWarning(
                "AbpCluster has no connection string (ConnectionStrings:{Name}), so every lock is granted: right for one process only.",
                o.ConnectionStringName);
        }
    }

    public Task<IAsyncDisposable?> TryTakeAsync(string name, CancellationToken cancellationToken) =>
        TryTakeAsync(_connectionString, _prefix + name, cancellationToken);

    public Task<IAsyncDisposable> TakeAsync(string name, CancellationToken cancellationToken) =>
        TakeAsync(_connectionString, _prefix + name, cancellationToken);

    /// <summary>The lock named <paramref name="key"/> as it is, or null when another server holds it now: for work before the container is built.</summary>
    public static async Task<IAsyncDisposable?> TryTakeAsync(string? connectionString, string key, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(connectionString))
        {
            return Granted.Instance;
        }

        var connection = new NpgsqlConnection(connectionString);
        string own = OwnName(connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            await using NpgsqlCommand take = Command(connection,
                "SELECT pg_try_advisory_lock(hashtextextended(@key, 0)) FROM (SELECT set_config('application_name', @session, false)) named", key);
            if (await take.ExecuteScalarAsync(cancellationToken) is true)
            {
                return new Held(connection, key, own);
            }

            await using NpgsqlCommand rename = Command(connection, "SELECT set_config('application_name', @session, false)", key, own);
            await rename.ExecuteNonQueryAsync(cancellationToken);
        }
        catch
        {
            await UndoAsync(connection);
            throw;
        }

        await connection.DisposeAsync();
        return null;
    }

    /// <summary>The lock named <paramref name="key"/> as it is, once no other server holds it: for work before the container is built.</summary>
    public static async Task<IAsyncDisposable> TakeAsync(string? connectionString, string key, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(connectionString))
        {
            return Granted.Instance;
        }

        var connection = new NpgsqlConnection(connectionString);
        string own = OwnName(connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            // No command timeout: the wait lasts as long as the other server's work, and the token ends it.
            await using NpgsqlCommand take = Command(connection,
                "SELECT set_config('application_name', @session, false), pg_advisory_lock(hashtextextended(@key, 0))", key);
            take.CommandTimeout = 0;
            await take.ExecuteNonQueryAsync(cancellationToken);
            return new Held(connection, key, own);
        }
        catch
        {
            // Had the lock come just as the wait was cancelled, it mustn't go back to the pool with the connection.
            await UndoAsync(connection);
            throw;
        }
    }

    /// <summary>The session's name from the connection string, which it goes back to when the lock is let go.</summary>
    private static string OwnName(string connectionString) => new NpgsqlConnectionStringBuilder(connectionString).ApplicationName ?? "";

    private static NpgsqlCommand Command(NpgsqlConnection connection, string sql, string key, string? session = null)
    {
        var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("key", key);
        string named = $"lock {key}";
        command.Parameters.AddWithValue("session", session ?? (named.Length <= 63 ? named : named[..63]));
        return command;
    }

    private static async Task UndoAsync(NpgsqlConnection connection)
    {
        try
        {
            if (connection.State == ConnectionState.Open)
            {
                await using var undo = new NpgsqlCommand("SELECT pg_advisory_unlock_all()", connection);
                await undo.ExecuteNonQueryAsync();
            }
        }
        catch (NpgsqlException)
        {
            // The connection broke: its session ended, and with it any lock it had.
        }
        finally
        {
            await connection.DisposeAsync();
        }
    }

    private sealed class Held(NpgsqlConnection connection, string key, string own) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try
            {
                if (connection.State == ConnectionState.Open)
                {
                    // Let go, and the session's own name back, before the connection goes back to the pool.
                    await using NpgsqlCommand release = Command(connection,
                        "SELECT pg_advisory_unlock(hashtextextended(@key, 0)), set_config('application_name', @session, false)",
                        key, own);
                    await release.ExecuteNonQueryAsync();
                }
            }
            catch (NpgsqlException)
            {
                // The connection broke while the work ran: its session ended, and the database let the lock go then.
            }
            finally
            {
                await connection.DisposeAsync();
            }
        }
    }

    private sealed class Granted : IAsyncDisposable
    {
        public static readonly Granted Instance = new();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
