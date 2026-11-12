# 0002. Two sites: one active, one standby that runs and takes over

- **Status:** Accepted
- **Date:** 2026-11-12

## Context

Organizations ask for a product at their headquarters (HQ) and at a disaster-recovery site (DR), both running, so that
when HQ is lost DR serves the same site within a minute of someone deciding. Every ABP host on PostgreSQL meets the same
traps on the way:

- **A server can't start on a read-only standby.** Migrations, seeders, key rings and sign-in tokens all write, and
  ABP's outbox sender would hand the active site's replicated events to handlers that call out before their first
  write fails.
- **If both sites were writable, everything happens twice**: mail, webhooks, syncs, background jobs, and numbers the
  host hands out (record keys, invoice numbers) given to two different things.
- **A health check that always answers 200** can't tell a balancer which site to send people to.

One product built the answer (two sites in production, with planned switchovers drilled), the next product needs it
too, and none of it depends on what the product does: it works on the database and the servers.

How others do it: GitLab Geo, Atlassian Data Center and Mattermost run one active site and a standby, with PostgreSQL
streaming replication and the storage's own replication; promotion across two sites is a person's decision, because two
sites can't form a quorum; the old primary is fenced first and rebuilt as the new standby.

## Decision

**`AbpCluster.Sites`, a second package beside the cluster's lock**: AbpCluster is about several servers at one site;
this is about two sites, built on the same database and its locks. Its parts:

- **`SiteStart`** decides, before anything touches the database, what a server runs: the app, or the site host as a
  standby, a replaced site, a site handing over, or a server on another site's database. It takes over a promoted
  database (a new epoch, and the host's `OnTakeover` steps, in one transaction under a lock).
- **`SiteHost`** is what runs while the site isn't active: health, readiness, the site's state (503), the break-glass
  actions, and a 503 for everything else. Its watch decides again every two seconds and stops it when that changes.
- **`SiteGuard`** keeps the running app honest: it claims an unclaimed database at start, holds the site's app lock
  shared, and stops the app when its database turns into a replica, names another site, or hands over, or when another
  site signed with the installation's key is active at a higher epoch.
- **`SiteState`** (and `MapClusterSites`) answers the app's readiness and the site's state, signed while active.
- **`SitesAdmin`**: the sites, their states and history; add, change, remove; start a planned switchover.
- **`SiteLagWatch`**: the active site reads its standbys and tells `ISiteAlerts` once a spell when one lags.
- **`SiteRunner`**: the start-up loop a host's `Program` runs through.

Choices:

- **Plain SQL, not a DbContext.** The tables are read before the app starts and on a replica. They live in a schema
  the host names (`cluster` by default) and are made at the app's start where they aren't; a host whose own migrations
  made them in the same shape names its schema and keeps them.
- **Seams, not a product's names.** What a takeover moves (`OnTakeover`), what changed on a replaced site
  (`ChangesSince`), work after a takeover (`ISiteAfterTakeover`, a files check, say), alerts (`ISiteAlerts`), the
  signing key (`ISiteSigningKey`; the default keeps one in the schema), the fingerprint of settings both sites must
  share (`SharedSettings`, `SharedSecrets`, or the host's own), paths, error codes, the options' section and the locks'
  prefix are all the host's.
- **Services, not controllers, for the admin side.** Who may see and change the sites, the password asked again before
  a switchover, and the audit are the host's (its identity package's), as in the team's other packages. The state and
  readiness are endpoints, since balancers and the other sites read them before anyone signs in.
- **A person promotes.** The break-glass key's hash is configuration, checked in constant time, five wrong keys from an
  address in a quarter of an hour and that address waits. The package calls `pg_promote` where the database's role may,
  and otherwise follows the database's own tooling.

## Consequences

- A host adds the module, runs `SiteRunner`, calls `SiteGuard.CheckAtStartAsync` after its migrations, maps the
  endpoints, and gives its takeover steps; an organization runs a replica, storage with replication, and a second set
  of servers, and decides who may promote. No service of the package's own.
- Not tested here: a real replica and its promotion (they need two PostgreSQL servers). The tests drive takeovers with
  `ForceTakeover`, and the first product's end-to-end suite drives real promotions.
- Active-active is not offered: one writer keeps numbers, locks and claims right without a second store. Reads at the
  standby, and promotion without a person (a witness at a third place), can come later on this design.
