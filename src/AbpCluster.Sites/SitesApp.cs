using System.Text.RegularExpressions;
using AbpCluster;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Volo.Abp;
using Volo.Abp.Modularity;

namespace AbpCluster.Sites;

/// <summary>
/// Two sites for an ABP host: the guard, the lag watch, the state and readiness, and the sites' admin service. A host adds
/// the module, gives its options with <see cref="SitesServiceCollectionExtensions.AddClusterSites"/>, calls
/// <see cref="SiteGuard.CheckAtStartAsync"/> after its migrations, maps <see cref="SitesEndpoints.MapClusterSites"/>, and
/// runs through <see cref="SiteRunner"/>.
/// </summary>
[DependsOn(typeof(AbpClusterModule))]
public sealed class AbpClusterSitesModule : AbpModule;

public static class SitesServiceCollectionExtensions
{
    /// <summary>The sites' services, with these options (the same the runner was given).</summary>
    public static IServiceCollection AddClusterSites(this IServiceCollection services, SitesOptions options)
    {
        services.AddSingleton(options);
        services.TryAddSingleton<ISiteSigningKey, DatabaseSiteSigningKey>();
        services.TryAddSingleton<ISiteAlerts, LoggedSiteAlerts>();
        services.AddSingleton<SiteGuard>();
        services.AddHostedService(provider => provider.GetRequiredService<SiteGuard>());
        services.AddHostedService<SiteLagWatch>();
        services.AddSingleton<SiteState>();
        services.AddTransient<SitesAdmin>();
        return services;
    }
}

/// <summary>The default signing key: made in the sites' schema on the first start, read after.</summary>
public sealed class DatabaseSiteSigningKey(IConfiguration configuration, SitesOptions options) : ISiteSigningKey
{
    private byte[]? _key;

    public async Task<byte[]> KeyAsync(CancellationToken cancellationToken)
    {
        if (_key is not null)
        {
            return _key;
        }

        await using NpgsqlConnection connection = new(options.ConnectionStringFrom(configuration));
        await connection.OpenAsync(cancellationToken);
        return _key = await new SiteStore(options).SigningKeyAsync(connection, cancellationToken);
    }
}

/// <summary>What the running app says of its site and its readiness, for the balancer and the other sites.</summary>
public sealed class SiteState(IConfiguration configuration, SitesOptions options, ISiteSigningKey signingKey, ILogger<SiteState> logger)
{
    private readonly string? _connectionString = options.ConnectionStringFrom(configuration);

    /// <summary>Ready when the database answers; the health check stays the liveness probe, so a blip doesn't restart servers.</summary>
    public async Task<(bool Ready, ReadinessDto Readiness)> ReadinessAsync(CancellationToken cancellationToken)
    {
        if (_connectionString is null)
        {
            return (true, new ReadinessDto("Ready", null));
        }

        try
        {
            await SiteStore.ReadDatabaseAsync(_connectionString, cancellationToken);
            return (true, new ReadinessDto("Ready", null));
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Not ready: the database can't be reached.");
            return (false, new ReadinessDto("DatabaseUnreachable", ex.Message));
        }
    }

    /// <summary>
    /// The site's state: active (to be answered 200) while this is the active site, signed with the installation's key;
    /// otherwise its state, to be answered 503.
    /// </summary>
    public async Task<SiteStateDto> StateAsync(CancellationToken cancellationToken)
    {
        string? site = options.Name;
        if (_connectionString is null)
        {
            return new SiteStateDto(site, SiteStates.Active, null);
        }

        SiteDatabaseDto database;
        ActiveSiteDto? active;
        try
        {
            database = await SiteStore.ReadDatabaseAsync(_connectionString, cancellationToken);
            await using NpgsqlConnection connection = new(_connectionString);
            await connection.OpenAsync(cancellationToken);
            active = await new SiteStore(options).ReadActiveAsync(connection, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "The site's state is unknown: the database can't be reached.");
            return new SiteStateDto(site, SiteStates.Unreachable, null);
        }

        // The app on a replica, on a database that names another site, or on a site handing over, lasts only until the guard stops it.
        string state = SiteStart.StateOf(database, active, site);
        SiteStateDto said = new(site, state, database, active, Settings: SiteFingerprint.Of(configuration, options));
        return state != SiteStates.Active || site is null ? said : SiteSignature.Sign(said, await signingKey.KeyAsync(cancellationToken), DateTimeOffset.UtcNow);
    }
}

