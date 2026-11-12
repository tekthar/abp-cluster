using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace AbpCluster.Sites;

/// <summary>
/// How a host runs on two sites. Read from configuration (the <c>Sites</c> section by default, or the one a host names)
/// before anything else starts, and given to both the start-up loop (<see cref="SiteRunner"/>) and the app
/// (<see cref="SitesServiceCollectionExtensions.AddClusterSites"/>). The hooks (<see cref="OnTakeover"/>,
/// <see cref="ChangesSince"/>, <see cref="Fingerprint"/>, <see cref="ConfigureHostBuilder"/>, <see cref="ConfigureHost"/>)
/// are set in code.
/// </summary>
public sealed partial class SitesOptions
{
    public const string DefaultSection = "Sites";

    /// <summary>This server's site (<c>hq</c>, <c>dr</c>). Unset: the server is a site of its own and none of this runs.</summary>
    public string? Name { get; set; }

    /// <summary>The database. Unset: the connection string named <see cref="ConnectionStringName"/>.</summary>
    public string? ConnectionString { get; set; }

    public string ConnectionStringName { get; set; } = "Default";

    /// <summary>The schema of the sites' tables ("cluster"). A host that has them already names its own.</summary>
    public string Schema { get; set; } = "cluster";

    /// <summary>Put before the names of the advisory locks this package takes, as <see cref="AbpClusterOptions.KeyPrefix"/>.</summary>
    public string LockPrefix { get; set; } = "";

    /// <summary>
    /// How far a takeover after a disaster tells <see cref="OnTakeover"/> to move numbers it hands out (record keys,
    /// invoice numbers), so ones the lost site gave out in the seconds the replica never saw aren't given again. 1,000.
    /// A planned switchover loses nothing, and passes 0.
    /// </summary>
    public long KeyGap { get; set; } = 1000;

    /// <summary>How often the running app looks at its database and the other sites, in seconds. 5.</summary>
    public int GuardSeconds { get; set; } = 5;

    /// <summary>How often the site host looks at its database, in seconds. 2.</summary>
    public int WatchSeconds { get; set; } = 2;

    /// <summary>How often the active site reads its standbys, in seconds. 60.</summary>
    public int LagCheckSeconds { get; set; } = 60;

    /// <summary>How far behind a standby may be before it lags (and is refused as a switchover's target), in megabytes. 16.</summary>
    public long LagWarnMegabytes { get; set; } = 16;

    /// <summary>How long a standby lags or can't be read before the site's admins hear of it, in minutes. 5.</summary>
    public double WarnAfterMinutes { get; set; } = 5;

    /// <summary>
    /// The SHA-256, in hex, of the key the operators keep to promote a standby, or call a hand-over off, when no one can
    /// sign in. As <c>sha256sum</c> prints it is fine. Unset: nothing takes a key.
    /// </summary>
    public string? BreakGlassKeyHash { get; set; }

    /// <summary>Takes over even when the database's timeline doesn't show a promotion (some managed databases). Set once, then unset.</summary>
    public bool ForceTakeover { get; set; }

    /// <summary>A standby handed over to promotes its own database, where its role may (<c>pg_promote</c>). On.</summary>
    public bool PromoteOnHandover { get; set; } = true;

    /// <summary>
    /// The site's state, which another site and a global balancer read: 200 only on the active site. Its actions
    /// (promote, cancel-handover, changes) are below it. "/api/site".
    /// </summary>
    public string StatePath { get; set; } = "/api/site";

    /// <summary>Whether this server can serve (its readiness probe). "/api/ready".</summary>
    public string ReadyPath { get; set; } = "/api/ready";

    /// <summary>Whether the process is alive (its liveness probe), on the site host. "/api/health".</summary>
    public string HealthPath { get; set; } = "/api/health";

    /// <summary>Put before the error codes the site host answers with ("Sites" makes "Sites:Standby").</summary>
    public string ErrorCodePrefix { get; set; } = "Sites";

    /// <summary>Settings both sites must have the same, by their keys (the public address, the issuer, the time zone).</summary>
    public List<string> SharedSettings { get; set; } = [];

    /// <summary>Secrets both sites must have the same; only their hashes go into the fingerprint.</summary>
    public List<string> SharedSecrets { get; set; } = [];

    /// <summary>
    /// Run in the takeover's transaction, after the active site changes and before it commits: move the numbers the host
    /// hands out by <see cref="SiteTakeover.KeyGap"/>. Each runs on every takeover and switchover; one that throws undoes it.
    /// </summary>
    public List<SiteTakeoverStep> OnTakeover { get; } = [];

    /// <summary>
    /// What changed on a replaced site after the other took over (its row says which, and since when), from the host's own
    /// tables, for operators to export before its database is rebuilt; answered as it is. Unset: the site host doesn't
    /// offer it.
    /// </summary>
    public Func<NpgsqlConnection, ActiveSiteDto, CancellationToken, Task<object>>? ChangesSince { get; set; }

    /// <summary>The settings fingerprint, when the host makes its own; else one from <see cref="SharedSettings"/> and <see cref="SharedSecrets"/>.</summary>
    public Func<IConfiguration, string>? Fingerprint { get; set; }

    /// <summary>Called on the site host's builder: the host's configuration sources, forwarded headers' options.</summary>
    public Action<WebApplicationBuilder>? ConfigureHostBuilder { get; set; }

    /// <summary>Called on the site host before its routes: forwarded headers, the host's own routes that are safe on a replica.</summary>
    public Action<WebApplication>? ConfigureHost { get; set; }

    /// <summary>The options from <paramref name="section"/> of <paramref name="configuration"/>.</summary>
    public static SitesOptions From(IConfiguration configuration, string section = DefaultSection)
    {
        SitesOptions options = new();
        configuration.GetSection(section).Bind(options);
        options.Name = string.IsNullOrWhiteSpace(options.Name) ? null : options.Name.Trim();
        return options;
    }

    /// <summary>The database this site's servers use, or null when there's none (a test host).</summary>
    public string? ConnectionStringFrom(IConfiguration configuration) =>
        ConnectionString is { Length: > 0 } given ? given : configuration.GetConnectionString(ConnectionStringName);

    /// <summary>The schema, quoted for SQL; refused when it isn't a plain lower-case name.</summary>
    internal string QuotedSchema => SchemaName().IsMatch(Schema)
        ? $"\"{Schema}\""
        : throw new InvalidOperationException($"Sites:Schema \"{Schema}\" isn't a plain name (lower-case letters, digits and _).");

    /// <summary>A lock's key, as the database hashes it.</summary>
    internal string LockKey(string name) => $"hashtextextended('{(LockPrefix + name).Replace("'", "''", StringComparison.Ordinal)}', 0)";

    internal string Code(string code) => $"{ErrorCodePrefix}:{code}";

    [GeneratedRegex("^[a-z_][a-z0-9_]{0,62}$")]
    private static partial Regex SchemaName();
}

/// <summary>A step of a takeover, in its transaction: move what the host numbers past what the lost site may have given out.</summary>
public delegate Task SiteTakeoverStep(NpgsqlConnection connection, NpgsqlTransaction transaction, SiteTakeover takeover, CancellationToken cancellationToken);

/// <summary>
/// A takeover: the site taking over, the one it replaces, the new epoch, how far to move numbers (0 for a planned
/// switchover, which loses nothing), and whether it was one.
/// </summary>
public sealed record SiteTakeover(string Site, string PreviousSite, long Epoch, long KeyGap, bool Switchover);
