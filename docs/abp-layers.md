# For ABP developers: where these packages sit in ABP

The packages here are infrastructure, like ABP's own `Volo.Abp.DistributedLocking` or `Volo.Abp.BlobStoring`, not an
application module, so there are no Domain, Application or HttpApi layers to map. Each is one package and one module
(AbpGateway is a plain ASP.NET Core app with no module). This page says which ABP part each builds on or replaces.

| Package | ABP's counterpart | How it fits |
|---|---|---|
| `AbpCluster` | `Volo.Abp.DistributedLocking` | `AbpClusterModule` replaces ABP's `IAbpDistributedLock` with one on PostgreSQL advisory locks (`IClusterLocks`), so ABP's outbox sender and background jobs run on one server at a time |
| `AbpCluster.Sites` | None (ABP has no two-site standby) | `AbpClusterSitesModule`, `SitesOptions`; its tables are made by SQL in a schema you name, since it runs before your migrations and on a read-only replica |
| `AbpCluster.Storage` | `Volo.Abp.BlobStoring` with its FileSystem, AWS and Azure providers | `AbpClusterStorageModule` configures ABP's BLOB containers from settings (with keyless sign-in), and adds what BlobStoring doesn't: reading from the previous store while files are copied, a hash-checked copier, the store test and the inventory of stored files |
| `AbpWebhooks` | None in ABP's open modules | `AbpWebhooksModule`; its table through `IWebhooksDbContext` and `modelBuilder.ConfigureWebhookDeliveries()`, in your context and migrations, as an ABP module's would be |
| `AbpGateway` | None | A YARP app, `GatewayApp.Build(args, options)`; it routes to your ABP servers and has no ABP dependency |

## What works the ABP way

- **Modules and options:** each package (but the gateway) is a module with `[DependsOn]`, configured by an options
  class (`SitesOptions`, `ClusterStorageOptions`…) or the configuration section it names.
- **ABP's abstractions are used, not bypassed:** the lock is ABP's `IAbpDistributedLock` underneath, and files go
  through ABP's BLOB containers, so code written against ABP's interfaces keeps working.
- **The database:** AbpWebhooks follows ABP's pattern (an `I*DbContext` you replace, a `Configure*` extension).

## Where it differs, and why

- **Sites and Storage make their tables by SQL,** not EF Core migrations: Sites must work before the host's migrations
  run and on a read-only standby, and both name their schema in options so a host keeps them apart from its own.
- **The gateway isn't an ABP module:** it runs in its own process in front of the servers and needs nothing of ABP.
- **PostgreSQL only** for the lock, the sites and the storage inventory, as their README sections say.
