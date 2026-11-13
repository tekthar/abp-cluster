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

## Two sites (AbpCluster.Sites)

For an organization that runs at a headquarters and a disaster-recovery site, both up, so that when one is lost the
other serves within a minute of someone deciding. PostgreSQL's own streaming replication carries the data, and the
storage's own replication the files; the package watches both and never copies them.

- **One active site, one standby that runs.** A server names its site (`Sites:Name`, say `hq` or `dr`). Before it
  touches its database it decides what to run (`SiteStart`): the app while its site is active, else the site host,
  which is safe on a read-only replica: its health, readiness and state, and nothing that writes or calls out.
- **The active site is a row with an epoch** that grows at each change, and the database timeline it became active on.
  A standby whose database turns writable on a newer timeline was promoted: its servers write the new row and take
  over by themselves. A writable database on the same timeline naming another site is that site's, and nothing writes
  to it.
- **Your numbers move past the lost seconds.** `SitesOptions.OnTakeover` runs in the takeover's transaction with a key
  gap (1,000 by default): move record keys, invoice numbers or anything else you number, so ones the lost site gave out
  in the seconds the replica never saw aren't given again.
- **Fencing.** The running app's guard reads every other site's state every few seconds; a site signed with the
  installation's key as active at a higher epoch makes this one stand down, write that down, and serve no one.
- **Promotion is a person's decision**, with a break-glass key the operators keep (its SHA-256 in `Sites:BreakGlassKeyHash`,
  as no one can sign in to a read-only site), or the database's own tooling, which the site then follows.
- **A planned switchover loses nothing.** The active site hands over: its apps stop, its last write waits until none of
  them holds the site's lock, and the standby promotes its own database and takes over with no key gap.
- **Readiness tells the truth**: a server's readiness is 200 when it can serve (as the app or the site host); the site's
  state is 200 only on the active site, for a global balancer or DNS failover to follow.
- **The active site watches its standbys** and tells its admins (`ISiteAlerts`) once a spell when one lags or can't be
  read. `SitesAdmin` lists the sites with their states, adds, changes and removes them, and starts a switchover; your
  controller checks who may.

Use it: run your app through `SiteRunner.RunAsync`, add `AbpClusterSitesModule` and `services.AddClusterSites(options)`,
call `SiteGuard.CheckAtStartAsync` after your migrations, and map `MapClusterSites()`. The guide is `docs/sites.md`, and
the decisions are in `docs/adr/0002-two-sites.md`.

## Storage (AbpCluster.Storage)

Where the app keeps its files, chosen by the operator in configuration, and moved while the site runs. It sits on ABP's
blob storing: your code keeps using `IBlobContainer<T>`.

- **File system, S3 or Azure**, from the `Storage` section. S3 is AWS by region or another server by address (MinIO,
  Ceph, path-style); with no keys it signs in through AWS's own chain (an IAM role). Azure takes a connection string, or
  the account's https address to sign in as the server (workload identity, a managed identity). Problems name your keys.
- **Your stores keep their places**: a folder each in the file system (`{path}/host/{store}/{name}`, ABP's own layout),
  or a prefix each in one bucket. Other containers go to the same storage.
- **Moving to another storage**: name the old one under `Storage:Previous`. Saves go to the new one, a file not moved yet
  is read from the old one, deletes happen in both, and `StorageCopier` copies every file across (one run for the whole
  site, each file checked against its recorded hash before and after, what's there skipped, its state kept in
  PostgreSQL for every server).
- **The files your tables name**: register an `IStoredFiles` per part of your app. The copy walks them, and so does
  `StoredFilesCheck`, which counts the files storage lacks: after a standby site takes over, or a restore.
- **`StorageAdmin`** for your admin page or start-up check: where the storage is, a test of each store, the copy.
- **`FileKinds`** knows an upload by its bytes (PNG, JPEG, WebP, GIF, PDF, ZIP), and `ServeSafely` sets the headers that
  keep a served file from running as a page.

```csharp
[DependsOn(typeof(AbpClusterStorageModule))]
public class MyModule : AbpModule
{
    public override void PreConfigureServices(ServiceConfigurationContext context) =>
        PreConfigure<ClusterStorageOptions>(o =>
        {
            o.Stores.Add("attachments");                 // [BlobContainerName("attachments")]
            o.FileSystemPath = "/var/lib/myapp/files";   // when Storage:FileSystem:Path isn't set
        });

    public override void ConfigureServices(ServiceConfigurationContext context) =>
        context.Services.AddTransient<IStoredFiles, AttachmentFiles>();
}
```

```json
{ "Storage": { "Provider": "S3", "S3": { "Region": "eu-north-1", "Bucket": "myapp-files" },
               "Previous": { "Provider": "FileSystem", "FileSystem": { "Path": "/var/lib/myapp/files" } } } }
```

The decisions are in `docs/adr/0003-storage.md`.

## A gateway in front (AbpGateway)

One origin for a site's servers and its web app, so the browser, cookies and the content security policy see one
address. It's a small ASP.NET Core app on [YARP](https://github.com/dotnet/yarp), with no ABP module and no database.

- **It only routes.** The server's paths (`/api`, `/connect`, `/.well-known`, `/mcp` by default, plus your own) go to
  the servers, every other path to the web. A public path can reach the server under another, a webhook's address under
  the API, say. Sign-in, limits and everything else stay in the servers.
- **Several servers.** `Gateway:Server` names each, or one name every server answers to with `Gateway:ResolveServers`
  (a Kubernetes headless Service), resolved every 5 seconds and patient while it has no address yet. With a readiness
  path each server is checked every few seconds and left out after two failures; one that two requests in a row
  couldn't reach is left out at once, for 10 seconds.
- **MCP sessions stay put.** An MCP session lives in the memory of the server that started it, so the gateway puts that
  server's address, encoded, before the session id it hands the client, and sends each request carrying it back there.
  Any gateway replica can, since nothing is learned. Requests without a session go to the less busy of two servers.
- **Long answers and big uploads.** Server-sent events may stay quiet for 30 minutes (`GatewayOptions.StreamIdle`), and
  the request size isn't limited here: the servers know what each upload is for.
- **Browser protections.** HSTS over HTTPS (`Gateway:Hsts:MaxAgeDays`), `nosniff`, a referrer policy, a content security
  policy enforced (`Gateway:Csp`) and a fuller one reported only (`Gateway:CspReportOnly`), each yours to set.
- **The visitor's address** from `X-Forwarded-For` and `X-Forwarded-Proto`, believed only from loopback and the networks
  in `Gateway:TrustedProxies`, so a caller can't name its own.
- **`/_gateway/health`** answers while the gateway runs, for a container's liveness probe.

```csharp
GatewayOptions options = new() { ReadinessPath = "/api/ready" };
options.ServerPaths.Add("/plugins-web/{**rest}");
options.Rewritten.Add(new GatewayRewrite("/hooks/git/{id}", "/api/hooks/git/{id}"));

WebApplication app = GatewayApp.Build(args, options);
app.Run();
```

```json
{ "Gateway": { "Server": "http://server-1:8080, http://server-2:8080", "Web": "http://web:3000" } }
```

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
