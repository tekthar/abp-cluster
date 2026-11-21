using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using AbpLanguages.HttpApi;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Volo.Abp;
using Volo.Abp.Data;
using Volo.Abp.DependencyInjection;
using Volo.Abp.EntityFrameworkCore;
using Volo.Abp.EntityFrameworkCore.PostgreSql;
using Volo.Abp.Modularity;

namespace AbpLanguages.Tests;

/// <summary>
/// The packages on a small web host and PostgreSQL: a new site's languages, the texts merged along a chain and cached by
/// version, the admins' languages and texts with their rules, export and import, two servers agreeing, and who may do what.
/// Needs PostgreSQL: localhost:5432 (postgres/postgres), or the server ABP_CLUSTER_TEST_POSTGRES names.
/// </summary>
[Collection("languages")]
public sealed class LanguagesApiTests : IAsyncLifetime
{
    internal static readonly string Server =
        Environment.GetEnvironmentVariable("ABP_CLUSTER_TEST_POSTGRES") is { Length: > 0 } server ? server : "Host=localhost;Port=5432;Username=postgres;Password=postgres";

    private const string Prefix = "/api/platform";
    private const string AdminPolicy = "site-admin";

    [ReplaceDbContext(typeof(ILanguagesDbContext))]
    [ConnectionStringName("Default")]
    public sealed class SiteDbContext(DbContextOptions<SiteDbContext> options) : AbpDbContext<SiteDbContext>(options), ILanguagesDbContext
    {
        public DbSet<Language> Languages => Set<Language>();

        public DbSet<TextOverride> TextOverrides => Set<TextOverride>();

        public DbSet<LanguagesState> LanguagesState => Set<LanguagesState>();

        protected override void OnModelCreating(ModelBuilder builder)
        {
            base.OnModelCreating(builder);
            builder.ConfigureLanguages("platform", new LanguagesTables(State: "localization_state"));
        }
    }

    /// <summary>The site's texts: the app's own and a plugin's, whose source a test can change (a new generation).</summary>
    public sealed class Catalogs : ITextCatalogSource
    {
        public Dictionary<string, string> AppEnglish { get; } = new() { ["hello"] = "Hello {name}", ["records.create"] = "Create", ["records.delete"] = "Delete" };

        public long Generation { get; set; } = 1;

        public IEnumerable<TextCatalog> Load() =>
        [
            new("app", new Dictionary<string, IReadOnlyDictionary<string, string>>
            {
                ["en"] = new Dictionary<string, string>(AppEnglish),
                ["ar"] = new Dictionary<string, string> { ["records.create"] = "إنشاء" },
                ["fr"] = new Dictionary<string, string> { ["records.create"] = "Créer", ["records.delete"] = "Supprimer" },
            }),
            new("acme.tasks", new Dictionary<string, IReadOnlyDictionary<string, string>> { ["en"] = new Dictionary<string, string> { ["title"] = "Tasks" } }),
            new("broken", new Dictionary<string, IReadOnlyDictionary<string, string>> { ["fr"] = new Dictionary<string, string> { ["x"] = "y" } }),
        ];
    }

    public sealed class Events : ILanguagesEvents
    {
        public List<LanguagesChange> Changes { get; } = [];

        public Task ChangedAsync(LanguagesChange change, CancellationToken cancellationToken)
        {
            lock (Changes)
            {
                Changes.Add(change);
            }

            return Task.CompletedTask;
        }
    }

    [DependsOn(typeof(AbpLanguagesHttpApiModule), typeof(AbpEntityFrameworkCorePostgreSqlModule), typeof(Volo.Abp.Autofac.AbpAutofacModule))]
    public sealed class SiteHost : AbpModule
    {
        public override void ConfigureServices(ServiceConfigurationContext context)
        {
            context.Services.AddAbpDbContext<SiteDbContext>(o => o.ReplaceDbContext<ILanguagesDbContext>());
            Configure<AbpDbContextOptions>(o => o.UseNpgsql());
            context.Services.AddAuthentication(TestSignIn.Name).AddScheme<AuthenticationSchemeOptions, TestSignIn>(TestSignIn.Name, null);
            context.Services.AddAuthorization(o => o.AddPolicy(AdminPolicy, p => p.RequireRole("admin")));
            Configure<AbpLanguagesOptions>(o =>
            {
                o.Seed.Add(new LanguageSeed("ar", "العربية", "Arabic", Fallback: "en"));
                o.CheckEvery = TimeSpan.FromMilliseconds(200);
            });
        }

