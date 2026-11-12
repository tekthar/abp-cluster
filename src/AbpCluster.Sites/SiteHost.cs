using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace AbpCluster.Sites;

/// <summary>
/// What a server runs while its site isn't active: as a standby (its database a replica), a replaced site (it stood down
/// for another), a site handing over, or a server on another site's database. It serves its health, its readiness and
/// the site's state, and nothing that writes or calls out: no migrations, seeders, workers or outbox. Every other request
/// is told the site isn't active. Operators promote a standby's database with the break-glass key, call a hand-over off,
/// and read what changed on a replaced site after the other took over (when the host says how).
/// </summary>
public static class SiteHost
{
    /// <summary>Runs the site host until what the server should run changes (true), or until the server is stopped (false).</summary>
    public static async Task<bool> RunAsync(string[] args, SiteDecision decision, Func<IConfiguration, SitesOptions> options)
    {
        (WebApplication app, SiteWatch watch) = Build(args, decision, options);
        await using (app)
        {
            await app.RunAsync();
        }

        return watch.Changed;
    }

    /// <summary>The site host, built and not started, with its watch.</summary>
    public static (WebApplication App, SiteWatch Watch) Build(string[] args, SiteDecision decision, Func<IConfiguration, SitesOptions> optionsOf)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
        SitesOptions first = optionsOf(builder.Configuration);
        first.ConfigureHostBuilder?.Invoke(builder);
        IConfiguration configuration = builder.Configuration;
        SitesOptions options = optionsOf(configuration);
        options.ConfigureHostBuilder = first.ConfigureHostBuilder;
        options.ConfigureHost ??= first.ConfigureHost;
        string connectionString = options.ConnectionStringFrom(configuration)!;
        string? site = options.Name;
        SiteStore store = new(options);
        builder.Services.AddSingleton(services => new SiteWatch(
            configuration, options, decision,
            services.GetRequiredService<IHostApplicationLifetime>(), services.GetRequiredService<ILogger<SiteWatch>>()));
        builder.Services.AddHostedService(services => services.GetRequiredService<SiteWatch>());

        WebApplication app = builder.Build();
        options.ConfigureHost?.Invoke(app);
        BreakGlass breakGlass = new(options.BreakGlassKeyHash);
        if (breakGlass.Unreadable)
        {
            app.Logger.LogWarning("{Setting} isn't a SHA-256 in hex (64 hex characters): this server takes no break-glass key.", options.Setting("BreakGlassKeyHash"));
        }

        string settings = SiteFingerprint.Of(configuration, options);
        string statePath = "/" + options.StatePath.Trim('/');
        static string State(SiteWatch watch) => watch.Database is null ? SiteStates.Unreachable : watch.Decision.Mode switch
        {
            SiteMode.Standby => SiteStates.Standby,
            SiteMode.Replaced => SiteStates.Replaced,
            SiteMode.HandingOver => SiteStates.HandingOver,
            _ => SiteStates.Foreign,
        };
        IResult Error(int status, string code, string message) => Results.Json(new { error = new { code = options.Code(code), message } }, statusCode: status);
        IResult? Refused(string? key, HttpContext http) => breakGlass.Check(key, http.Connection.RemoteIpAddress?.ToString()) switch
        {
            null => null,
            BreakGlassRefusal.NoKey => Error(StatusCodes.Status403Forbidden, "NoBreakGlass",
                $"This server has no break-glass key: set {options.Setting("BreakGlassKeyHash")} to the SHA-256 of one, in hex."),
            BreakGlassRefusal.TooMany => Error(StatusCodes.Status429TooManyRequests, "TooManyKeys", "Too many wrong keys: wait a quarter of an hour."),
            _ => Error(StatusCodes.Status403Forbidden, "WrongBreakGlass", "That isn't this site's break-glass key."),
        };

