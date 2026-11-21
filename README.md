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

Coming from ABP's module template? [Where ABP's layers are in this repository](docs/abp-layers.md).

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
- **`AbpCluster.Storage.HttpApi`**, the admin API for that page, so you write no controller: `GET {prefix}` (where the
  storage is, without a key), `POST {prefix}/test`, and `GET`/`POST {prefix}/copy`. It's read only; storage stays in
  configuration. You give the route prefix, your site-admin policy (or your own `IStorageRights`; with neither, no one
  is let in) and what starting a copy needs besides, such as a step-up.
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

```csharp
[DependsOn(typeof(AbpClusterStorageHttpApiModule))]
public class MyHttpApiModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context) =>
        Configure<StorageHttpOptions>(o =>
        {
            o.RoutePrefix = "api/admin/storage";
            o.Policy = "SiteAdmin";                                          // who may see, test and copy
            o.Sensitive = action => action.Filters.Add(new MyStepUpFilter()); // on POST copy
        });
}
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

## Outgoing webhooks (AbpWebhooks)

Signed webhooks from an ABP app, sent reliably from every server. The application keeps its webhooks (where each sends,
which events, who may change them), decides what each delivery says, and queues it; AbpWebhooks keeps the queue in the
application's own database and sends it. It doesn't need AbpCluster: it can be used alone.

- **Queued once.** A delivery's id comes from the event's and the webhook's (`WebhookIds.Delivery`, a name-based UUID),
  so an event heard twice from an outbox is queued once: `WebhookQueue.EnqueueAsync` leaves one already there alone.
- **Signed.** A POST of the payload with `X-Webhook-Event`, `X-Webhook-Delivery`, `X-Webhook-Timestamp` and
  `X-Webhook-Signature: sha256=` the HMAC-SHA256 of `{timestamp}.{body}` with the webhook's secret. Each header can go
  under more prefixes too (`WebhookSendingOptions.HeaderPrefixes`), for receivers set up with older names. A receiver
  checks with `WebhookSignature.Verify`, which also refuses a delivery more than five minutes old.
- **Every server, each delivery once.** The worker on each server claims due deliveries by moving their next attempt on
  (a two-minute lease), so two servers never send the same one; `WebhookWakeup.Signal` wakes it at once, and an
  `IWebhookWakeOthers` of yours wakes the other servers.
- **Retried, then given up.** Anything but a 2xx is tried again after 1, 5 and 15 minutes, 1 and 4 hours, then fails;
  the status and the start of a refusal are kept. A delivery to a webhook that's off waits while it's off; one to a
  removed webhook fails. `WebhookQueue.RedeliverAsync` sends one again as a new delivery. Sent and failed ones are kept
  30 days.

```csharp
[DependsOn(typeof(AbpWebhooksModule))]
public class MyAppModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        context.Services.AddAbpDbContext<MyDbContext>(o => o.ReplaceDbContext<IWebhooksDbContext>());
        context.Services.AddTransient<IWebhookEndpoints, MyWebhookEndpoints>(); // url, secret, enabled for a webhook id
        // The client goes wherever an admin typed: give it your guard (no private addresses, no redirects).
        context.Services.AddHttpClient("Webhooks", c => c.Timeout = TimeSpan.FromSeconds(10));
    }
}

// In MyDbContext (which implements IWebhooksDbContext): a table made before keeps its names.
builder.ConfigureWebhookDeliveries("webhook_deliveries", columns: new("ProjectId", "RecordId", "RecordKey"));

// Where something happened:
await queue.EnqueueAsync([new WebhookDelivery(WebhookIds.Delivery(eventId, hook.Id), hook.Id, "order.paid",
    scopeId: shopId, subjectId: order.Id, subject: order.Number, payload)], ct);
