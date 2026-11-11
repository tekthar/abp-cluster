using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace AbpCluster.Tests;

/// <summary>
/// The locks on a real PostgreSQL, two "servers" being two <see cref="ClusterLocks"/> (each lock is a session of its
/// own, so two in one process exclude each other as two servers would). Needs Postgres: localhost:5432
/// (postgres/postgres), or the server ABP_CLUSTER_TEST_POSTGRES names. Every test locks names of its own.
/// </summary>
public sealed class ClusterLocksTests
{
    internal static readonly string Server =
        Environment.GetEnvironmentVariable("ABP_CLUSTER_TEST_POSTGRES") is { Length: > 0 } server ? server : "Host=localhost;Port=5432;Username=postgres;Password=postgres";

    private static readonly TimeSpan Soon = TimeSpan.FromSeconds(10);

    internal static ClusterLocks Locks(string? connectionString = null, string prefix = "", string? named = null) => new(
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Default"] = named ?? Server }).Build(),
        Options.Create(new AbpClusterOptions { ConnectionString = connectionString, KeyPrefix = prefix }),
        NullLogger<ClusterLocks>.Instance);

    private static string Unique(string what) => $"test:{what}:{Guid.NewGuid():N}";

    [Fact]
    public async Task One_server_at_a_time()
    {
        string name = Unique("one");
        ClusterLocks a = Locks(), b = Locks();

        IAsyncDisposable held = await a.TakeAsync(name, default);
        Assert.Null(await b.TryTakeAsync(name, default));

        await held.DisposeAsync();
        await using IAsyncDisposable? next = await b.TryTakeAsync(name, default);
        Assert.NotNull(next);
    }

    [Fact]
    public async Task Taking_waits_its_turn()
    {
        string name = Unique("turn");
        ClusterLocks a = Locks(), b = Locks();

        IAsyncDisposable held = await a.TakeAsync(name, default);
        Task<IAsyncDisposable> waiting = b.TakeAsync(name, default);
        await Task.Delay(500);
        Assert.False(waiting.IsCompleted);

        await held.DisposeAsync();
        await using IAsyncDisposable next = await waiting.WaitAsync(Soon);
    }

    [Fact]
    public async Task Cancelling_stops_the_wait_and_leaves_nothing_held()
    {
        string name = Unique("cancel");
        ClusterLocks a = Locks(), b = Locks();

        IAsyncDisposable held = await a.TakeAsync(name, default);
        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => b.TakeAsync(name, stop.Token).WaitAsync(Soon));

        await held.DisposeAsync();
        await using IAsyncDisposable? next = await Locks().TryTakeAsync(name, default);
        Assert.NotNull(next);
    }

    [Fact]
    public async Task Other_names_and_prefixes_never_meet()
    {
        string name = Unique("names");
        await using IAsyncDisposable held = await Locks().TakeAsync(name, default);

        await using IAsyncDisposable? other = await Locks().TryTakeAsync(name + ":other", default);
        Assert.NotNull(other);
        await using IAsyncDisposable? prefixed = await Locks(prefix: "another-product:").TryTakeAsync(name, default);
        Assert.NotNull(prefixed);
    }

    [Fact]
    public async Task A_server_that_dies_lets_go()
    {
        string name = Unique("dies");
        IAsyncDisposable held = await Locks().TakeAsync(name, default);

        // The holder's session, by the name it has in pg_stat_activity, ends as a crashed server's would.
        await using (var admin = new NpgsqlConnection(Server))
        {
            await admin.OpenAsync();
            await using var kill = new NpgsqlCommand("SELECT count(pg_terminate_backend(pid)) FROM pg_stat_activity WHERE application_name = @name", admin);
            kill.Parameters.AddWithValue("name", ("lock " + name)[..Math.Min(63, 5 + name.Length)]);
            Assert.Equal(1L, await kill.ExecuteScalarAsync());
        }

        await using IAsyncDisposable? next = await Locks().TakeAsync(name, default).WaitAsync(Soon);
        Assert.NotNull(next);
        // Its work ends normally: letting go of a lock its broken connection no longer holds doesn't throw.
        await held.DisposeAsync();
    }

    [Fact]
    public async Task A_held_lock_names_its_session_and_letting_go_names_it_back()
    {
        string name = Unique("session");
        IAsyncDisposable held = await Locks().TakeAsync(name, default);
        Assert.Equal(1L, await SessionsNamedAsync("lock " + name));

        await held.DisposeAsync();
        // The connection is back in the pool, idle, under its own name: nothing claims a lock it no longer has.
        Assert.Equal(0L, await SessionsNamedAsync("lock " + name));
        await using IAsyncDisposable? again = await Locks().TryTakeAsync(name, default);
        Assert.NotNull(again);
    }

    private static async Task<long> SessionsNamedAsync(string session)
    {
        await using var admin = new NpgsqlConnection(Server);
        await admin.OpenAsync();
        await using var count = new NpgsqlCommand("SELECT count(*) FROM pg_stat_activity WHERE application_name = @session", admin);
        count.Parameters.AddWithValue("session", session);
        return (long)(await count.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task Before_the_container_by_connection_string()
    {
        string key = Unique("static");
        await using IAsyncDisposable held = await ClusterLocks.TakeAsync(Server, key, default);

        Assert.Null(await ClusterLocks.TryTakeAsync(Server, key, default));
        Assert.Null(await Locks().TryTakeAsync(key, default)); // the same lock: no prefix
    }

    [Fact]
    public async Task Options_name_the_database()
    {
        string name = Unique("options");
        // The connection string set in the options wins over the configuration's, which here goes nowhere.
        ClusterLocks a = Locks(connectionString: Server, named: "Host=nowhere.invalid");
        await using IAsyncDisposable held = await a.TakeAsync(name, default);
        Assert.Null(await Locks().TryTakeAsync(name, default));
    }

    [Fact]
    public async Task Without_a_database_every_lock_is_granted()
    {
        ClusterLocks alone = Locks(named: "");

        await using IAsyncDisposable first = await alone.TakeAsync("x", default);
        await using IAsyncDisposable second = await alone.TakeAsync("x", default);
        Assert.NotNull(await alone.TryTakeAsync("x", default));
    }
}
