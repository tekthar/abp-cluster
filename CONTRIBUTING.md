# Contributing

- Build with `dotnet build AbpCluster.slnx`.
- `dotnet test AbpCluster.slnx` runs the tests, which need PostgreSQL (see the README).
- Keep the package small: what a site needs to run on several servers, nothing a single product needs. No product's
  name in it.
- A change to how a lock is taken or let go comes with a test on PostgreSQL that shows it, and a line in CHANGELOG.md.
