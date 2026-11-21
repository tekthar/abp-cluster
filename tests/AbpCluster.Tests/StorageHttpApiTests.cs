using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using AbpCluster.Storage;
using AbpCluster.Storage.HttpApi;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Volo.Abp;
using Volo.Abp.Modularity;

namespace AbpCluster.Tests;

/// <summary>
/// AbpCluster.Storage.HttpApi on a small web host, on the file system: under the host's prefix, behind its policy or its
/// own rights, refusing everyone when it names neither; where the storage is without a key, a test of each store, the
/// copy and its sensitive mark, and its own OpenAPI group.
/// </summary>
[Collection("storage")]
public sealed class StorageHttpApiTests : IAsyncDisposable
{
    private const string Prefix = "/api/admin/storage";
    private const string AdminPolicy = "site-admin";

    [DependsOn(typeof(AbpClusterStorageHttpApiModule))]
    public sealed class WebHost : AbpModule
    {
        public override void PreConfigureServices(ServiceConfigurationContext context) =>
            PreConfigure<ClusterStorageOptions>(o => o.Stores.Add("things"));

        public override void ConfigureServices(ServiceConfigurationContext context)
        {
            context.Services.AddAuthentication(TestSignIn.Name).AddScheme<AuthenticationSchemeOptions, TestSignIn>(TestSignIn.Name, null);
            context.Services.AddAuthorization(o => o.AddPolicy(AdminPolicy, p => p.RequireRole("admin")));
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

    /// <summary>Signed in as the <c>X-Role</c> header says (<c>admin</c>, <c>member</c>), or not at all without it.</summary>
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

            ClaimsIdentity identity = new([new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()), new Claim(ClaimTypes.Role, role)], Name);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Name)));
        }
    }

    /// <summary>The host's addition on a sensitive action, in these tests: a header on its answer.</summary>
    private sealed class SensitiveMark : IActionFilter
    {
        public const string Header = "X-Sensitive";

        public void OnActionExecuting(ActionExecutingContext context) => context.HttpContext.Response.Headers[Header] = "yes";

        public void OnActionExecuted(ActionExecutedContext context)
        {
        }
    }

    /// <summary>A host's own rights: only a caller whose role is <c>owner</c>.</summary>
    private sealed class OwnersOnly(Microsoft.AspNetCore.Http.IHttpContextAccessor http) : IStorageRights
    {
        public Task<bool> MayManageAsync(CancellationToken cancellationToken) => Task.FromResult(http.HttpContext?.User.IsInRole("owner") == true);
    }

    private readonly string _root = Path.Combine(Path.GetTempPath(), "abp-storage-http-tests", Guid.NewGuid().ToString("N"));
    private readonly List<WebApplication> _apps = [];

    public async ValueTask DisposeAsync()
    {
        foreach (WebApplication app in _apps)
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private async Task<WebApplication> StartAsync(string? policy, bool previous = false, Action<IServiceCollection>? services = null)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        Dictionary<string, string?> settings = new() { ["Storage:FileSystem:Path"] = Path.Combine(_root, "current") };
        if (previous)
        {
            settings["Storage:Previous:Provider"] = "FileSystem";
            settings["Storage:Previous:FileSystem:Path"] = Path.Combine(_root, "previous");
        }

        builder.Configuration.AddInMemoryCollection(settings);
        await builder.AddApplicationAsync<WebHost>();
        builder.Services.Configure<StorageHttpOptions>(o =>
        {
            o.RoutePrefix = Prefix;
            o.Policy = policy;
            o.OpenApiGroupName = "storage";
            o.Sensitive = action => action.Filters.Add(new SensitiveMark());
        });
        services?.Invoke(builder.Services);
        WebApplication app = builder.Build();
        await app.InitializeApplicationAsync();
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

    private static async Task<string?> CodeOf(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetProperty("code").GetString();

    [Fact]
    public async Task The_routes_are_under_the_hosts_prefix_behind_its_policy()
    {
        WebApplication app = await StartAsync(AdminPolicy);
        Assert.Equal(HttpStatusCode.Unauthorized, (await As(app, null).GetAsync(Prefix)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await As(app, "member").GetAsync(Prefix)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await As(app, "admin").GetAsync("/api/storage")).StatusCode);

        HttpResponseMessage read = await As(app, "admin").GetAsync(Prefix);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        StorageDto storage = (await read.Content.ReadFromJsonAsync<StorageDto>())!;
        Assert.Equal(StorageLocation.FileSystem, storage.Current.Provider);
        Assert.Equal(["things"], storage.Stores);
        Assert.Null(storage.Previous);
    }

    [Fact]
    public async Task A_test_writes_reads_and_deletes_a_file_in_each_store()
    {
        WebApplication app = await StartAsync(AdminPolicy);
        HttpResponseMessage tested = await As(app, "admin").PostAsync($"{Prefix}/test", null);
        Assert.Equal(HttpStatusCode.OK, tested.StatusCode);
        StorageTestDto store = Assert.Single((await tested.Content.ReadFromJsonAsync<List<StorageTestDto>>())!);
        Assert.True(store.Ok, store.Error);
        Assert.Equal("things", store.Store);
        Assert.False(tested.Headers.Contains(SensitiveMark.Header));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_root, "current"), "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task The_copy_is_read_and_started_and_only_starting_it_is_sensitive()
    {
        WebApplication none = await StartAsync(AdminPolicy);
        HttpResponseMessage nothing = await As(none, "admin").PostAsync($"{Prefix}/copy", null);
        Assert.Equal(HttpStatusCode.Conflict, nothing.StatusCode);
        Assert.Equal(StorageErrorCodes.NoPrevious, await CodeOf(nothing));

        WebApplication moving = await StartAsync(AdminPolicy, previous: true);
        HttpClient admin = As(moving, "admin");
        StorageDto storage = (await admin.GetFromJsonAsync<StorageDto>(Prefix))!;
        Assert.Equal(StorageLocation.FileSystem, storage.Previous?.Provider);
        HttpResponseMessage idle = await admin.GetAsync($"{Prefix}/copy");
        Assert.Equal(("idle", false), ((await idle.Content.ReadFromJsonAsync<StorageCopyDto>())!.State, idle.Headers.Contains(SensitiveMark.Header)));

        HttpResponseMessage started = await admin.PostAsync($"{Prefix}/copy", null);
        Assert.Equal(HttpStatusCode.OK, started.StatusCode);
        Assert.True(started.Headers.Contains(SensitiveMark.Header));
        StorageCopyDto copy = (await started.Content.ReadFromJsonAsync<StorageCopyDto>())!;
        for (int i = 0; i < 50 && copy.State == "running"; i++)
        {
            await Task.Delay(100);
            copy = (await admin.GetFromJsonAsync<StorageCopyDto>($"{Prefix}/copy"))!;
        }

        Assert.Equal(("done", 0), (copy.State, copy.Failed));
    }

    [Fact]
    public async Task With_no_policy_and_no_rights_of_the_hosts_no_one_manages_the_storage()
    {
        WebApplication app = await StartAsync(policy: null);
        HttpResponseMessage refused = await As(app, "admin").GetAsync(Prefix);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal(StorageErrorCodes.Forbidden, await CodeOf(refused));
        Assert.Equal(HttpStatusCode.Unauthorized, (await As(app, null).GetAsync(Prefix)).StatusCode);
    }

    [Fact]
    public async Task A_hosts_own_rights_decide_who_manages_it()
    {
        WebApplication app = await StartAsync(policy: null, services: s =>
        {
            s.AddHttpContextAccessor();
            s.AddTransient<IStorageRights, OwnersOnly>();
        });
        Assert.Equal(HttpStatusCode.OK, (await As(app, "owner").GetAsync(Prefix)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await As(app, "admin").PostAsync($"{Prefix}/test", null)).StatusCode);
    }

    [Fact]
    public async Task The_api_is_in_its_own_openapi_group()
    {
        WebApplication app = await StartAsync(AdminPolicy);
        IReadOnlyList<ApiDescriptionGroup> groups = app.Services.GetRequiredService<IApiDescriptionGroupCollectionProvider>().ApiDescriptionGroups.Items;
        ApiDescriptionGroup group = Assert.Single(groups, g => g.GroupName == "storage");
        Assert.Equal(
            ["GET api/admin/storage", "GET api/admin/storage/copy", "POST api/admin/storage/copy", "POST api/admin/storage/test"],
            group.Items.Select(d => $"{d.HttpMethod} {d.RelativePath}").Order(StringComparer.Ordinal));
    }
}
