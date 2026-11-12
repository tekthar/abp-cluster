using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AbpCluster.Sites;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace AbpCluster.Tests;

/// <summary>Two sites: what needs no database.</summary>
public sealed class SitesUnitTests
{
    private static readonly byte[] Key = RandomNumberGenerator.GetBytes(32);

    private static SiteStateDto Hq(long epoch = 3) =>
        new("hq", SiteStates.Active, null, new ActiveSiteDto("hq", epoch, 2, DateTimeOffset.UtcNow.AddHours(-1), SiteReasons.Takeover));

    [Fact]
    public void A_signed_state_checks_only_with_the_key_while_it_is_fresh()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        SiteStateDto signed = SiteSignature.Sign(Hq(), Key, now);

        Assert.True(SiteSignature.Verify(signed, Key, now.AddSeconds(30)));
        Assert.False(SiteSignature.Verify(signed, RandomNumberGenerator.GetBytes(32), now), "another installation's key");
        Assert.False(SiteSignature.Verify(signed, Key, now.AddMinutes(3)), "too old");
        Assert.False(SiteSignature.Verify(signed with { Active = signed.Active! with { Epoch = 9 } }, Key, now), "a higher epoch it never signed");
        Assert.False(SiteSignature.Verify(Hq(), Key, now), "not signed at all");
    }

    [Theory]
    [InlineData("ABCDEF0123456789abcdef0123456789ABCDEF0123456789abcdef0123456789", true)]
    [InlineData("abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789  -", true)]
    [InlineData("\"abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789\"", true)]
    [InlineData("abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789  key.txt", true)]
    [InlineData("not-a-hash", false)]
    [InlineData("abcdef0123456789", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void A_break_glass_hash_is_taken_as_people_write_it(string? value, bool readable) =>
        Assert.Equal(readable, BreakGlassKeyHash.Parse(value) is { Length: 32 });

    [Fact]
    public void Break_glass_takes_the_key_and_makes_an_address_wait_after_five_wrong_ones()
    {
        string hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("open sesame")));
        BreakGlass glass = new(hash);

        Assert.Null(glass.Check("open sesame", "10.0.0.1"));
        for (int i = 0; i < 5; i++)
        {
            Assert.Equal(BreakGlassRefusal.Wrong, glass.Check("guess", "10.0.0.2"));
        }

        Assert.Equal(BreakGlassRefusal.TooMany, glass.Check("open sesame", "10.0.0.2"));
        Assert.Null(glass.Check("open sesame", "10.0.0.3"));
        Assert.Equal(BreakGlassRefusal.NoKey, new BreakGlass(null).Check("open sesame", "10.0.0.1"));
        Assert.True(new BreakGlass("not-a-hash").Unreadable);
    }

    [Fact]
    public void The_fingerprint_says_whether_shared_settings_match_and_shows_no_secret()
    {
        SitesOptions options = new() { SharedSettings = ["Server:PublicUrl", "Calendar:TimeZone"], SharedSecrets = ["Oidc:ClientSecret"] };
        IConfiguration Config(string url, string secret) => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Server:PublicUrl"] = url, ["Calendar:TimeZone"] = "Asia/Riyadh", ["Oidc:ClientSecret"] = secret,
        }).Build();

        string hq = SiteFingerprint.Of(Config("https://work.example/", "s3cret"), options);
        Assert.Equal(hq, SiteFingerprint.Of(Config("https://work.example", "s3cret"), options));
        Assert.NotEqual(hq, SiteFingerprint.Of(Config("https://dr.example", "s3cret"), options));
        Assert.NotEqual(hq, SiteFingerprint.Of(Config("https://work.example", "other"), options));
        Assert.DoesNotContain("s3cret", hq, StringComparison.Ordinal);
        Assert.Equal("own", SiteFingerprint.Of(Config("x", "y"), new SitesOptions { Fingerprint = _ => "own" }));
    }

    [Fact]
    public void Options_come_from_the_section_a_host_names()
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Site:Name"] = " dr ", ["Site:KeyGap"] = "5000", ["Site:GuardSeconds"] = "3", ["Site:ForceTakeover"] = "true",
            ["Site:SharedSettings:0"] = "Server:PublicUrl", ["ConnectionStrings:Default"] = "Host=db",
        }).Build();

        SitesOptions options = SitesOptions.From(configuration, "Site");
        Assert.Equal(("dr", 5000L, 3, true), (options.Name, options.KeyGap, options.GuardSeconds, options.ForceTakeover));
        Assert.Equal(["Server:PublicUrl"], options.SharedSettings);
        Assert.Equal(("Site", "Site:BreakGlassKeyHash"), (options.Section, options.Setting("BreakGlassKeyHash")));
        Assert.Equal("Sites:Name", SitesOptions.From(configuration).Setting("Name"));
        Assert.Equal("Host=db", options.ConnectionStringFrom(configuration));
        Assert.Null(SitesOptions.From(new ConfigurationBuilder().Build()).Name);
        Assert.Throws<InvalidOperationException>(() => new SiteStore(new SitesOptions { Schema = "x; DROP TABLE y" }));
    }
}

