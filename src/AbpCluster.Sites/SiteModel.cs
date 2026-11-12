namespace AbpCluster.Sites;

/// <summary>The states a site can be in, as its state path (<see cref="SitesOptions.StatePath"/>) says them.</summary>
public static class SiteStates
{
    /// <summary>Its database is writable: it serves people.</summary>
    public const string Active = "active";

    /// <summary>Its database is a replica: it serves no one until the database is promoted.</summary>
    public const string Standby = "standby";

    /// <summary>Another site took over from it: it stood down, and serves no one until its database is rebuilt as a replica.</summary>
    public const string Replaced = "replaced";

    /// <summary>It hands over to another site in a planned switchover: its apps stopped, it serves no one.</summary>
    public const string HandingOver = "handing-over";

    /// <summary>
    /// Its database is writable but names another site as active, on the same timeline: it's that site's database (or a
    /// promotion the server can't see), so this site's servers won't write to it.
    /// </summary>
    public const string Foreign = "foreign";

    /// <summary>Its database can't be reached.</summary>
    public const string Unreachable = "unreachable";
}

/// <summary>Why the active site changed.</summary>
public static class SiteReasons
{
    /// <summary>The first server of a named site claimed a database no site had claimed.</summary>
    public const string FirstStart = "first-start";

    /// <summary>A site's database was promoted and its servers took over.</summary>
    public const string Takeover = "takeover";

    /// <summary>A site found another active with a higher epoch, and wrote down that it was replaced.</summary>
    public const string StoodDown = "stood-down";

    /// <summary>A site took over from one that handed over to it: a planned switchover, nothing lost.</summary>
    public const string Switchover = "switchover";

    /// <summary>The active site began handing over to another (history only).</summary>
    public const string HandoverStarted = "handover";

    /// <summary>A hand-over was called off before the other site took over (history only).</summary>
    public const string HandoverCancelled = "handover-cancelled";
}

/// <summary>The phases of a planned switchover.</summary>
public static class HandoverPhases
{
    /// <summary>The site's servers are stopping their apps; nothing is handed over yet.</summary>
    public const string Draining = "draining";

    /// <summary>No app of the site runs, the last write is made: the other site may take over.</summary>
    public const string Ready = "ready";
}

/// <summary>
/// A site's database: a replica or not, and, on a replica, when the last change it replayed was made at the active
/// site, how many bytes it has received but not replayed yet, and the positions it has received and replayed to.
/// </summary>
public sealed record SiteDatabaseDto(bool Replica, DateTimeOffset? LastReplayedAt, long? UnreplayedBytes, string? ReceivedLsn, string? ReplayedLsn);

/// <summary>
/// The active site: its name, epoch (a number that grows at each change), the database timeline it became active on,
/// since when and why; and, while it hands over in a planned switchover, to which site, in which phase, since when.
/// </summary>
public sealed record ActiveSiteDto(
    string Site, long Epoch, int? Timeline, DateTimeOffset Since, string Reason,
    string? HandingTo = null, string? HandingPhase = null, DateTimeOffset? HandingSince = null);

/// <summary>
/// A site's state: its name, <see cref="SiteStates"/>, its database's, which site its database says is active, and, on
/// the active site, its signature. While it hands over, the site it hands over to as it last said; and a fingerprint of
/// the settings every site must share.
/// </summary>
public sealed record SiteStateDto(
    string? Site, string State, SiteDatabaseDto? Database, ActiveSiteDto? Active = null, DateTimeOffset? SignedAt = null, string? Signature = null,
    SitePeerDto? Peer = null, string? Settings = null);

/// <summary>While a site hands over, the site it hands over to, as it last said: shown, never acted on.</summary>
public sealed record SitePeerDto(string Site, string? State, long? Epoch);

/// <summary>Whether this server can serve: <c>Ready</c>, or <c>DatabaseUnreachable</c> with the reason.</summary>
public sealed record ReadinessDto(string Status, string? Reason);

/// <summary>One site: its name (what its servers' <see cref="SitesOptions.Name"/> says), a label, and the address the others read its state at.</summary>
public sealed record SiteRecord(string Name, string Label, string Address);

/// <summary>A change of the active site, as the history keeps it.</summary>
public sealed record SiteChangeDto(long Epoch, string Site, string? PreviousSite, int? Timeline, string Reason, long? KeyGap, DateTime At, string? Note);

/// <summary>
/// A site as an admin sees it: its record, whether it's this server's, its state as read now (or why it couldn't be),
/// how many bytes a standby of this database is behind it, and whether its settings match this one's.
/// </summary>
public sealed record SiteDto(
    string Name, string Label, string Address, bool IsThis, SiteStateDto? Live, string? Error, long? Behind = null, bool? SettingsMatch = null);

/// <summary>
/// The sites at a glance: this server's site, which is active, each site, the latest changes, the key gap, whether a
/// break-glass key is set (and set but unreadable), and how far behind a standby may be.
/// </summary>
public sealed record SitesOverviewDto(
    string? ThisSite, ActiveSiteDto? Active, List<SiteDto> Sites, List<SiteChangeDto> Changes, long KeyGap, bool BreakGlassSet,
    long LagWarnBytes, bool BreakGlassUnreadable = false);

/// <summary>A site added: its name, a label, and the address the other sites read its state at.</summary>
public sealed record SiteInput(string Name, string? Label, string? Address);

/// <summary>A site's label and address changed.</summary>
public sealed record SiteUpdateInput(string? Label, string? Address);

/// <summary>A change of the active site to write: who's active from now, at which epoch and timeline, why, and the site writing it.</summary>
internal sealed record SiteChangePlan(
    string Site, long Epoch, int? Timeline, DateTime Since, string Reason, string? PreviousSite, long? KeyGap, string? Note, string Self, bool Switchover = false);
