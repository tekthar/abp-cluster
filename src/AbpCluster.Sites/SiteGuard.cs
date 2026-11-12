using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace AbpCluster.Sites;

/// <summary>
/// Work a host runs once after its site took over (a takeover or a switchover), on the first app start of that epoch:
/// check that the files the database names are in storage, say. The host keeps whether it ran for an epoch.
/// </summary>
public interface ISiteAfterTakeover
{
    /// <summary>Whether this already ran for <paramref name="epoch"/>.</summary>
    Task<bool> DoneForAsync(long epoch, CancellationToken cancellationToken);

    /// <summary>Runs once the app runs; it shouldn't hold the app's start.</summary>
    Task RunAsync(ActiveSiteDto active, CancellationToken cancellationToken);
}

/// <summary>
/// The running app's guard. At start (<see cref="CheckAtStartAsync"/>, which the host calls after its migrations and
/// before any worker runs) it makes the sites' tables, claims a database no site has claimed, holds the site's app lock
/// shared, and stands down for a site that replaced this one. Then every few seconds (<see cref="SitesOptions.GuardSeconds"/>)
/// it stops the app when its database turns into a replica, names another site, or hands over, and stands down when
/// another site says, signed with the installation's key, that it's active at a higher epoch: it writes that down in its
/// own database, so it doesn't start as the active site again, and the runner starts the site host.
/// </summary>
public sealed class SiteGuard(
    IConfiguration configuration, SitesOptions options, ISiteSigningKey signingKey, IServiceProvider services,
    IHostApplicationLifetime lifetime, ILogger<SiteGuard> logger) : BackgroundService
{
    private readonly string? _connectionString = options.ConnectionStringFrom(configuration);
    private readonly string? _site = options.Name;
    private readonly SiteStore _store = new(options);
    private readonly HashSet<string> _unsigned = new(StringComparer.Ordinal);
    private ActiveSiteDto? _tookOver;

    // Held for the app's life: a hand-over writes its last row only once no app of the site holds it.
    private NpgsqlConnection? _appLock;

    /// <summary>The app stopped because of its site: the runner decides again what to run, in the same process.</summary>
    public static bool StoppedForSite { get; internal set; }

    /// <summary>Before any worker runs: claims the site, or throws <see cref="SiteReplacedException"/> after standing down.</summary>
    public async Task CheckAtStartAsync(CancellationToken cancellationToken)
    {
        if (_connectionString is null || _site is null)
        {
            return;
        }

        // Said now, while the site is active: the day it's a standby, its page would take no key.
        if (new BreakGlass(options.BreakGlassKeyHash).Unreadable)
        {
            logger.LogWarning("{Setting} isn't a SHA-256 in hex (64 hex characters): this server takes no break-glass key.", options.Setting("BreakGlassKeyHash"));
        }

        await using NpgsqlConnection connection = new(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await _store.EnsureTablesAsync(connection, cancellationToken);
        int? timeline = await SiteStore.TimelineAsync(connection, cancellationToken);
        ActiveSiteDto? active = await _store.ChangeAsync(connection, current => current is null
            ? new SiteChangePlan(_site, 1, timeline, DateTime.UtcNow, SiteReasons.FirstStart, null, null, null, _site)
            : null, options.OnTakeover, cancellationToken);
        if (active is null || active.Site != _site)
        {
            throw new SiteReplacedException($"The database names {active?.Site} as its active site, not {_site}.");
        }

        if (active.HandingTo is not null)
        {
            throw new SiteReplacedException($"{_site} hands over to {active.HandingTo}: its apps stay stopped.");
        }

        if (active.Reason is SiteReasons.Takeover or SiteReasons.Switchover)
        {
            _tookOver = active;
        }

        // Shared with the site's other apps; a hand-over takes it alone only when every app has let go of it. Not pooled:
        // a pooled connection goes back to its pool with its session's locks still held.
        _appLock = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(_connectionString) { Pooling = false }.ConnectionString);
        await _appLock.OpenAsync(cancellationToken);
        await using (NpgsqlCommand hold = new($"SELECT pg_advisory_lock_shared({_store.AppLock})", _appLock))
        {
            await hold.ExecuteNonQueryAsync(cancellationToken);
        }

        // Made here if it's new, so it reaches the other sites with the database before any of them needs it.
        byte[] key = await signingKey.KeyAsync(cancellationToken);
        if (await ReplacedByAsync(connection, active, key, cancellationToken) is { } other)
        {
            await StandDownAsync(connection, other, cancellationToken);
            throw new SiteReplacedException($"{other.Site} is the active site at epoch {other.Epoch}: {_site} stood down.");
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_connectionString is null)
        {
            return;
        }

        if (_tookOver is { } active)
        {
            await AfterTakeoverAsync(active, stoppingToken);
        }

        TimeSpan interval = TimeSpan.FromSeconds(Math.Max(1, options.GuardSeconds));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, stoppingToken);
                if (await LookAsync(stoppingToken) is { } why)
                {
                    Stop(why);
                    return;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // The database or a site doesn't answer: the app reports the one where it needs it; the guard keeps going.
                logger.LogDebug(ex, "The site guard couldn't look this time.");
            }
        }
    }

    /// <summary>One look: why the app must stop now, or null.</summary>
    internal async Task<string?> LookAsync(CancellationToken cancellationToken)
    {
        if ((await SiteStore.ReadDatabaseAsync(_connectionString!, cancellationToken)).Replica)
        {
            return "The database is now a replica of another site's: the app stops, and the server runs as a standby.";
        }

        if (_site is null)
        {
            return null;
        }

        await using NpgsqlConnection connection = new(_connectionString);
        await connection.OpenAsync(cancellationToken);
        ActiveSiteDto? active = await _store.ReadActiveAsync(connection, cancellationToken);
        if (active is null)
        {
            return null;
        }

        if (active.Site != _site)
        {
            return $"The database names {active.Site} as its active site now: the app stops.";
        }

        if (active.HandingTo is not null)
        {
            return $"{_site} hands over to {active.HandingTo}: the app stops, so nothing written here is left behind.";
        }

        byte[] key = await signingKey.KeyAsync(cancellationToken);
        if (await ReplacedByAsync(connection, active, key, cancellationToken) is { } other)
        {
            await StandDownAsync(connection, other, cancellationToken);
            return $"{other.Site} is the active site at epoch {other.Epoch}: {_site} stood down, and the app stops.";
        }

        return null;
    }

    private async Task AfterTakeoverAsync(ActiveSiteDto active, CancellationToken cancellationToken)
    {
        foreach (ISiteAfterTakeover step in services.GetServices<ISiteAfterTakeover>())
        {
            try
            {
                if (!await step.DoneForAsync(active.Epoch, cancellationToken))
                {
                    _ = Task.Run(() => step.RunAsync(active, cancellationToken), cancellationToken);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Work after the takeover at epoch {Epoch} couldn't start.", active.Epoch);
            }
        }
    }

    private void Stop(string why)
    {
        logger.LogCritical("{Why}", why);
        StoppedForSite = true;
        lifetime.StopApplication();
    }

    /// <summary>Lets go of the app's shared lock as the last thing the app does, when its host is disposed.</summary>
    public override void Dispose()
    {
        _appLock?.Dispose();
        base.Dispose();
    }

    /// <summary>Another site active at a higher epoch than this one, saying so signed with the installation's key.</summary>
    private async Task<ActiveSiteDto?> ReplacedByAsync(NpgsqlConnection connection, ActiveSiteDto mine, byte[] key, CancellationToken cancellationToken)
    {
        List<SiteRecord> others = [.. (await _store.SitesAsync(connection, cancellationToken)).Where(s => s.Name != _site && s.Address.Length > 0)];
        foreach ((SiteStateDto? state, string? _) in await Task.WhenAll(others.Select(s => SiteStatusClient.ReadAsync(s.Address, options.StatePath, cancellationToken))))
        {
            if (state is not { State: SiteStates.Active, Active: { } active } || active.Site != state.Site || active.Epoch <= mine.Epoch)
            {
                continue;
            }

            if (SiteSignature.Verify(state, key, DateTimeOffset.UtcNow))
            {
                return active;
            }

            if (_unsigned.Add($"{active.Site}:{active.Epoch}"))
            {
                logger.LogWarning(
                    "{Other} says it's the active site at epoch {Epoch}, above this site's {Mine}, but without a valid signature: {Site} doesn't stand down for it.",
                    active.Site, active.Epoch, mine.Epoch, _site);
            }
        }

        return null;
    }

    /// <summary>Writes down, in this site's own database, that the other site replaced it, since when it's active.</summary>
    private async Task StandDownAsync(NpgsqlConnection connection, ActiveSiteDto other, CancellationToken cancellationToken) =>
        await _store.ChangeAsync(connection, current => current is not null && current.Site == _site
            ? new SiteChangePlan(other.Site, other.Epoch, current.Timeline, other.Since.UtcDateTime, SiteReasons.StoodDown, _site, null,
                $"{other.Site} said it was active at epoch {other.Epoch}, above {_site}'s {current.Epoch}.", _site!)
            : null, [], cancellationToken);
}

/// <summary>The site stood down, or its database names another site: the app doesn't start, and the runner runs the site host.</summary>
public sealed class SiteReplacedException(string message) : Exception(message);