        public override void OnApplicationInitialization(ApplicationInitializationContext context)
        {
            IApplicationBuilder app = context.GetApplicationBuilder();
            app.UseRouting();
            app.UseAuthentication();
            app.UseAuthorization();
            app.UseConfiguredEndpoints();
        }
    }

    /// <summary>Signed in as the <c>X-Role</c> header says, or not at all without it.</summary>
    public sealed class TestSignIn(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string Name = "Test";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (Request.Headers["X-Role"].ToString() is not { Length: > 0 } role)
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            ClaimsIdentity identity = new([new Claim(ClaimTypes.NameIdentifier, "00000000-0000-0000-0000-0000000000a1"), new Claim(ClaimTypes.Role, role)], Name);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Name)));
        }
    }

    private readonly string _database = "abp_languages_" + Guid.NewGuid().ToString("N")[..12];
    private readonly Catalogs _catalogs = new();
    private readonly Events _events = new();
    private readonly List<WebApplication> _apps = [];

    public async Task InitializeAsync()
    {
        await using NpgsqlConnection connection = new($"{Server};Database=postgres");
        await connection.OpenAsync();
        await using NpgsqlCommand create = new($"CREATE DATABASE \"{_database}\"", connection);
        await create.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync()
    {
        foreach (WebApplication app in _apps)
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }

        NpgsqlConnection.ClearAllPools();
        await using NpgsqlConnection connection = new($"{Server};Database=postgres");
        await connection.OpenAsync();
        await using NpgsqlCommand drop = new($"DROP DATABASE IF EXISTS \"{_database}\" WITH (FORCE)", connection);
        await drop.ExecuteNonQueryAsync();
    }

    private async Task<WebApplication> StartAsync(string? policy = AdminPolicy, Action<IServiceCollection>? services = null)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Host.UseAutofac();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Default"] = $"{Server};Database={_database}" });
        await builder.AddApplicationAsync<SiteHost>();
        builder.Services.AddSingleton<ITextCatalogSource>(_catalogs);
        builder.Services.AddSingleton<ILanguagesEvents>(_events);
        builder.Services.Configure<LanguagesHttpOptions>(o =>
        {
            o.RoutePrefix = Prefix;
            o.Policy = policy;
        });
        services?.Invoke(builder.Services);
        WebApplication app = builder.Build();
        await app.InitializeApplicationAsync();
        if (_apps.Count == 0)
        {
            await using AsyncServiceScope scope = app.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<SiteDbContext>().Database.EnsureCreatedAsync();
            await scope.ServiceProvider.GetRequiredService<LanguagesSeeder>().SeedAsync(CancellationToken.None);
        }

        await app.StartAsync();
        _apps.Add(app);
        return app;
    }

    private static HttpClient As(WebApplication app, string? role)
    {
        HttpClient http = app.GetTestClient();
        if (role is not null)
        {
            http.DefaultRequestHeaders.Add("X-Role", role);
        }

        return http;
    }

    private static async Task<JsonElement> TextsAsync(HttpClient http, string code) =>
        await http.GetFromJsonAsync<JsonElement>($"{Prefix}/texts/{code}");

    private static async Task<string?> CodeOf(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetProperty("code").GetString();

    [Fact]
    public async Task A_new_site_has_its_seeded_languages_and_anyone_reads_them_and_their_texts()
    {
        WebApplication app = await StartAsync();
        HttpClient anyone = As(app, null);
        LanguagesDto site = (await anyone.GetFromJsonAsync<LanguagesDto>($"{Prefix}/languages"))!;
        Assert.Equal("en", site.DefaultLanguage);
        Assert.Equal(["en", "ar"], site.Languages.Select(l => l.Code));
        Assert.Equal(("rtl", "en", null), (site.Languages[1].Direction, site.Languages[1].Fallback, site.Languages[1].Translated));

        // Arabic: the shipped Arabic where there is one, else the source; a catalog with no English is left out.
        JsonElement ar = await TextsAsync(anyone, "ar");
        Assert.Equal(("ar", "rtl"), (ar.GetProperty("language").GetString(), ar.GetProperty("direction").GetString()));
        Assert.Equal(["ar", "en"], ar.GetProperty("chain").EnumerateArray().Select(c => c.GetString()));
        Assert.Equal("إنشاء", ar.GetProperty("messages").GetProperty("records").GetProperty("create").GetString());
        Assert.Equal("Delete", ar.GetProperty("messages").GetProperty("records").GetProperty("delete").GetString());
        Assert.Equal("Tasks", ar.GetProperty("catalogs").GetProperty("acme.tasks").GetProperty("title").GetString());
        Assert.False(ar.GetProperty("catalogs").TryGetProperty("broken", out _));
        Assert.Equal(HttpStatusCode.NotFound, (await anyone.GetAsync($"{Prefix}/texts/xx")).StatusCode);
    }

    [Fact]
    public async Task Texts_are_cached_for_good_by_their_version_and_answered_by_etag_without_it()
    {
        WebApplication app = await StartAsync();
        HttpClient anyone = As(app, null);
        string version = (await anyone.GetFromJsonAsync<LanguagesDto>($"{Prefix}/languages"))!.Languages[0].TextsVersion;

        HttpResponseMessage versioned = await anyone.GetAsync($"{Prefix}/texts/en?v={version}");
        Assert.Equal("public, max-age=31536000, immutable", versioned.Headers.CacheControl?.ToString());
        HttpResponseMessage plain = await anyone.GetAsync($"{Prefix}/texts/en");
        Assert.True(plain.Headers.CacheControl?.NoCache);
        Assert.Equal($"\"{version}\"", plain.Headers.ETag?.Tag);

        HttpRequestMessage again = new(HttpMethod.Get, $"{Prefix}/texts/en");
        again.Headers.IfNoneMatch.Add(new EntityTagHeaderValue($"\"{version}\""));
        Assert.Equal(HttpStatusCode.NotModified, (await anyone.SendAsync(again)).StatusCode);
    }

    [Fact]
    public async Task Admins_add_and_change_languages_by_the_rules()
    {
        WebApplication app = await StartAsync();
        HttpClient admin = As(app, "admin");

        LanguageDto fr = (await (await admin.PostAsJsonAsync($"{Prefix}/languages", new LanguageInput("fr", "Français", "French"))).Content.ReadFromJsonAsync<LanguageDto>())!;
        Assert.Equal(("ltr", 3, 4, 2), (fr.Direction, fr.Order, fr.Total, fr.Translated));
        LanguageDto frCa = (await (await admin.PostAsJsonAsync($"{Prefix}/languages", new LanguageInput("fr-CA", EnglishName: "Canadian French"))).Content.ReadFromJsonAsync<LanguageDto>())!;
        Assert.Equal(("fr", "Canadian French"), (frCa.Fallback, frCa.NativeName));
        JsonElement canadian = await TextsAsync(admin, "fr-CA");
        Assert.Equal(["fr-CA", "fr", "en"], canadian.GetProperty("chain").EnumerateArray().Select(c => c.GetString()));
        Assert.Equal("Supprimer", canadian.GetProperty("messages").GetProperty("records").GetProperty("delete").GetString());

        foreach (LanguageInput bad in new LanguageInput[] { new("French"), new("fr", EnglishName: "Again"), new("de", EnglishName: "German", Direction: "up"), new("de"), new("de", EnglishName: "German", Fallback: "xx") })
        {
            HttpResponseMessage refused = await admin.PostAsJsonAsync($"{Prefix}/languages", bad);
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            Assert.Equal(LanguagesErrorCodes.Invalid, await CodeOf(refused));
        }

        // A loop of fallbacks, turning the default off, and taking the default or the source away are refused.
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync($"{Prefix}/languages/fr", new LanguageInput(Fallback: "fr-CA"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync($"{Prefix}/languages/en", new LanguageInput(Enabled: false))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.DeleteAsync($"{Prefix}/languages/en")).StatusCode);

        // French as the default; then English, no longer the default, can be off, but still not removed.
        Assert.True((await admin.PutAsJsonAsync($"{Prefix}/languages/fr", new LanguageInput(IsDefault: true))).IsSuccessStatusCode);
        Assert.True((await admin.PutAsJsonAsync($"{Prefix}/languages/EN", new LanguageInput(Enabled: false))).IsSuccessStatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.DeleteAsync($"{Prefix}/languages/en")).StatusCode);
        LanguagesDto every = (await admin.GetFromJsonAsync<LanguagesDto>($"{Prefix}/languages?all=true"))!;
        Assert.Equal(("fr", 4), (every.DefaultLanguage, every.Languages.Count));
        Assert.DoesNotContain((await As(app, null).GetFromJsonAsync<LanguagesDto>($"{Prefix}/languages"))!.Languages, l => l.Code == "en");
        Assert.Equal(HttpStatusCode.NotFound, (await As(app, null).GetAsync($"{Prefix}/texts/en")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"{Prefix}/texts/en")).StatusCode);

        // Removing a language clears the fallbacks to it.
        Assert.True((await admin.DeleteAsync($"{Prefix}/languages/fr-CA")).IsSuccessStatusCode);
        Assert.Equal(["language.created", "language.created", "language.changed", "language.changed", "language.removed"], _events.Changes.Select(c => c.What));
    }

    [Fact]
    public async Task Admins_texts_win_keep_their_arguments_and_show_outdated_when_the_source_changes()
    {
        WebApplication app = await StartAsync();
        HttpClient admin = As(app, "admin");
        HttpResponseMessage broken = await admin.PutAsJsonAsync($"{Prefix}/texts/ar", new SaveTextsInput([new("app", "hello", "مرحبا"), new("app", "records.delete", "حذف")]));
        Assert.Equal(HttpStatusCode.BadRequest, broken.StatusCode);
        Assert.Contains("keep {name}", (await broken.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetProperty("message").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync($"{Prefix}/texts/ar", new SaveTextsInput([new("app", "nope", "x")]))).StatusCode);

        SaveTextsResult saved = (await (await admin.PutAsJsonAsync($"{Prefix}/texts/ar", new SaveTextsInput([new("app", "hello", "مرحبا {name}"), new("app", "records.delete", "حذف")]))).Content.ReadFromJsonAsync<SaveTextsResult>())!;
        Assert.Equal((2, 0), (saved.Saved, saved.Removed));
        Assert.Equal("حذف", (await TextsAsync(admin, "ar")).GetProperty("messages").GetProperty("records").GetProperty("delete").GetString());

        TextEntriesDto entries = (await admin.GetFromJsonAsync<TextEntriesDto>($"{Prefix}/texts/ar/entries"))!;
        Assert.Equal((4, 3), (entries.Total, entries.Translated));
        Assert.Equal(["translated", "bundled", "translated", "missing"], entries.Entries.Select(e => e.Status));

        // The source changes (a new generation): the translation made from the old text is out of date.
        _catalogs.AppEnglish["hello"] = "Hi {name}";
        _catalogs.Generation++;
        entries = (await admin.GetFromJsonAsync<TextEntriesDto>($"{Prefix}/texts/ar/entries"))!;
        Assert.Equal("outdated", entries.Entries.Single(e => e.Key == "hello").Status);

        // An empty value takes the admin's text away.
        SaveTextsResult removed = (await (await admin.PutAsJsonAsync($"{Prefix}/texts/ar", new SaveTextsInput([new("app", "records.delete", "")]))).Content.ReadFromJsonAsync<SaveTextsResult>())!;
        Assert.Equal((0, 1), (removed.Saved, removed.Removed));
        Assert.Equal("Delete", (await TextsAsync(admin, "ar")).GetProperty("messages").GetProperty("records").GetProperty("delete").GetString());
    }

    [Fact]
    public async Task A_translators_file_goes_out_and_comes_back()
    {
        WebApplication app = await StartAsync();
        HttpClient admin = As(app, "admin");
        await admin.PutAsJsonAsync($"{Prefix}/texts/ar", new SaveTextsInput([new("app", "records.delete", "حذف")]));
        JsonObject exported = (await admin.GetFromJsonAsync<JsonObject>($"{Prefix}/texts/ar/export"))!;
        Assert.Equal("""{"records":{"create":"إنشاء","delete":"حذف"}}""", exported.ToJsonString(new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));

        JsonObject file = JsonNode.Parse("""{"hello":"أهلا {name}","records":{"create":"إنشاء","delete":"امسح"},"extra":"x"}""")!.AsObject();
        ImportTextsResult kept = (await (await admin.PostAsJsonAsync($"{Prefix}/texts/ar/import", new ImportTextsInput("app", file))).Content.ReadFromJsonAsync<ImportTextsResult>())!;
        Assert.Equal((1, 1, 1), (kept.Imported, kept.Unchanged, kept.Kept));
        Assert.Equal(["extra"], kept.Unknown);
        ImportTextsResult overwrote = (await (await admin.PostAsJsonAsync($"{Prefix}/texts/ar/import", new ImportTextsInput("app", file, Overwrite: true))).Content.ReadFromJsonAsync<ImportTextsResult>())!;
        Assert.Equal((1, 2), (overwrote.Imported, overwrote.Unchanged));
        JsonObject bad = JsonNode.Parse("""{"hello":"أهلا"}""")!.AsObject();
        Assert.Equal(["hello"], (await (await admin.PostAsJsonAsync($"{Prefix}/texts/ar/import", new ImportTextsInput("app", bad, true))).Content.ReadFromJsonAsync<ImportTextsResult>())!.Refused);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync($"{Prefix}/texts/ar/import", new ImportTextsInput("nope", file))).StatusCode);
        Assert.Equal("امسح", (await TextsAsync(admin, "ar")).GetProperty("messages").GetProperty("records").GetProperty("delete").GetString());
    }

    [Fact]
    public async Task Two_servers_agree_on_a_change_through_the_version()
    {
        WebApplication one = await StartAsync();
        WebApplication two = await StartAsync();
        string before = (await TextsAsync(As(two, null), "ar")).GetProperty("version").GetString()!;
        await As(one, "admin").PutAsJsonAsync($"{Prefix}/texts/ar", new SaveTextsInput([new("app", "records.delete", "حذف")]));
        await Task.Delay(400);
        JsonElement after = await TextsAsync(As(two, null), "ar");
        Assert.NotEqual(before, after.GetProperty("version").GetString());
        Assert.Equal("حذف", after.GetProperty("messages").GetProperty("records").GetProperty("delete").GetString());
    }

    [Fact]
    public async Task Only_the_hosts_admins_change_anything_and_with_no_policy_no_one_does()
    {
        WebApplication app = await StartAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, (await As(app, null).PostAsJsonAsync($"{Prefix}/languages", new LanguageInput("fr", EnglishName: "French"))).StatusCode);
        HttpResponseMessage member = await As(app, "member").PostAsJsonAsync($"{Prefix}/languages", new LanguageInput("fr", EnglishName: "French"));
        Assert.Equal((HttpStatusCode.Forbidden, LanguagesErrorCodes.Forbidden), (member.StatusCode, await CodeOf(member)));
        Assert.Equal(HttpStatusCode.Forbidden, (await As(app, "member").GetAsync($"{Prefix}/languages?all=true")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await As(app, "member").GetAsync($"{Prefix}/texts/ar/entries")).StatusCode);

        WebApplication none = await StartAsync(policy: null);
        Assert.Equal(HttpStatusCode.Forbidden, (await As(none, "admin").PutAsJsonAsync($"{Prefix}/texts/ar", new SaveTextsInput([]))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await As(none, null).GetAsync($"{Prefix}/languages")).StatusCode);
    }
}
