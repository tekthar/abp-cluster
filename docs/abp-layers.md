# For ABP developers: where these packages sit in ABP

The packages here are infrastructure, like ABP's own `Volo.Abp.DistributedLocking` or `Volo.Abp.BlobStoring`, not an
application module, so there are no Domain or Application layers to map. Each is one package and one module
(AbpGateway is a plain ASP.NET Core app with no module), and AbpCluster.Storage has an HttpApi package beside it. This
page says which ABP part each builds on or replaces.

| Package | ABP's counterpart | How it fits |
|---|---|---|
| `AbpCluster` | `Volo.Abp.DistributedLocking` | `AbpClusterModule` replaces ABP's `IAbpDistributedLock` with one on PostgreSQL advisory locks (`IClusterLocks`), so ABP's outbox sender and background jobs run on one server at a time |
| `AbpCluster.Sites` | None (ABP has no two-site standby) | `AbpClusterSitesModule`, `SitesOptions`; its tables are made by SQL in a schema you name, since it runs before your migrations and on a read-only replica |
| `AbpCluster.Storage` | `Volo.Abp.BlobStoring` with its FileSystem, AWS and Azure providers | `AbpClusterStorageModule` configures ABP's BLOB containers from settings (with keyless sign-in), and adds what BlobStoring doesn't: reading from the previous store while files are copied, a hash-checked copier, the store test, the inventory of stored files (`IStoredFiles`), a check of the files storage lacks after a takeover or a restore (`StoredFilesCheck`), and knowing an upload by its bytes (`FileKinds`). It depends on `AbpCluster`: the copy and the check each hold a cluster lock |
| `AbpCluster.Storage.HttpApi` | An ABP module's `*.HttpApi` layer: controllers over the module's services | `AbpClusterStorageHttpApiModule`: one controller over `StorageAdmin` (the storage, a test of each store, the copy), under the route prefix the host sets, behind the host's policy or its `IStorageRights`. Plain controllers, not `[RemoteService]` application services, so ABP makes no dynamic proxies for them |
| `AbpWebhooks` | None in ABP's open modules (ASP.NET Boilerplate, ABP's predecessor, had webhooks; ABP's outbox carries events between services, not to outside addresses) | `AbpWebhooksModule`, on ABP's EF Core alone (no AbpCluster). The queue's table goes through `IWebhooksDbContext` (`ReplaceDbContext`) and `modelBuilder.ConfigureWebhookDeliveries(table, schema, columns)` in your context and migrations, as an ABP module's would. Work runs in ABP units of work, and sending uses a named `IHttpClientFactory` client you give your guard. Your webhooks are yours (`IWebhookEndpoints`) |
| `AbpGateway` | None | A YARP app, `GatewayApp.Build(args, options)`; it routes to your ABP servers and has no ABP dependency |

## What works the ABP way

- **Modules and options:** each package (but the gateway) is a module with `[DependsOn]`, configured by an options
  class (`SitesOptions`, `ClusterStorageOptions`…) or the configuration section it names. Storage sets itself up in its
  module (`PreConfigure<ClusterStorageOptions>`), not through an `Add…` call, so `AbpClusterStorageModule` must be in
  your module's `[DependsOn]`.
- **ABP's abstractions are used, not bypassed:** the lock is ABP's `IAbpDistributedLock` underneath, and files go
  through ABP's BLOB containers, so code written against ABP's interfaces keeps working.
- **The database:** AbpWebhooks follows ABP's pattern (an `I*DbContext` you replace, a `Configure*` extension).

## Where it differs, and why

- **Sites and Storage make their tables by SQL,** not EF Core migrations. Sites must work before the host's migrations
  run and on a read-only standby. Storage makes one table, `{schema}.storage_copy` (schema `cluster` unless
  `ClusterStorageOptions.Schema` names another), and only the first time a copy saves its state with a connection string:
  none at start, and reading tolerates it missing. Both name their schema in options, so a host keeps them apart from its
  own, or makes the table in its own migration.
- **The gateway isn't an ABP module:** it runs in its own process in front of the servers and needs nothing of ABP.
- **PostgreSQL only** for the lock, the sites and the storage inventory, as their README sections say.
