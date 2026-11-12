# Running at two sites

`AbpCluster.Sites` runs an ABP host at two sites: one active, one standby that runs and takes over when its database is
promoted. This page is what a host does to use it, and what operators do with it. The decisions are in
[ADR 0002](adr/0002-two-sites.md).

## In the host

```csharp
// Program.cs: decide what this server runs, again whenever that changes.
static SitesOptions Sites(IConfiguration configuration)
{
    SitesOptions options = SitesOptions.From(configuration);          // the "Sites" section
    options.SharedSettings.AddRange(["App:PublicUrl", "Oidc:Issuer", "Calendar:TimeZone"]);
    options.SharedSecrets.Add("Oidc:WebClientSecret");
    options.OnTakeover.Add(async (connection, transaction, takeover, ct) =>
    {
        // Move what you number past what the lost site may have given out in its last seconds.
        await using var raise = new NpgsqlCommand("UPDATE app.counters SET \"Value\" = \"Value\" + @gap", connection, transaction);
        raise.Parameters.AddWithValue("gap", takeover.KeyGap);
        await raise.ExecuteNonQueryAsync(ct);
    });
    return options;
}

return await SiteRunner.RunAsync(args, Sites, async () =>
{
    WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
    builder.Host.UseAutofac();
    builder.Services.AddClusterSites(Sites(builder.Configuration));
    await builder.AddApplicationAsync<MyHostModule>();      // [DependsOn(typeof(AbpClusterSitesModule))]
    await using WebApplication app = builder.Build();
    await app.InitializeApplicationAsync();
    app.MapClusterSites();
    await app.RunAsync();
});
```

And in the host module, after the migrations and before any worker runs:

```csharp
public override async Task OnPreApplicationInitializationAsync(ApplicationInitializationContext context)
{
    // ... migrate ...
    await context.ServiceProvider.GetRequiredService<SiteGuard>().CheckAtStartAsync(CancellationToken.None);
}
```

Give the sites' admin page your own controller over `SitesAdmin`, with your rights, the password asked again before a
switchover, and your audit. Give `ISiteAlerts` your own notice to admins, and `ISiteAfterTakeover` any check to run
once after a takeover (that every stored file the database names is in storage, say).

`SiteRunner.RunAsync` returns the exit code: 0 when the server was stopped, 75 when the app should start afresh (the
site became active again after its site host ran). Let your process manager restart on 75.

## Settings

| Setting | Default | |
|---|---|---|
| `Sites:Name` | none | This server's site. Unset, none of this runs: the server is a site of its own. |
| `Sites:Schema` | `cluster` | The schema of the sites' tables. |
| `Sites:LockPrefix` | empty | Before the names of this package's own advisory locks; set it as `AbpClusterOptions.KeyPrefix`. |
| `Sites:KeyGap` | 1000 | How far a takeover after a disaster tells `OnTakeover` to move numbers. |
| `Sites:BreakGlassKeyHash` | none | The SHA-256, in hex, of the operators' break-glass key. |
| `Sites:PromoteOnHandover` | true | A standby handed over to promotes its own database, where its role may. |
| `Sites:ForceTakeover` | false | Take over though the timeline shows no promotion (some managed databases). Set once. |
| `Sites:GuardSeconds`, `WatchSeconds`, `LagCheckSeconds` | 5, 2, 60 | How often the app, the site host and the lag watch look. |
| `Sites:LagWarnMegabytes`, `WarnAfterMinutes` | 16, 5 | When a standby lags, and for how long before admins hear. |
| `Sites:StatePath`, `ReadyPath`, `HealthPath` | `/api/site`, `/api/ready`, `/api/health` | The routes. |

Both sites use the same public address and identity issuer, the same secrets, and keys kept in the database (never in a
folder of a server's own, which makes new keys when empty). Each site says a fingerprint of `SharedSettings` and
`SharedSecrets` with its state, and `SitesAdmin` flags one that differs.

## For operators

1. **Replicate the database** by physical streaming replication (plain PostgreSQL, CloudNativePG's replica cluster,
   Patroni's standby cluster, or a managed replica). Logical replication carries neither DDL nor sequences.
2. **Replicate the storage** with its own replication (S3, MinIO site replication, Azure geo-redundancy).
3. **Run both sites' servers.** DR's start as standbys: their readiness is 200 (they can show the standby page), their
   state is 503.
4. **Send people to the site whose state answers 200**: a global balancer, or DNS failover with a TTL of 60 seconds or
   less.
5. **Give each site the other's address** (its servers' address inside the network) through `SitesAdmin`.
6. **Keep the break-glass key** somewhere every operator can reach in a disaster; set its hash on every server.

**Planned switchover** (no data lost): from the active site, start a switchover to the standby. Its apps stop, its last
write waits until none runs, the standby promotes its own database and takes over. Drill it each quarter.

**Disaster**: once someone decides, promote DR (its standby page with the break-glass key, or the database's tooling).
Its servers take over within seconds, moving your numbers by the key gap. If HQ comes back it finds DR active at a
higher epoch, signed, and stands down; export what changed there after DR took over, then rebuild its database as DR's
replica (`pg_rewind`, CloudNativePG's demotion, or a new copy), and switch back when you choose.
