# AbpCluster

What an ABP site on .NET 10 and PostgreSQL needs to run on several servers: a lock all the servers share, and ABP's own
distributed lock on it.

> **Preview.** 0.x versions may change their API in any minor version (SemVer). Pin an exact version.

> **Not a Volosoft project.** [ABP Framework](https://abp.io) is made and owned by [Volosoft](https://volosoft.com).
> AbpCluster is an independent enhancement that works on ABP: this repository and its packages don't belong to
> Volosoft, and aren't affiliated with or endorsed by them.

## What it does

- **`IClusterLocks`**: a lock every server shares, so one server at a time does some work. `TryTakeAsync` gives the
  lock, or null when another server holds it now, for a worker's round that skips this time. `TakeAsync` waits its
  turn, for work that must happen: a server's start, or work a person asked for. Cancelling stops the wait.
- **PostgreSQL advisory locks**, on the name's 64-bit hash, each on a connection of its own. A connection goes back to
  the pool holding no lock: let go of, or every lock of its session undone when something failed after the lock may
  have come (a wait cancelled just as it was granted). A server that dies lets go of its locks with its connections.
  While a lock is held its session is named `lock <name>` in `pg_stat_activity`, so you can see which server holds what.
- **ABP's distributed lock on the cluster.** ABP's default `IAbpDistributedLock` locks within one process, so with two
  servers each module's outbox sender would publish the same events, and each server would run the same background
  jobs. `AbpClusterModule` replaces it with one on `IClusterLocks`.
- **Without a database** (a test host with no connection string) every lock is granted, with a warning in the log: one
  process is a cluster of one.

## Use it

```bash
dotnet add package AbpCluster
```

```csharp
[DependsOn(typeof(AbpClusterModule))]
public class MyHostModule : AbpModule;
```

The locks live in the database `ConnectionStrings:Default` names. Point them elsewhere, or keep two products on one
database apart, with `AbpClusterOptions`:

```csharp
Configure<AbpClusterOptions>(o =>
{
    o.ConnectionStringName = "Locks";   // or o.ConnectionString = "...";
    o.KeyPrefix = "my-product:";        // before every name
});
```

A worker's round, on one server at a time:

```csharp
await using IAsyncDisposable? held = await locks.TryTakeAsync("nightly-report", stoppingToken);
if (held is null)
{
    return; // another server is doing it
}
```

### Migrating and seeding at start, one server at a time

Servers that start together take turns: each migrates and seeds alone, then the next finds it done. Hold the lock
around everything a start writes, and let go before the server takes requests:

```csharp
WebApplication app = builder.Build();
await using (await app.Services.GetRequiredService<IClusterLocks>().TakeAsync("startup", CancellationToken.None))
{
    await MigrateAsync(app.Services);
    await app.InitializeApplicationAsync(); // the modules start and the data seeders run
}

await app.RunAsync();
```

Upgrade by stopping every server before the new version starts (Kubernetes' `Recreate`), so an older server never runs
beside a newer one's schema and seed.

Before the container is built, `ClusterLocks.TakeAsync(connectionString, key, ct)` and `TryTakeAsync` take a lock by
its key as it is (no prefix).

## What it doesn't do

- It doesn't notice when a holder's connection breaks while its work runs: the database lets the lock go, another
  server may take it, and the first goes on unaware. Keep the work under a lock short, or check it's still yours.
- It isn't fair: when several servers wait, PostgreSQL doesn't promise who goes next.
- PostgreSQL only.

## Build and test

```bash
dotnet build AbpCluster.slnx
dotnet test AbpCluster.slnx
```

The tests need PostgreSQL: `localhost:5432` (postgres/postgres), or the server `ABP_CLUSTER_TEST_POSTGRES` names. Any
database does; the locks make no tables.

## License

Apache-2.0. See `LICENSE` and `NOTICE`.
