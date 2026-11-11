# Changelog

All notable changes to AbpCluster are written here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the package follows
[Semantic Versioning](https://semver.org/spec/v2.0.0.html): before 1.0, a minor version may change the API.

## [Unreleased]

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