/// <summary>
/// Two sites on a real PostgreSQL (as <see cref="ClusterLocksTests"/>): each test has a schema of its own, made and
/// dropped. A replica and its promotion need a second server, so takeovers here go by <see cref="SitesOptions.ForceTakeover"/>.
/// </summary>
public sealed class SitesDatabaseTests : IAsyncLifetime
{
    private readonly string _schema = $"sites_test_{Guid.NewGuid():N}"[..30];

    private SitesOptions Options(string? site = "hq", Action<SitesOptions>? more = null)
    {
        SitesOptions options = new() { Name = site, Schema = _schema, ConnectionString = ClusterLocksTests.Server, LockPrefix = _schema + ":" };
        more?.Invoke(options);
        return options;
    }

    private static IConfiguration Empty => new ConfigurationBuilder().Build();

    public async Task InitializeAsync()
    {
        await using NpgsqlConnection connection = await OpenAsync();
        await new SiteStore(Options()).EnsureTablesAsync(connection, default);
    }

    public async Task DisposeAsync()
    {
        await using NpgsqlConnection connection = await OpenAsync();
        await using NpgsqlCommand drop = new($"DROP SCHEMA IF EXISTS \"{_schema}\" CASCADE", connection);
        await drop.ExecuteNonQueryAsync();
    }

    private static async Task<NpgsqlConnection> OpenAsync()
    {
        NpgsqlConnection connection = new(ClusterLocksTests.Server);
        await connection.OpenAsync();
        return connection;
    }