public static class SitesEndpoints
{
    /// <summary>
    /// The running app's state (200 only while it's the active site) and readiness (200 while its database answers),
    /// open to anyone at <see cref="SitesOptions.StatePath"/> and <see cref="SitesOptions.ReadyPath"/>, as the site host
    /// answers them while the site isn't active. A host with its own routes for them uses <see cref="SiteState"/> instead.
    /// </summary>
    public static IEndpointRouteBuilder MapClusterSites(this IEndpointRouteBuilder endpoints)
    {
        SitesOptions options = endpoints.ServiceProvider.GetRequiredService<SitesOptions>();
        endpoints.MapGet(options.ReadyPath, async (SiteState state, CancellationToken cancellationToken) =>
        {
            (bool ready, ReadinessDto readiness) = await state.ReadinessAsync(cancellationToken);
            return Results.Json(readiness, statusCode: ready ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable);
        }).AllowAnonymous();
        endpoints.MapGet("/" + options.StatePath.Trim('/'), async (SiteState state, CancellationToken cancellationToken) =>
        {
            SiteStateDto said = await state.StateAsync(cancellationToken);
            return Results.Json(said, statusCode: said.State == SiteStates.Active ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable);
        }).AllowAnonymous();
        return endpoints;
    }
}

/// <summary>
/// Tells a site's admins that a standby can't be read or stays too far behind, once a spell. The default writes a
/// warning to the log; a host gives its own (a notice to its admins, an email).
/// </summary>
public interface ISiteAlerts
{
    /// <summary><paramref name="behindBytes"/> is null when the standby can't be read; then <paramref name="error"/> says why.</summary>
    Task StandbyLaggingAsync(string standby, DateTime since, long? behindBytes, string? error, CancellationToken cancellationToken);
}

public sealed class LoggedSiteAlerts(ILogger<LoggedSiteAlerts> logger) : ISiteAlerts
{
    public Task StandbyLaggingAsync(string standby, DateTime since, long? behindBytes, string? error, CancellationToken cancellationToken)
    {
        if (behindBytes is { } bytes)
        {
            logger.LogWarning("{Standby}, the standby, is {Behind} MB behind since {Since:u}.", standby, bytes / (1024 * 1024), since);
        }
        else
        {
            logger.LogWarning("{Standby}, the standby, can't be read since {Since:u}: {Error}.", standby, since, error);
        }

        return Task.CompletedTask;
    }
}

