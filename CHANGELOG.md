# Changelog

All notable changes to AbpCluster are written here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the package follows
[Semantic Versioning](https://semver.org/spec/v2.0.0.html): before 1.0, a minor version may change the API.

## [Unreleased]

## [0.4.1] - 2026-11-13

### Fixed

- `AbpCluster.Storage`'s store test (`StorageAdmin.TestAsync`) writes its file at the store's top
  (`.storage-test-{id}`) instead of in a folder of its own, so it no longer leaves an empty `.storage-test` folder in
  each file-system store on every start. Remove one left by 0.4.0 if you like; nothing reads it. AbpCluster,
  AbpCluster.Sites and AbpGateway don't change.

## [0.4.0] - 2026-11-13

### Added

- **AbpCluster.Storage**, a new package: where an app keeps its files, set by configuration (`Storage`): the file
  system, S3 (AWS, or MinIO and other servers, keyless through AWS's own chain) or Azure Blob (a connection string, or
  the account's address to sign in as the server). `AbpClusterStorageModule` with `ClusterStorageOptions` (the app's
  stores, the section, the file system's default folder, the copy's schema); moving to another storage while the site
  runs (`Storage:Previous`: reads fall back, deletes in both, `StorageCopier` checks hashes, one run per site, its
  state in PostgreSQL); `IStoredFiles` and `StoredFilesCheck`; `StorageAdmin` (describe, test each store, the copy);
  `IStorageEvents`; `FileKinds` (known by bytes) and `ServeSafely`. Unlike the code it came from, S3 on AWS with no
  keys (an IAM role) works: ABP's configuration is never handed a null key. AbpCluster, AbpCluster.Sites and AbpGateway don't
  change.

## [0.3.0] - 2026-11-13

### Added

- **AbpGateway**, a new package: one origin for a site's servers and its web app, on YARP. `GatewayApp.Build(args,
  options)` with `GatewayOptions`: the server's paths (`CommonServerPaths` plus your own), public paths rewritten to the
  server's, a readiness path, and how long server-sent events may stay quiet. Several servers, named or resolved from DNS,
  checked for readiness and left out after failed requests; MCP sessions kept on the server that started them; browser
  protections (HSTS, nosniff, referrer policy, content security policy); `X-Forwarded-For` believed only from trusted
  proxies; `/_gateway/health`. It comes from Toda's gateway, for Toda and Insight to share. AbpCluster and
  AbpCluster.Sites don't change.

## [0.2.1] - 2026-11-12

### Fixed

- `AbpCluster.Sites`' messages name settings as the host configures them (`SitesOptions.Section`, set by
  `SitesOptions.From`): a host reading its options from `Site` is told to set `Site:BreakGlassKeyHash`, not
  `Sites:BreakGlassKeyHash`, which it would have set to no effect when its standby needed promoting.

## [0.2.0] - 2026-11-12

Two sites, from the first product that ran them, proven by moving it onto the package.

### Added

- `AbpCluster.Sites`, a second package: two sites, one active and one standby that runs and takes over when its
  database is promoted (ADR 0002, `docs/sites.md`). `SiteStart` decides what a server runs; `SiteHost` serves a standby,
  a replaced site or a site handing over safely on a replica; `SiteGuard` claims, fences and stands down;
  `SiteState` and `MapClusterSites` answer readiness and the signed state; `SitesAdmin` lists and changes the sites and
  starts a planned switchover; `SiteLagWatch` tells `ISiteAlerts` of a lagging standby; `SiteRunner` is the start-up
  loop. A host's own parts are seams: `OnTakeover`, `ChangesSince`, `ISiteAfterTakeover`, `ISiteSigningKey`, the
  shared settings' fingerprint, paths, error codes, schema and lock prefix.

## [0.1.0] - 2026-11-11

The first release: the cluster lock three products kept their own copies of, as one package.

### Added

- `IClusterLocks` (`TryTakeAsync`, `TakeAsync`) and its default, `ClusterLocks`: PostgreSQL advisory locks, each on a
  connection of its own, with `ClusterLocks.TakeAsync(connectionString, key, ct)` and `TryTakeAsync` for work before
  the container is built.
- `AbpClusterOptions`: the database (`ConnectionString`, else the one `ConnectionStringName` names, "Default") and a
  `KeyPrefix` before every name.
- `ClusterAbpDistributedLock`: ABP's `IAbpDistributedLock` on the cluster's locks, put in place by `AbpClusterModule`.

### Changed, from the copies it replaces

- A wait cancelled just as the lock came no longer leaves it held by a connection back in the pool: when taking fails,
  every lock of that session is undone first.
- Letting go of a lock whose connection broke while the work ran no longer throws: the database let it go already.
- While a lock is held its session is named `lock <name>` in `pg_stat_activity`, and goes back to its own name after.
