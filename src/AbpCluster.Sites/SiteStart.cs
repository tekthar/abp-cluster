using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace AbpCluster.Sites;

/// <summary>What a server runs: the app, or the site host as a standby, a replaced site, a site handing over, or a server on another site's database.</summary>
public enum SiteMode
{
    App,
    Standby,
    Replaced,
    Foreign,
    HandingOver,
}

/// <summary>What a server decided to run, with its database's state and which site that database says is active.</summary>
public sealed record SiteDecision(SiteMode Mode, SiteDatabaseDto? Database, ActiveSiteDto? Active);

/// <summary>
/// The first thing a server does, before anything touches its database: decide what to run, and take over a database
/// that was promoted.
/// <list type="bullet">
/// <item>No database, or one that doesn't answer, or no site name: the app (it reports a database that doesn't answer as it always has).</item>
/// <item>A replica: the standby.</item>
/// <item>A database that names this site, or no site yet: the app (which claims it at start).</item>
/// <item>A database that names another site but is on a newer timeline than the one that site became active on: it was
/// promoted, so this site takes over (a new epoch, and <see cref="SitesOptions.OnTakeover"/> in the same transaction),
/// then the app.</item>
/// <item>Otherwise a site that stood down, or a server on another site's database, which writes nothing to it.
/// <see cref="SitesOptions.ForceTakeover"/> takes over anyway, for a promotion a managed database doesn't show.</item>
/// </list>
/// </summary>
public static class SiteStart
{
    /// <summary>Said in the history when this server's standby page promoted the database; read by the takeover.</summary>
    internal static string? PromotionNote { get; set; }

    /// <summary>
    /// Decides; with <paramref name="act"/>, also takes over a promoted database (in one transaction under a lock: of a
    /// site's servers starting together, one takes over and the others find it done).
    /// </summary>
    public static async Task<SiteDecision> DecideAsync(
        IConfiguration configuration, SitesOptions options, bool act, ILogger logger, CancellationToken cancellationToken)
    {
        if (options.ConnectionStringFrom(configuration) is not { Length: > 0 } connectionString)
        {
            return new SiteDecision(SiteMode.App, null, null);
        }

        SiteDatabaseDto database;
        try
        {
            database = await SiteStore.ReadDatabaseAsync(connectionString, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return new SiteDecision(SiteMode.App, null, null);
        }

        SiteStore store = new(options);
        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken);
        ActiveSiteDto? active = await store.ReadActiveAsync(connection, cancellationToken);
        if (database.Replica)
        {
            return new SiteDecision(SiteMode.Standby, database, active);
        }

        string? site = options.Name;
        if (site is not null && active is not null && active.Site == site && active.HandingTo is not null)
        {
            // A planned switchover under way, or done: the site's apps stay stopped.
            return new SiteDecision(SiteMode.HandingOver, database, active);
        }

        if (site is null || active is null || active.Site == site)
        {
            return new SiteDecision(SiteMode.App, database, active);
        }

        int? timeline = await SiteStore.TimelineAsync(connection, cancellationToken);
        bool promoted = timeline is { } now && active.Timeline is { } then && now > then;
        if (!promoted && !options.ForceTakeover)
        {
            return new SiteDecision(active.Reason == SiteReasons.StoodDown ? SiteMode.Replaced : SiteMode.Foreign, database, active);
        }

        if (!act)
        {
            return new SiteDecision(SiteMode.App, database, active);
        }

        // A site that handed over to this one wrote nothing after: a switchover loses nothing, so no key gap.
        bool handedOver = active.HandingTo == site && active.HandingPhase == HandoverPhases.Ready;
        long gap = handedOver ? 0 : Math.Max(0, options.KeyGap);
        string note = (handedOver ? $"{active.Site} handed over to {site}. " : "")
            + (PromotionNote ?? (promoted ? $"Its database was promoted (timeline {active.Timeline} to {timeline})." : "Sites:ForceTakeover was set."));
        bool tookOver = false;
        ActiveSiteDto? after = await store.ChangeAsync(connection, current =>
        {
            if (current is null || current.Site == site)
            {
                return null;
            }

            tookOver = true;
            return new SiteChangePlan(site, current.Epoch + 1, timeline, DateTime.UtcNow,
                handedOver ? SiteReasons.Switchover : SiteReasons.Takeover, current.Site, gap > 0 ? gap : null, note, site, handedOver);
        }, options.OnTakeover, cancellationToken);
        if (tookOver)
        {
            logger.LogWarning(
                "{Site} took over from {Previous} ({How}): epoch {Epoch}, timeline {Timeline}, key gap {Gap}.",
                site, active.Site, handedOver ? "a planned switchover" : "a takeover", after?.Epoch, timeline, gap);
        }

        return new SiteDecision(SiteMode.App, database, after);
    }

    /// <summary>The state an app server says, from its database and which site it names: active only while it names this one.</summary>
    internal static string StateOf(SiteDatabaseDto database, ActiveSiteDto? active, string? site) =>
        database.Replica ? SiteStates.Standby
        : site is null || active is null ? SiteStates.Active
        : active.Site == site ? active.HandingTo is null ? SiteStates.Active : SiteStates.HandingOver
        : active.Reason == SiteReasons.StoodDown ? SiteStates.Replaced
        : SiteStates.Foreign;
}