/// <summary>
/// The active site's watch on its standbys. Every <see cref="SitesOptions.LagCheckSeconds"/>, on one server at a time, it
/// reads each standby of this site: one that can't be read, or stays more than <see cref="SitesOptions.LagWarnMegabytes"/>
/// behind for <see cref="SitesOptions.WarnAfterMinutes"/>, is told (<see cref="ISiteAlerts"/>) once for that spell. A
/// spell ends when the standby is read and caught up again. Spells are kept in the database, so they're the site's,
/// whichever server looks.
/// </summary>
public sealed class SiteLagWatch(
    IConfiguration configuration, SitesOptions options, IClusterLocks locks, ISiteAlerts alerts, ILogger<SiteLagWatch> logger) : BackgroundService
{
    private readonly string? _connectionString = options.ConnectionStringFrom(configuration);
    private readonly SiteStore _store = new(options);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_connectionString is null || options.Name is null)
        {
            return;
        }

        TimeSpan interval = TimeSpan.FromSeconds(Math.Max(1, options.LagCheckSeconds));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, stoppingToken);
                await using IAsyncDisposable? held = await locks.TryTakeAsync("site-lag", stoppingToken);
                if (held is not null)
                {
                    await LookAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "The standbys couldn't be looked at this time.");
            }
        }
    }

    internal async Task LookAsync(CancellationToken cancellationToken)
    {
        TimeSpan warnAfter = TimeSpan.FromMinutes(Math.Max(0, options.WarnAfterMinutes));
        long lagWarn = Math.Max(0, options.LagWarnMegabytes) * 1024 * 1024;
        await using NpgsqlConnection connection = new(_connectionString);
        await connection.OpenAsync(cancellationToken);
        if (await _store.ReadActiveAsync(connection, cancellationToken) is not { } active || active.Site != options.Name || active.HandingTo is not null)
        {
            return;
        }

        foreach (SiteRecord standby in (await _store.SitesAsync(connection, cancellationToken)).Where(s => s.Name != options.Name && s.Address.Length > 0))
        {
            (SiteStateDto? state, string? error) = await SiteStatusClient.ReadAsync(standby.Address, options.StatePath, cancellationToken);
            // Only a standby of this site is watched: a site that was replaced, or handed over, is no standby to lag.
            if (state is not null && !(state.State == SiteStates.Standby && state.Active?.Site == options.Name))
            {
                await _store.EndSpellAsync(connection, standby.Name, cancellationToken);
                continue;
            }

            long? behind = state?.Database?.ReplayedLsn is { } lsn ? await SiteStore.BehindAsync(connection, lsn, cancellationToken) : null;
            if (state is not null && behind is not null && behind <= lagWarn)
            {
                await _store.EndSpellAsync(connection, standby.Name, cancellationToken);
                continue;
            }

            (DateTime since, bool told) = await _store.SpellAsync(connection, standby.Name, cancellationToken);
            if (told || DateTime.UtcNow - since < warnAfter)
            {
                continue;
            }

            await alerts.StandbyLaggingAsync(standby.Name, since, state is null ? null : behind, error, cancellationToken);
            await _store.ToldAsync(connection, standby.Name, cancellationToken);
        }
    }
}

/// <summary>
/// The sites as a site admin sees and changes them: the list and each site's state read now, which is active, the
/// history; adding, changing and removing a site; starting a planned switchover. The host's controller checks who may
/// (and asks for the password again before a switchover, as it does for other sensitive actions), and audits it.
/// </summary>
public sealed partial class SitesAdmin(IConfiguration configuration, SitesOptions options)
{
    private readonly SiteStore _store = new(options);

    private string ConnectionString => options.ConnectionStringFrom(configuration)
        ?? throw new UserFriendlyException("This server has no database, so it has no sites.");

