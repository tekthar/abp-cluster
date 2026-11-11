# 0001. AbpCluster as a package

- **Status:** Accepted
- **Date:** 2026-11-11

## Context

A site that runs on several servers needs some work done on one server at a time: migrating and seeding at start, a
worker's round, ABP's outbox and background jobs. Three products kept their own copy of the same PostgreSQL advisory
lock for it, line for line, apart from the prefix before each name. And ABP's own distributed lock locks within one
process by default, so a host must replace it before running on two servers.

## Decision

- **One small package, named for what it does.** `IClusterLocks` with its PostgreSQL default, and ABP's
  `IAbpDistributedLock` on it. Not a package of everything the team's packages share: what looks shared between them
  (the host's accounts, rights and scopes) asks different things of each host, and a package every other one depends
  on would make each of its changes a release of all of them.
- **The same API as the copies it replaces**, so moving onto it is mechanical: `TryTakeAsync` and `TakeAsync`, and the
  static forms by connection string.
- **Three fixes on the way:** no connection goes back to the pool holding a lock (when taking fails, every lock of its
  session is undone); letting go of a lock whose connection broke doesn't throw; a held lock's session carries its
  name. Connections stay pooled: workers try their locks every second or so, and a new connection each time would
  cost the database a process each.
- **A package that wants a lock of its own keeps its seam** (AbpGuard's `IGuardLocks`, which a host may replace) and
  builds its default on `IClusterLocks`.

## Consequences

- A host adds `AbpClusterModule` and gets both the locks and ABP's lock across servers. A host that wants another
  provider for ABP's lock (Redis, through `Volo.Abp.DistributedLocking`) replaces `IAbpDistributedLock` again in its own
  module, whose services are configured after the modules it depends on.
- PostgreSQL only, as the products are.