        // The health check stays the liveness probe: the process is alive.
        app.MapGet(options.HealthPath, () => Results.Json(new { status = "Healthy", checks = Array.Empty<object>() }));
        // Ready while the database answers, so the site's web reaches this server and shows what it is.
        app.MapGet(options.ReadyPath, (SiteWatch watch) => watch.Database is null
            ? Results.Json(new ReadinessDto("DatabaseUnreachable", watch.Error), statusCode: StatusCodes.Status503ServiceUnavailable)
            : Results.Json(new ReadinessDto("Ready", null)));
        // Never 200: a global balancer sends no one here, and no site stands down for it.
        app.MapGet(statePath, (SiteWatch watch) => Results.Json(
            new SiteStateDto(site, State(watch), watch.Database, watch.Decision.Active, Peer: watch.Peer, Settings: settings),
            statusCode: StatusCodes.Status503ServiceUnavailable));

        // A hand-over called off from the site handing over, with the break-glass key, while the other hasn't taken over.
        app.MapPost(statePath + "/cancel-handover", async (CancelHandoverInput input, SiteWatch watch, HttpContext http, CancellationToken cancellationToken) =>
        {
            if (watch.Decision.Mode != SiteMode.HandingOver)
            {
                return Error(StatusCodes.Status409Conflict, "NotHandingOver", "This site isn't handing over.");
            }

            if (Refused(input.Key, http) is { } refused)
            {
                return refused;
            }

            if (watch.Peer is { State: SiteStates.Active } peer && peer.Epoch > watch.Decision.Active?.Epoch)
            {
                return Error(StatusCodes.Status409Conflict, "TakenOver", $"{peer.Site} has taken over: the hand-over can't be called off now.");
            }

            // This site turns active again: safe only while the other is known not to have taken over. One that answers as
            // a standby says so; one that can't be read may have, so the operators check it themselves and type its name.
            string to = watch.Decision.Active?.HandingTo ?? "";
            if (watch.Peer?.State != SiteStates.Standby && !string.Equals(input.Confirm?.Trim(), to, StringComparison.Ordinal))
            {
                return Error(StatusCodes.Status409Conflict, "ConfirmNotServing",
                    $"{to} can't be read here as a standby, so this site can't tell whether it took over; calling it off while {to} serves leaves two active sites. Check {to} yourself (its own page, or SELECT pg_is_in_recovery() on its database: true until it's promoted), then type its name to call it off.");
            }

            await using NpgsqlConnection connection = new(connectionString);
            await connection.OpenAsync(cancellationToken);
            if (!await store.CancelHandoverAsync(connection, site!, cancellationToken))
            {
                return Error(StatusCodes.Status409Conflict, "NotHandingOver", "This site isn't handing over.");
            }

            app.Logger.LogWarning("{Site}'s hand-over was called off with the break-glass key: it becomes the active site again.", site);
            return Results.Json(new { cancelled = true });
        });

        // The standby page's Promote: the break-glass key the operators hold, and the site's name typed to confirm.
        app.MapPost(statePath + "/promote", async (PromoteInput input, SiteWatch watch, HttpContext http, CancellationToken cancellationToken) =>
        {
            if (watch.Decision.Mode != SiteMode.Standby)
            {
                return Error(StatusCodes.Status409Conflict, "NotStandby", "Only a standby's database is promoted from here.");
            }

            if (Refused(input.Key, http) is { } refused)
            {
                return refused;
            }

            if (!string.Equals(input.Site?.Trim(), site, StringComparison.Ordinal))
            {
                return Error(StatusCodes.Status400BadRequest, "ConfirmSite", $"Type this site's name, {site}, to confirm.");
            }

            try
            {
                await using NpgsqlConnection connection = new(connectionString);
                await connection.OpenAsync(cancellationToken);
                await using NpgsqlCommand promote = new("SELECT pg_promote(true, 60)", connection) { CommandTimeout = 90 };
                if (await promote.ExecuteScalarAsync(cancellationToken) is not true)
                {
                    return Error(StatusCodes.Status504GatewayTimeout, "PromoteSlow", "The database didn't finish its promotion within a minute; this site takes over once it has.");
                }
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.InsufficientPrivilege)
            {
                return Error(StatusCodes.Status409Conflict, "PromoteNotAllowed",
                    "This server's database role may not promote it. Promote it with the database's own tooling (pg_ctl promote, CloudNativePG, Patroni, or the cloud's console), or let the role do it: GRANT EXECUTE ON FUNCTION pg_promote TO <role>. This site takes over by itself once its database is promoted.");
            }

            SiteStart.PromotionNote = $"Promoted from {site}'s standby page with the break-glass key.";
            app.Logger.LogWarning("{Site}'s database was promoted from its standby page with the break-glass key.", site);
            return Results.Json(new { promoted = true });
        });