    public async Task<SitesOverviewDto> GetAsync(CancellationToken cancellationToken)
    {
        string? self = options.Name;
        await using NpgsqlConnection connection = new(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        List<SiteRecord> sites = await _store.SitesAsync(connection, cancellationToken);
        ActiveSiteDto? active = await _store.ReadActiveAsync(connection, cancellationToken);
        List<SiteChangeDto> changes = await _store.ChangesAsync(connection, 50, cancellationToken);
        string settings = SiteFingerprint.Of(configuration, options);

        // Each site's state as the others read it, all at once.
        (SiteRecord Site, SiteStateDto? Live, string? Error)[] read = await Task.WhenAll(sites.Select(async site =>
        {
            (SiteStateDto? live, string? error) = site.Address.Length == 0 ? (null, null) : await SiteStatusClient.ReadAsync(site.Address, options.StatePath, cancellationToken);
            return (site, live, error);
        }));

        List<SiteDto> listed = [];
        foreach ((SiteRecord site, SiteStateDto? live, string? error) in read)
        {
            long? behind = live is { State: SiteStates.Standby, Database.ReplayedLsn: { } lsn } && live.Active?.Site == self
                ? await SiteStore.BehindAsync(connection, lsn, cancellationToken)
                : null;
            listed.Add(new SiteDto(site.Name, site.Label, site.Address, site.Name == self, live, error, behind,
                live?.Settings is { } theirs ? theirs == settings : null));
        }

        BreakGlass breakGlass = new(options.BreakGlassKeyHash);
        return new SitesOverviewDto(self, active, listed, changes, Math.Max(0, options.KeyGap), breakGlass.Set,
            Math.Max(0, options.LagWarnMegabytes) * 1024 * 1024, breakGlass.Unreadable);
    }

    /// <summary>
    /// A planned switchover to <paramref name="to"/>: only from the active site, to a standby of it that answers and is
    /// caught up. It starts the hand-over; this site's servers stop at their guards' next look.
    /// </summary>
    public async Task<SiteDto> SwitchoverAsync(string to, CancellationToken cancellationToken)
    {
        string self = options.Name ?? throw new UserFriendlyException($"This server has no site name ({options.Setting("Name")}): there's nothing to switch over from.");
        await using NpgsqlConnection connection = new(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        ActiveSiteDto? active = await _store.ReadActiveAsync(connection, cancellationToken);
        if (active is null || active.Site != self)
        {
            throw new UserFriendlyException($"Only the active site hands over, and {self} isn't it.");
        }

        if (active.HandingTo is not null)
        {
            throw new UserFriendlyException($"{self} already hands over to {active.HandingTo}.");
        }

        SiteRecord site = (await _store.SitesAsync(connection, cancellationToken)).FirstOrDefault(s => s.Name == to)
            ?? throw new UserFriendlyException($"There's no site named {to}.");
        if (site.Address.Length == 0)
        {
            throw new UserFriendlyException($"{to} has no address, so its state can't be read: give it one first.");
        }

        (SiteStateDto? live, string? error) = await SiteStatusClient.ReadAsync(site.Address, options.StatePath, cancellationToken);
        if (live is not { State: SiteStates.Standby } || live.Active?.Site != self || live.Database?.ReplayedLsn is not { } replayed)
        {
            throw new UserFriendlyException($"{to} isn't a standby of {self} that answers ({live?.State ?? error}): there's nothing to hand over to.");
        }

        long? behind = await SiteStore.BehindAsync(connection, replayed, cancellationToken);
        if (behind is null || behind > Math.Max(0, options.LagWarnMegabytes) * 1024 * 1024)
        {
            throw new UserFriendlyException($"{to} is {(behind is null ? "an unknown amount" : $"{behind / (1024 * 1024)} MB")} behind: wait until it catches up.");
        }

        if (!await _store.BeginHandoverAsync(connection, self, to, cancellationToken))
        {
            throw new UserFriendlyException($"{self} isn't the active site, or already hands over.");
        }

        return new SiteDto(site.Name, site.Label, site.Address, false, live, null, behind, null);
    }

    public async Task<SiteDto> AddAsync(SiteInput input, CancellationToken cancellationToken)
    {
        string name = (input.Name ?? "").Trim().ToLowerInvariant();
        if (!SiteName().IsMatch(name))
        {
            throw new UserFriendlyException($"A site's name is what its servers' {options.Setting("Name")} says: lower-case letters, digits and hyphens, up to 32.");
        }

        SiteRecord site = new(name, Label(input.Label, name), Address(input.Address));
        await using NpgsqlConnection connection = new(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        if (!await _store.SaveSiteAsync(connection, site, adding: true, cancellationToken))
        {
            throw new UserFriendlyException($"There's a site named {name} already.");
        }

        return new SiteDto(site.Name, site.Label, site.Address, site.Name == options.Name, null, null);
    }

    public async Task<SiteDto> UpdateAsync(string name, SiteUpdateInput input, CancellationToken cancellationToken)
    {
        SiteRecord site = new(name, Label(input.Label, name), Address(input.Address));
        await using NpgsqlConnection connection = new(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        if (!await _store.SaveSiteAsync(connection, site, adding: false, cancellationToken))
        {
            throw new UserFriendlyException($"There's no site named {name}.");
        }

        return new SiteDto(site.Name, site.Label, site.Address, site.Name == options.Name, null, null);
    }

    /// <summary>Takes a site off the list; never this server's own, nor the active one.</summary>
    public async Task RemoveAsync(string name, CancellationToken cancellationToken)
    {
        if (name == options.Name)
        {
            throw new UserFriendlyException("This server's own site stays on the list.");
        }

        await using NpgsqlConnection connection = new(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        if ((await _store.ReadActiveAsync(connection, cancellationToken))?.Site == name)
        {
            throw new UserFriendlyException($"{name} is the active site.");
        }

        if (!await _store.RemoveSiteAsync(connection, name, cancellationToken))
        {
            throw new UserFriendlyException($"There's no site named {name}.");
        }
    }

    private static string Label(string? label, string name) =>
        string.IsNullOrWhiteSpace(label) ? name : label.Trim().Length <= 100 ? label.Trim() : throw new UserFriendlyException("A site's label is at most 100 characters.");

    private static string Address(string? address)
    {
        string value = (address ?? "").Trim().TrimEnd('/');
        if (value.Length == 0)
        {
            return "";
        }

        return value.Length <= 500 && Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) && uri.Scheme is "http" or "https" && string.IsNullOrEmpty(uri.Query)
            ? value
            : throw new UserFriendlyException("A site's address is its servers' http or https address inside the organization's network, with no query.");
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,31}$")]
    private static partial Regex SiteName();
}

/// <summary>
/// A host's start: decide what the server runs (<see cref="SiteStart"/>), run the site host while its site isn't active
/// and decide again when that changes, else run the app. An app that stopped for its site makes the server decide again;
/// an app that comes back after the site host ran ends the process with exit code 75, so it starts afresh.
/// </summary>
public static class SiteRunner
{
    /// <summary>
    /// <paramref name="runApp"/> builds, starts and runs the host's app until it stops; it adds the module and calls
    /// <see cref="SiteGuard.CheckAtStartAsync"/> after its migrations.
    /// </summary>
    public static async Task<int> RunAsync(string[] args, Func<IConfiguration, SitesOptions> options, Func<Task> runApp, ILogger? logger = null)
    {
        ILogger log = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        bool appRan = false;
        while (true)
        {
            IConfiguration configuration = WebApplication.CreateBuilder(args).Configuration;
            SitesOptions own = options(configuration);
            if (own.ConfigureHostBuilder is { } configure)
            {
                // The host's own configuration sources (settings by former names, say) before deciding.
                WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
                configure(builder);
                configuration = builder.Configuration;
                own = options(configuration);
            }

            SiteDecision decision = await SiteStart.DecideAsync(configuration, own, act: !appRan, log, CancellationToken.None);
            if (decision.Mode != SiteMode.App)
            {
                if (!await SiteHost.RunAsync(args, decision, options))
                {
                    return 0;
                }

                continue;
            }

            if (appRan)
            {
                log.LogWarning("The site is active again: the server stops so it starts the app afresh (exit code 75).");
                return 75;
            }

            appRan = true;
            SiteGuard.StoppedForSite = false;
            try
            {
                await runApp();
            }
            catch (Exception ex) when (Replaced(ex) is { } replaced)
            {
                log.LogWarning("{Why}", replaced.Message);
                continue;
            }

            if (!SiteGuard.StoppedForSite)
            {
                return 0;
            }
        }
    }

    /// <summary>ABP wraps what a module throws while it starts.</summary>
    public static SiteReplacedException? Replaced(Exception? ex)
    {
        for (; ex is not null; ex = ex.InnerException)
        {
            if (ex is SiteReplacedException replaced)
            {
                return replaced;
            }
        }

        return null;
    }
}