    private async Task ExecAsync(string sql)
    {
        await using NpgsqlConnection connection = await OpenAsync();
        await using NpgsqlCommand command = new(sql.Replace("{s}", $"\"{_schema}\"", StringComparison.Ordinal), connection);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<ActiveSiteDto?> ActiveAsync()
    {
        await using NpgsqlConnection connection = await OpenAsync();
        return await new SiteStore(Options()).ReadActiveAsync(connection, default);
    }

    private SiteGuard Guard(SitesOptions options) => new(
        Empty, options, new DatabaseSiteSigningKey(Empty, options), new ServiceCollection().BuildServiceProvider(),
        new Lifetime(), NullLogger<SiteGuard>.Instance);

    /// <summary>An app's start on its site: the guard's check, its shared app lock let go of after.</summary>
    private async Task ClaimAsync(SitesOptions options)
    {
        using SiteGuard guard = Guard(options);
        await guard.CheckAtStartAsync(default);
    }

    [Fact]
    public async Task Tables_are_made_once_and_left_as_they_are()
    {
        await using NpgsqlConnection connection = await OpenAsync();
        await new SiteStore(Options()).EnsureTablesAsync(connection, default);
        Assert.Null(await ActiveAsync());
        Assert.Empty(await new SiteStore(Options()).SitesAsync(connection, default));
    }

    [Fact]
    public async Task The_first_server_claims_the_database_for_its_site_and_others_find_it_claimed()
    {
        await ClaimAsync(Options("hq"));
        await ClaimAsync(Options("hq"));

        ActiveSiteDto active = (await ActiveAsync())!;
        Assert.Equal(("hq", 1L, SiteReasons.FirstStart), (active.Site, active.Epoch, active.Reason));
        Assert.Equal(SiteMode.App, (await SiteStart.DecideAsync(Empty, Options("hq"), act: true, NullLogger.Instance, default)).Mode);
        await Assert.ThrowsAsync<SiteReplacedException>(() => ClaimAsync(Options("dr")));

        await using NpgsqlConnection connection = await OpenAsync();
        Assert.Equal([new SiteRecord("hq", "hq", "")], await new SiteStore(Options()).SitesAsync(connection, default));
        Assert.Single(await new SiteStore(Options()).ChangesAsync(connection, 10, default));
    }

    [Fact]
    public async Task A_server_on_another_sites_database_writes_nothing_unless_it_takes_over()
    {
        await ClaimAsync(Options("hq"));
        Assert.Equal(SiteMode.Foreign, (await SiteStart.DecideAsync(Empty, Options("dr"), act: true, NullLogger.Instance, default)).Mode);

        List<SiteTakeover> told = [];
        SitesOptions dr = Options("dr", o =>
        {
            o.ForceTakeover = true;
            o.KeyGap = 500;
            o.OnTakeover.Add((_, _, takeover, _) =>
            {
                told.Add(takeover);
                return Task.CompletedTask;
            });
        });
        Assert.Equal(SiteMode.App, (await SiteStart.DecideAsync(Empty, dr, act: false, NullLogger.Instance, default)).Mode);
        Assert.Equal("hq", (await ActiveAsync())!.Site);

        SiteDecision decision = await SiteStart.DecideAsync(Empty, dr, act: true, NullLogger.Instance, default);
        Assert.Equal(SiteMode.App, decision.Mode);
        Assert.Equal(("dr", 2L, SiteReasons.Takeover), (decision.Active!.Site, decision.Active.Epoch, decision.Active.Reason));
        Assert.Equal([new SiteTakeover("dr", "hq", 2, 500, false)], told);

        await using NpgsqlConnection connection = await OpenAsync();
        Assert.Equal(500, (await new SiteStore(Options()).ChangesAsync(connection, 1, default))[0].KeyGap);
    }

    [Fact]
    public async Task A_takeover_step_that_throws_undoes_the_takeover()
    {
        await ClaimAsync(Options("hq"));
        SitesOptions dr = Options("dr", o =>
        {
            o.ForceTakeover = true;
            o.OnTakeover.Add(async (connection, transaction, _, cancellationToken) =>
            {
                await using NpgsqlCommand fail = new("SELECT 1/0", connection, transaction);
                await fail.ExecuteNonQueryAsync(cancellationToken);
            });
        });

        await Assert.ThrowsAsync<PostgresException>(() => SiteStart.DecideAsync(Empty, dr, act: true, NullLogger.Instance, default));
        Assert.Equal(("hq", 1L), ((await ActiveAsync())!.Site, (await ActiveAsync())!.Epoch));
    }

    [Fact]
    public async Task A_planned_switchover_waits_for_every_app_then_hands_over_with_no_gap()
    {
        await ClaimAsync(Options("hq"));
        SiteStore store = new(Options());
        await using NpgsqlConnection connection = await OpenAsync();
        Assert.True(await store.BeginHandoverAsync(connection, "hq", "dr", default));
        Assert.False(await store.BeginHandoverAsync(connection, "hq", "dr", default), "once");
        Assert.Equal(SiteMode.HandingOver, (await SiteStart.DecideAsync(Empty, Options("hq"), act: true, NullLogger.Instance, default)).Mode);
        await Assert.ThrowsAsync<SiteReplacedException>(() => ClaimAsync(Options("hq")));

        // An app of hq still runs: its shared lock keeps the hand-over from its last write.
        await using (NpgsqlConnection app = new(new NpgsqlConnectionStringBuilder(ClusterLocksTests.Server) { Pooling = false }.ConnectionString))
        {
            await app.OpenAsync();
            await using (NpgsqlCommand hold = new($"SELECT pg_advisory_lock_shared({Options().LockKey("site-app")})", app))
            {
                await hold.ExecuteNonQueryAsync();
            }

            Assert.False(await store.ReadyHandoverAsync(connection, default));
        }

        // PostgreSQL lets a closed session's locks go a moment after the client returns; the site host's watch tries
        // again every couple of seconds, so the test gives it as long.
        Assert.True(await EventuallyAsync(() => store.ReadyHandoverAsync(connection, default)));
        Assert.Equal(HandoverPhases.Ready, (await ActiveAsync())!.HandingPhase);

        List<SiteTakeover> told = [];
        SitesOptions dr = Options("dr", o =>
        {
            o.ForceTakeover = true;
            o.OnTakeover.Add((_, _, takeover, _) =>
            {
                told.Add(takeover);
                return Task.CompletedTask;
            });
        });
        SiteDecision decision = await SiteStart.DecideAsync(Empty, dr, act: true, NullLogger.Instance, default);
        Assert.Equal(("dr", 2L, SiteReasons.Switchover, (string?)null), (decision.Active!.Site, decision.Active.Epoch, decision.Active.Reason, decision.Active.HandingTo));
        Assert.Equal([new SiteTakeover("dr", "hq", 2, 0, true)], told);
    }

    [Fact]
    public async Task A_hand_over_called_off_leaves_the_site_active()
    {
        await ClaimAsync(Options("hq"));
        SiteStore store = new(Options());
        await using NpgsqlConnection connection = await OpenAsync();
        Assert.False(await store.CancelHandoverAsync(connection, "hq", default));
        await store.BeginHandoverAsync(connection, "hq", "dr", default);
        Assert.True(await store.CancelHandoverAsync(connection, "hq", default));
        Assert.Equal(SiteMode.App, (await SiteStart.DecideAsync(Empty, Options("hq"), act: true, NullLogger.Instance, default)).Mode);
        Assert.Equal([SiteReasons.HandoverCancelled, SiteReasons.HandoverStarted, SiteReasons.FirstStart],
            (await store.ChangesAsync(connection, 10, default)).Select(c => c.Reason));
    }

    [Fact]
    public async Task A_site_stands_down_for_another_signed_at_a_higher_epoch_and_not_for_an_unsigned_one()
    {
        SitesOptions hq = Options("hq");
        await ClaimAsync(hq);
        byte[] key = await new DatabaseSiteSigningKey(Empty, hq).KeyAsync(default);
        Assert.Equal(key, await new DatabaseSiteSigningKey(Empty, hq).KeyAsync(default));

        SiteStateDto dr = new("dr", SiteStates.Active, null, new ActiveSiteDto("dr", 2, 2, DateTimeOffset.UtcNow, SiteReasons.Takeover));
        await using (WebApplication other = await OtherSiteAsync(() => dr))
        {
            await SaveSiteAsync("dr", Address(other));
            Assert.Null(await Guard(hq).LookAsync(default));
        }

        await using (WebApplication other = await OtherSiteAsync(() => SiteSignature.Sign(dr, key, DateTimeOffset.UtcNow)))
        {
            await SaveSiteAsync("dr", Address(other));
            Assert.Contains("stood down", await Guard(hq).LookAsync(default), StringComparison.Ordinal);
        }

        ActiveSiteDto after = (await ActiveAsync())!;
        Assert.Equal(("dr", 2L, SiteReasons.StoodDown), (after.Site, after.Epoch, after.Reason));
        Assert.Equal(SiteMode.Replaced, (await SiteStart.DecideAsync(Empty, hq, act: true, NullLogger.Instance, default)).Mode);
    }

    [Fact]
    public async Task Admins_add_change_and_remove_sites_but_never_their_own_or_the_active_one()
    {
        SitesOptions hq = Options("hq");
        await ClaimAsync(hq);
        SitesAdmin admin = new(Empty, hq);

        SiteDto dr = await admin.AddAsync(new SiteInput(" DR ", "Disaster recovery", "http://dr.internal:8080/"), default);
        Assert.Equal(("dr", "Disaster recovery", "http://dr.internal:8080"), (dr.Name, dr.Label, dr.Address));
        await Assert.ThrowsAsync<Volo.Abp.UserFriendlyException>(() => admin.AddAsync(new SiteInput("dr", null, null), default));
        await Assert.ThrowsAsync<Volo.Abp.UserFriendlyException>(() => admin.AddAsync(new SiteInput("Bad Name!", null, null), default));
        await Assert.ThrowsAsync<Volo.Abp.UserFriendlyException>(() => admin.AddAsync(new SiteInput("x", null, "ftp://x"), default));
        Assert.Equal("Recovery", (await admin.UpdateAsync("dr", new SiteUpdateInput("Recovery", ""), default)).Label);
        await Assert.ThrowsAsync<Volo.Abp.UserFriendlyException>(() => admin.RemoveAsync("hq", default));

        SitesOverviewDto overview = await admin.GetAsync(default);
        Assert.Equal(("hq", "hq", 2), (overview.ThisSite, overview.Active!.Site, overview.Sites.Count));
        await Assert.ThrowsAsync<Volo.Abp.UserFriendlyException>(() => admin.SwitchoverAsync("dr", default));
        await admin.RemoveAsync("dr", default);
        Assert.Single((await admin.GetAsync(default)).Sites);
    }

    [Fact]
    public async Task A_standbys_lag_is_told_once_a_spell()
    {
        SitesOptions hq = Options("hq", o => o.WarnAfterMinutes = 0);
        await ClaimAsync(hq);
        await SaveSiteAsync("dr", "http://127.0.0.1:9");
        Alerts alerts = new();
        SiteLagWatch watch = new(Empty, hq, ClusterLocksTests.Locks(), alerts, NullLogger<SiteLagWatch>.Instance);

        await watch.LookAsync(default);
        await watch.LookAsync(default);
        Assert.Equal(["dr"], alerts.Told);
    }

    [Fact]
    public async Task The_running_app_says_active_signed_or_its_state()
    {
        SitesOptions hq = Options("hq");
        await ClaimAsync(hq);
        SiteState state = new(Empty, hq, new DatabaseSiteSigningKey(Empty, hq), NullLogger<SiteState>.Instance);
        SiteStateDto said = await state.StateAsync(default);
        Assert.Equal(SiteStates.Active, said.State);
        Assert.True(SiteSignature.Verify(said, await new DatabaseSiteSigningKey(Empty, hq).KeyAsync(default), DateTimeOffset.UtcNow));
        Assert.True((await state.ReadinessAsync(default)).Ready);

        SiteStateDto foreign = await new SiteState(Empty, Options("dr"), new DatabaseSiteSigningKey(Empty, hq), NullLogger<SiteState>.Instance).StateAsync(default);
        Assert.Equal((SiteStates.Foreign, (string?)null), (foreign.State, foreign.Signature));
    }

    [Fact]
    public async Task The_site_host_serves_its_state_and_takes_nothing_without_the_break_glass_key()
    {
        string hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("break glass")));
        SitesOptions Hq(IConfiguration _) => Options("hq", o =>
        {
            o.BreakGlassKeyHash = hash;
            o.WatchSeconds = 3600;
            o.ConfigureHostBuilder = builder => builder.WebHost.UseTestServer();
        });
        SiteDecision standby = new(SiteMode.Standby, new SiteDatabaseDto(true, null, 0, "0/1", "0/1"), null);
        (WebApplication app, SiteWatch _) = SiteHost.Build([], standby, Hq);
        await using (app)
        {
            await app.StartAsync();
            HttpClient http = app.GetTestClient();

            HttpResponseMessage state = await http.GetAsync("/api/site");
            Assert.Equal(HttpStatusCode.ServiceUnavailable, state.StatusCode);
            Assert.Equal(SiteStates.Standby, (await state.Content.ReadFromJsonAsync<SiteStateDto>())!.State);
            Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/api/ready")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/api/health")).StatusCode);
            Assert.Equal("Sites:Standby", await CodeAsync(await http.GetAsync("/api/anything")));
            Assert.Equal("Sites:Standby", await CodeAsync(await http.PostAsJsonAsync("/api/site/changes", new BreakGlassInput("break glass"))));

            Assert.Equal("Sites:WrongBreakGlass", await CodeAsync(await http.PostAsJsonAsync("/api/site/promote", new PromoteInput("guess", "hq"))));
            Assert.Equal("Sites:ConfirmSite", await CodeAsync(await http.PostAsJsonAsync("/api/site/promote", new PromoteInput("break glass", "dr"))));
            Assert.Equal("Sites:NotHandingOver", await CodeAsync(await http.PostAsJsonAsync("/api/site/cancel-handover", new CancelHandoverInput("break glass", null))));
            await app.StopAsync();
        }
    }

    [Fact]
    public async Task A_site_handing_over_calls_it_off_with_the_key_once_its_operators_confirm()
    {
        await ClaimAsync(Options("hq"));
        await using (NpgsqlConnection connection = await OpenAsync())
        {
            await new SiteStore(Options()).BeginHandoverAsync(connection, "hq", "dr", default);
        }

        string hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("break glass")));
        SitesOptions Hq(IConfiguration _) => Options("hq", o =>
        {
            o.BreakGlassKeyHash = hash;
            o.WatchSeconds = 3600;
            o.ConfigureHostBuilder = builder => builder.WebHost.UseTestServer();
        });
        SiteDecision decision = await SiteStart.DecideAsync(Empty, Hq(Empty), act: false, NullLogger.Instance, default);
        Assert.Equal(SiteMode.HandingOver, decision.Mode);
        (WebApplication app, SiteWatch _) = SiteHost.Build([], decision, Hq);
        await using (app)
        {
            await app.StartAsync();
            HttpClient http = app.GetTestClient();
            Assert.Equal("Sites:NotStandby", await CodeAsync(await http.PostAsJsonAsync("/api/site/promote", new PromoteInput("break glass", "hq"))));
            Assert.Equal("Sites:ConfirmNotServing", await CodeAsync(await http.PostAsJsonAsync("/api/site/cancel-handover", new CancelHandoverInput("break glass", null))));
            Assert.Equal(HttpStatusCode.OK, (await http.PostAsJsonAsync("/api/site/cancel-handover", new CancelHandoverInput("break glass", "dr"))).StatusCode);
            await app.StopAsync();
        }

        Assert.Null((await ActiveAsync())!.HandingTo);
    }

    [Theory]
    [InlineData(SiteMode.Replaced, SiteStates.Replaced)]
    [InlineData(SiteMode.Foreign, SiteStates.Foreign)]
    [InlineData(SiteMode.HandingOver, SiteStates.HandingOver)]
    public async Task The_site_host_starts_in_every_mode_and_says_its_state(SiteMode mode, string said)
    {
        SitesOptions Hq(IConfiguration _) => Options("hq", o =>
        {
            o.WatchSeconds = 3600;
            o.ConfigureHostBuilder = builder => builder.WebHost.UseTestServer();
        });
        ActiveSiteDto active = new("dr", 2, 2, DateTimeOffset.UtcNow, SiteReasons.StoodDown, HandingTo: mode == SiteMode.HandingOver ? "dr" : null);
        (WebApplication app, SiteWatch _) = SiteHost.Build([], new SiteDecision(mode, new SiteDatabaseDto(false, null, null, null, null), active), Hq);
        await using (app)
        {
            await app.StartAsync();
            HttpResponseMessage state = await app.GetTestClient().GetAsync("/api/site");
            Assert.Equal((HttpStatusCode.ServiceUnavailable, said), (state.StatusCode, (await state.Content.ReadFromJsonAsync<SiteStateDto>())!.State));
            await app.StopAsync();
        }
    }

    [Fact]
    public async Task Messages_name_settings_as_the_host_configures_them()
    {
        SitesOptions Hq(IConfiguration _) => Options("hq", o =>
        {
            o.Section = "Site";
            o.WatchSeconds = 3600;
            o.ConfigureHostBuilder = builder => builder.WebHost.UseTestServer();
        });
        SiteDecision standby = new(SiteMode.Standby, new SiteDatabaseDto(true, null, 0, "0/1", "0/1"), null);
        (WebApplication app, SiteWatch _) = SiteHost.Build([], standby, Hq);
        await using (app)
        {
            await app.StartAsync();
            HttpResponseMessage refused = await app.GetTestClient().PostAsJsonAsync("/api/site/promote", new PromoteInput("any", "hq"));
            using JsonDocument body = JsonDocument.Parse(await refused.Content.ReadAsStringAsync());
            Assert.Equal("This server has no break-glass key: set Site:BreakGlassKeyHash to the SHA-256 of one, in hex.",
                body.RootElement.GetProperty("error").GetProperty("message").GetString());
            await app.StopAsync();
        }
    }

    private static async Task<bool> EventuallyAsync(Func<Task<bool>> condition)
    {
        for (DateTime until = DateTime.UtcNow.AddSeconds(5); DateTime.UtcNow <= until; await Task.Delay(100))
        {
            if (await condition())
            {
                return true;
            }
        }

        return false;
    }

    private async Task SaveSiteAsync(string name, string address)
    {
        await using NpgsqlConnection connection = await OpenAsync();
        SiteStore store = new(Options());
        if (!await store.SaveSiteAsync(connection, new SiteRecord(name, name, address), adding: false, default))
        {
            await store.SaveSiteAsync(connection, new SiteRecord(name, name, address), adding: true, default);
        }
    }

    private static async Task<string?> CodeAsync(HttpResponseMessage response)
    {
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("error").GetProperty("code").GetString();
    }

    /// <summary>Another site, on a port of its own: what it says at its state path.</summary>
    private static async Task<WebApplication> OtherSiteAsync(Func<SiteStateDto> says)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        WebApplication app = builder.Build();
        app.MapGet("/api/site", () => Results.Json(says()));
        await app.StartAsync();
        return app;
    }

    private static string Address(WebApplication app) =>
        app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();

    private sealed class Lifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication()
        {
        }
    }

    private sealed class Alerts : ISiteAlerts
    {
        public List<string> Told { get; } = [];

        public Task StandbyLaggingAsync(string standby, DateTime since, long? behindBytes, string? error, CancellationToken cancellationToken)
        {
            Told.Add(standby);
            return Task.CompletedTask;
        }
    }
}