        if (options.ChangesSince is { } changesSince)
        {
            // What changed on a replaced site after the other took over. A POST with the key in its body.
            app.MapPost(statePath + "/changes", async (BreakGlassInput input, SiteWatch watch, HttpContext http, CancellationToken cancellationToken) =>
            {
                if (watch.Decision is not { Mode: SiteMode.Replaced, Active: { } active })
                {
                    return Error(StatusCodes.Status409Conflict, "NotReplaced", "Only a replaced site lists what changed on it after it was replaced.");
                }

                if (Refused(input.Key, http) is { } refused)
                {
                    return refused;
                }

                await using NpgsqlConnection connection = new(connectionString);
                await connection.OpenAsync(cancellationToken);
                return Results.Json(await changesSince(connection, active, cancellationToken));
            });
        }

        app.MapFallback((SiteWatch watch) => Results.Json(
            new { error = new { code = options.Code("Standby"), message = watch.Decision.Mode switch
            {
                SiteMode.Standby => "This site is a standby: it serves no one until its database is promoted.",
                SiteMode.Replaced => $"This site was replaced by {watch.Decision.Active?.Site}: it serves no one.",
                SiteMode.HandingOver => $"This site hands over to {watch.Decision.Active?.HandingTo}: it serves no one.",
                _ => $"This server's database is {watch.Decision.Active?.Site}'s: it serves no one from it.",
            } } },
            statusCode: StatusCodes.Status503ServiceUnavailable));

        // Each message names the site and the other site once, in that order: a name said twice would need its value twice.
        string? other = decision.Mode == SiteMode.HandingOver ? decision.Active?.HandingTo : decision.Active?.Site;
        app.Logger.LogWarning(decision.Mode switch
        {
            SiteMode.Standby => "The database is a replica: this server runs as {Site}'s standby, and starts the app when the database is promoted ({Other} is active).",
            SiteMode.Replaced => "{Site} was replaced by {Other}: this server serves no one until its database is rebuilt as the active site's replica.",
            SiteMode.HandingOver => "{Site} hands over to {Other}: its apps stay stopped, and it serves no one.",
            _ => "{Site} won't write to this database: it names {Other} as its active site, on the same timeline.",
        }, site ?? "the site", other);
        return (app, app.Services.GetRequiredService<SiteWatch>());
    }
}

/// <summary>The standby page's Promote: the break-glass key, and the site's name typed to confirm.</summary>
public sealed record PromoteInput(string? Key, string? Site);

/// <summary>The break-glass key, for what a replaced site lists.</summary>
public sealed record BreakGlassInput(string? Key);

/// <summary>A hand-over called off: the break-glass key, and the other site's name when it can't be read as a standby.</summary>
public sealed record CancelHandoverInput(string? Key, string? Confirm);