```

## Languages and translations (AbpLanguages)

A site's languages and translations: languages as data, the texts that ship with the code translated or reworded by the
site's admins without a build, and each language's texts served from memory on every server. It doesn't need
AbpCluster: it can be used alone.

- **Languages are data:** any BCP 47 code, a native and an English name, a direction (worked out from the code, and
  changeable), on or off, an order, and a fallback. One is the default. The source language (English) can be turned
  off but not removed, since every text is written in it first.
- **Chains:** a request's language, its fallbacks, the default, then the source. `LanguagesCache.ChainFor(acceptLanguage)`
  picks the first enabled language a header names: exactly, by its base language, or a regional language of that base.
  `LanguageChain.Pick` chooses a name kept by language (`{ "en": "Open", "ar": "مفتوح" }`).
- **Catalogs:** the app's texts, given as `ITextCatalogSource`s, such as the web's `messages/*.json` embedded in the
  server (`EmbeddedTextCatalogSource`) or a plugin system's. A source whose catalogs change while the site runs gives a
  `Generation` that changes with them.
- **Admins' texts** win over the shipped ones. Each is kept with a hash of the source it was made from, so one whose
  source changed shows as out of date. A translation must keep its source's ICU arguments (`{name}`,
  `{count, plural, …}`), or nothing is saved.
- **Bundles:** each language's merged texts (the admin's text, else the shipped one, along the chain, else the source)
  are built once, kept as bytes with a content hash, and served by a byte copy. Every server reads one row's version at
  most every two seconds, and a change bumps it in its own transaction, so a change reaches every server without a
  distributed cache.
- **Your tables:** `ILanguagesDbContext` with `ConfigureLanguages(schema, tables)`, so a product that had its own
  tables keeps them.

| Package | What it is | Depends on |
|---|---|---|
| `AbpLanguages` | The languages, catalogs, chains, cache, the admins' operations (`LanguagesService`) and the seeder | ABP's EF Core |
| `AbpLanguages.HttpApi` | The API over HTTP, under a prefix you set: the languages and a language's texts for anyone; the rest for your admins | `AbpLanguages`, ABP's MVC |

```csharp
[DependsOn(typeof(AbpLanguagesHttpApiModule))]
public class MyHostModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        context.Services.AddSingleton<ITextCatalogSource>(new EmbeddedTextCatalogSource(typeof(MyHostModule).Assembly, "myapp.texts."));
        Configure<AbpLanguagesOptions>(o =>
        {
            o.MainCatalog = "app";                                              // your web's own texts
            o.Seed.Add(new LanguageSeed("ar", "العربية", "Arabic", Fallback: "en"));
        });
        Configure<LanguagesHttpOptions>(o =>
        {
            o.RoutePrefix = "api/localization";
            o.Policy = "SiteAdmin";                                             // who may change languages and texts
        });
    }
}

[ReplaceDbContext(typeof(ILanguagesDbContext))]
public class MyDbContext(DbContextOptions<MyDbContext> options) : AbpDbContext<MyDbContext>(options), ILanguagesDbContext
{
    public DbSet<Language> Languages => Set<Language>();
    public DbSet<TextOverride> TextOverrides => Set<TextOverride>();
    public DbSet<LanguagesState> LanguagesState => Set<LanguagesState>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.ConfigureLanguages();                                          // schema "languages"
    }
}
```

Your migrations make the tables. `LanguagesSeeder` gives a new site its languages and the version row, through ABP's
data seeding or your own start (`SeedAsync`). Embed your web's messages as `myapp.texts.{catalog}.{language}.json`.

The API, relative to the prefix:

| Route | Who | What |
|---|---|---|
| `GET languages` | anyone | the enabled languages, each with its texts' version (`?all=true`: every one with its progress, for admins) |
| `GET texts/{code}` | anyone | the language's merged texts; with `?v={version}` cached for good, else answered by ETag |
| `POST languages`, `PUT languages/{code}`, `DELETE languages/{code}` | admins | add, change, remove |
| `GET texts/{code}/entries` | admins | every text with its source and status (source, translated, outdated, bundled, missing) |
| `PUT texts/{code}` | admins | set or take away texts, all or none |
| `GET texts/{code}/export?catalog=` | admins | a catalog's texts in the language, nested, for a translator |
| `POST texts/{code}/import` | admins | a translator's file back: imported, unchanged, kept, unknown and refused keys |

Who's an admin is your `Policy`, or your own `ILanguagesRights`. With neither, no one may change anything. The
bundle's JSON is `{ language, direction, chain, version, messages, catalogs }`; give your own `ITextBundleWriter` for
another shape. `ILanguagesEvents` tells you of each change once it's committed, for your audit.

What stays yours: names stored by language in your own data, and searching them (`LanguagesCache.TranslationsAsync`
gives every text in every enabled language, for a search of your own); which language a person chose (a cookie, an
account setting), and setting the request's chain where your code reads it (`ChainFor`). Server messages (exceptions'
texts) aren't catalogs.

The decisions are in `docs/adr/0004-languages.md`.

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