/// <summary>
/// The site host's watch: what the site's state reports, and the moment the server should run something else (its
/// database promoted, rebuilt as a replica, or naming this site again), when it stops the host so the runner decides
/// again. In a planned switchover it does two things more: on the site handing over, the hand-over's last write once no
/// app of the site runs; on the standby handed over to, the promotion of its database where its role may.
/// </summary>
public sealed class SiteWatch(
    IConfiguration configuration, SitesOptions options, SiteDecision first,
    IHostApplicationLifetime lifetime, ILogger<SiteWatch> logger) : BackgroundService
{
    private volatile SiteDecision _decision = first;
    private volatile string? _error;
    private volatile bool _reachable = true;
    private volatile SitePeerDto? _peer;
    private bool _promotionTried;
    private readonly SiteStore _store = new(options);
    private readonly string _connectionString = options.ConnectionStringFrom(configuration)!;

    public SiteDecision Decision => _decision;

    /// <summary>The database as last read; null while it can't be reached.</summary>
    public SiteDatabaseDto? Database => _reachable ? _decision.Database : null;

    public string? Error => _error;

    /// <summary>While this site hands over, the site it hands over to, as it last said.</summary>
    public SitePeerDto? Peer => _peer;

    /// <summary>What the server should run changed: the runner decides again once the host has stopped.</summary>
    public bool Changed { get; private set; }

    private async Task HandOverAsync(ActiveSiteDto active, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = new(_connectionString);
        await connection.OpenAsync(cancellationToken);
        if (active.HandingPhase == HandoverPhases.Draining && await _store.ReadyHandoverAsync(connection, cancellationToken))
        {
            logger.LogWarning("No app of {Site} runs any more: the hand-over to {To} is ready, for it to take over.", options.Name, active.HandingTo);
        }

        SiteRecord? to = (await _store.SitesAsync(connection, cancellationToken)).FirstOrDefault(s => s.Name == active.HandingTo);
        (SiteStateDto? state, string? _) = to is { Address.Length: > 0 } ? await SiteStatusClient.ReadAsync(to.Address, options.StatePath, cancellationToken) : (null, null);
        _peer = new SitePeerDto(active.HandingTo!, state?.State, state?.State == SiteStates.Active ? state.Active?.Epoch : null);
    }

    private async Task PromoteForHandoverAsync(CancellationToken cancellationToken)
    {
        _promotionTried = true;
        try
        {
            await using NpgsqlConnection connection = new(_connectionString);
            await connection.OpenAsync(cancellationToken);
            await using NpgsqlCommand promote = new("SELECT pg_promote(true, 60)", connection) { CommandTimeout = 90 };
            await promote.ExecuteScalarAsync(cancellationToken);
            logger.LogWarning("{Site} was handed over to: it promoted its database, and takes over.", options.Name);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.InsufficientPrivilege)
        {
            logger.LogWarning("{Site} was handed over to, but its database role may not promote it: promote it with its own tooling, or from the standby page.", options.Name);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        TimeSpan interval = TimeSpan.FromSeconds(Math.Max(1, options.WatchSeconds));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, stoppingToken);
                SiteDecision now = await SiteStart.DecideAsync(configuration, options, act: false, logger, stoppingToken);
                if (now.Database is null)
                {
                    throw new InvalidOperationException("The database doesn't answer.");
                }

                if (!_reachable)
                {
                    logger.LogInformation("The database answers again.");
                }

                _reachable = true;
                _error = null;
                if (now is { Mode: SiteMode.HandingOver, Active: { } handing })
                {
                    await HandOverAsync(handing, stoppingToken);
                }
                else if (now is { Mode: SiteMode.Standby, Active: { HandingPhase: HandoverPhases.Ready } handed } && handed.HandingTo == options.Name
                         && !_promotionTried && options.PromoteOnHandover)
                {
                    await PromoteForHandoverAsync(stoppingToken);
                }

                if (now.Mode != _decision.Mode)
                {
                    logger.LogWarning("This server runs {Mode} now, not {Before}: the site host stops.", now.Mode, _decision.Mode);
                    _decision = now;
                    Changed = true;
                    lifetime.StopApplication();
                    return;
                }

                _decision = now;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                if (_reachable)
                {
                    logger.LogWarning(ex, "The database can't be reached; the site host keeps trying.");
                }

                _reachable = false;
                _error = ex.Message;
            }
        }
    }
}
