using System.Collections.Concurrent;
using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Volo.Abp;
using Volo.Abp.Autofac;
using Volo.Abp.Data;
using Volo.Abp.EntityFrameworkCore;
using Volo.Abp.EntityFrameworkCore.PostgreSql;
using Volo.Abp.Modularity;
using Volo.Abp.Uow;

namespace AbpWebhooks.Tests;

/// <summary>
/// The queue in an application's own database: a delivery is sent signed under every header prefix, a refusal waits and
/// is tried again, a webhook that's off waits and a removed one fails, a redelivery is a new delivery, two servers send
/// a delivery once, old ones are pruned, and the table keeps the names an application gives its columns. Needs
/// PostgreSQL: localhost:5432 (postgres/postgres), or the server ABP_CLUSTER_TEST_POSTGRES names.
/// </summary>
public class QueueTests
{
    [Fact]
    public async Task A_delivery_is_sent_signed_under_every_prefix()
    {
        await using QueueApp app = await QueueApp.StartAsync();
        Guid hook = app.AddEndpoint(enabled: true);
        Guid scope = Guid.NewGuid(), subject = Guid.NewGuid();
        WebhookDelivery delivery = new(WebhookIds.Delivery(Guid.NewGuid(), hook), hook, "record.created", scope, subject, "ACME-1", "{\"event\":\"record.created\"}");

        await app.Queue.EnqueueAsync([delivery], default);
        await app.Queue.EnqueueAsync([new WebhookDelivery(delivery.Id, hook, "record.created", scope, subject, "ACME-1", "{}")], default); // heard again
        await app.Dispatcher.SendDueAsync(default);

        Received request = Assert.Single(app.Receiver.Requests);
        // The body as the database keeps it (jsonb writes it its own way); the signature is over that.
        Assert.Equal(("https://receiver.test/hooks/" + hook, "application/json; charset=utf-8", "record.created"),
            (request.Url, request.ContentType, System.Text.Json.JsonDocument.Parse(request.Body).RootElement.GetProperty("event").GetString()));
        foreach (string prefix in new[] { "X-Webhook-", "X-Former-" })
        {
            Assert.Equal(("record.created", delivery.Id.ToString()), (request.Headers[prefix + "Event"], request.Headers[prefix + "Delivery"]));
            Assert.True(WebhookSignature.Verify(QueueApp.Secret, request.Headers[prefix + "Timestamp"], request.Body, request.Headers[prefix + "Signature"]), prefix);
        }

        WebhookDelivery sent = (await app.DeliveriesAsync()).Single();
        Assert.Equal((WebhookDeliveryStatus.Sent, 1, 200, null, true, scope, subject, "ACME-1"),
            (sent.Status, sent.Attempts, sent.LastStatusCode, sent.NextAttemptAt, sent.SentAt is not null, sent.ScopeId, sent.SubjectId, sent.Subject));
    }

    [Fact]
    public async Task A_refusal_waits_and_is_tried_again()
    {
        await using QueueApp app = await QueueApp.StartAsync();
        Guid hook = app.AddEndpoint(enabled: true);
        app.Receiver.Answer = HttpStatusCode.InternalServerError;
        WebhookDelivery delivery = new(Guid.NewGuid(), hook, "record.updated", null, null, null, "{}");
        await app.Queue.EnqueueAsync([delivery], default);

        DateTime before = DateTime.UtcNow;
        await app.Dispatcher.SendDueAsync(default);
        WebhookDelivery waiting = (await app.DeliveriesAsync()).Single();
        Assert.Equal((WebhookDeliveryStatus.Queued, 1, 500), (waiting.Status, waiting.Attempts, waiting.LastStatusCode));
        Assert.StartsWith("500 Internal Server Error: receiver says no", waiting.LastError);
        Assert.InRange(waiting.NextAttemptAt!.Value, before.AddSeconds(59), DateTime.UtcNow.AddMinutes(1).AddSeconds(1));

        await app.Dispatcher.SendDueAsync(default); // not due yet
        Assert.Single(app.Receiver.Requests);
    }

    [Fact]
    public async Task A_webhook_that_is_off_waits_and_a_removed_one_fails()
    {
        await using QueueApp app = await QueueApp.StartAsync();
        Guid off = app.AddEndpoint(enabled: false);
        WebhookDelivery toOff = new(Guid.NewGuid(), off, "record.created", null, null, null, "{}");
        WebhookDelivery toNone = new(Guid.NewGuid(), Guid.NewGuid(), "record.created", null, null, null, "{}");
        await app.Queue.EnqueueAsync([toOff, toNone], default);
        await app.Dispatcher.SendDueAsync(default);

        Assert.Empty(app.Receiver.Requests);
        Dictionary<Guid, WebhookDelivery> rows = (await app.DeliveriesAsync()).ToDictionary(d => d.Id);
        Assert.Equal((WebhookDeliveryStatus.Queued, 0), (rows[toOff.Id].Status, rows[toOff.Id].Attempts));
        Assert.True(rows[toOff.Id].NextAttemptAt > DateTime.UtcNow.AddSeconds(50));
        Assert.Equal((WebhookDeliveryStatus.Queued, 1, "The webhook was removed."), (rows[toNone.Id].Status, rows[toNone.Id].Attempts, rows[toNone.Id].LastError));
    }

    [Fact]
    public async Task A_redelivery_is_a_new_delivery_with_its_own_id()
    {
        await using QueueApp app = await QueueApp.StartAsync();
        Guid hook = app.AddEndpoint(enabled: true);
        WebhookDelivery first = new(Guid.NewGuid(), hook, "comment.added", Guid.NewGuid(), Guid.NewGuid(), "ACME-2", $"{{\"id\":\"{Guid.Empty}\",\"event\":\"comment.added\"}}");
        await app.Queue.EnqueueAsync([first], default);
        await app.Dispatcher.SendDueAsync(default);

        Assert.Null(await app.Queue.RedeliverAsync(Guid.NewGuid(), first.Id, default));
        WebhookDelivery again = (await app.Queue.RedeliverAsync(hook, first.Id, default))!;
        Assert.Equal((first.Id, first.ScopeId, first.SubjectId, "ACME-2", "comment.added"), (again.RedeliveryOf, again.ScopeId, again.SubjectId, again.Subject, again.Event));
        Assert.Contains($"\"id\":\"{again.Id}\"", again.Payload);

        await app.Dispatcher.SendDueAsync(default);
        Assert.Equal(again.Id.ToString(), app.Receiver.Requests.Last().Headers["X-Webhook-Delivery"]);
    }

    [Fact]
    public async Task Two_servers_send_a_delivery_once()
    {
        await using QueueApp app = await QueueApp.StartAsync();
        Guid hook = app.AddEndpoint(enabled: true);
        app.Receiver.Delay = TimeSpan.FromMilliseconds(200);
        await app.Queue.EnqueueAsync([.. Enumerable.Range(0, 5).Select(_ => new WebhookDelivery(Guid.NewGuid(), hook, "record.created", null, null, null, "{}"))], default);

        await Task.WhenAll(app.NewDispatcher().SendDueAsync(default), app.NewDispatcher().SendDueAsync(default), app.NewDispatcher().SendDueAsync(default));

        Assert.Equal(5, app.Receiver.Requests.Count);
        Assert.Equal(5, app.Receiver.Requests.Select(r => r.Headers["X-Webhook-Delivery"]).Distinct().Count());
    }

    [Fact]
    public async Task Old_deliveries_are_pruned_and_waiting_ones_kept()
    {
        await using QueueApp app = await QueueApp.StartAsync();
        Guid hook = app.AddEndpoint(enabled: true);
        WebhookDelivery oldSent = new(Guid.NewGuid(), hook, "record.created", null, null, null, "{}") { Status = WebhookDeliveryStatus.Sent };
        WebhookDelivery oldQueued = new(Guid.NewGuid(), hook, "record.created", null, null, null, "{}");
        WebhookDelivery recent = new(Guid.NewGuid(), hook, "record.created", null, null, null, "{}") { Status = WebhookDeliveryStatus.Failed };
        await app.SaveAsync([oldSent, oldQueued, recent], age: d => d == recent ? TimeSpan.FromDays(1) : TimeSpan.FromDays(31));

        Assert.Equal(1, await app.Dispatcher.PruneAsync(default));
        Assert.Equal(new[] { oldQueued.Id, recent.Id }.Order(), (await app.DeliveriesAsync()).Select(d => d.Id).Order());
    }

    [Fact]
    public async Task The_table_keeps_the_names_an_application_gives_it()
    {
        await using QueueApp app = await QueueApp.StartAsync();
        IEntityType entity = app.Model.FindEntityType(typeof(WebhookDelivery))!;
        StoreObjectIdentifier table = StoreObjectIdentifier.Table("hook_queue", "hooks");
        Assert.Equal(("hooks", "hook_queue"), (entity.GetSchema(), entity.GetTableName()));
        Assert.Equal(("ProjectId", "RecordId", "RecordKey", "Event", "jsonb"),
            (entity.FindProperty(nameof(WebhookDelivery.ScopeId))!.GetColumnName(table), entity.FindProperty(nameof(WebhookDelivery.SubjectId))!.GetColumnName(table),
                entity.FindProperty(nameof(WebhookDelivery.Subject))!.GetColumnName(table), entity.FindProperty(nameof(WebhookDelivery.Event))!.GetColumnName(table),
                entity.FindProperty(nameof(WebhookDelivery.Payload))!.GetColumnType()));
    }
}

public sealed record Received(string Url, string? ContentType, string Body, Dictionary<string, string> Headers);

/// <summary>The receiver: every request the client sends, answered with <see cref="Answer"/>.</summary>
public sealed class Receiver
{
    public ConcurrentQueue<Received> Log { get; } = new();

    public IReadOnlyList<Received> Requests => [.. Log];

    public HttpStatusCode Answer { get; set; } = HttpStatusCode.OK;

    public TimeSpan Delay { get; set; }
}

internal sealed class ReceiverHandler(Receiver receiver) : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string body = await request.Content!.ReadAsStringAsync(cancellationToken);
        receiver.Log.Enqueue(new Received(request.RequestUri!.ToString(), request.Content.Headers.ContentType?.ToString(), body,
            request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase)));
        await Task.Delay(receiver.Delay, cancellationToken);
        return new HttpResponseMessage(receiver.Answer) { Content = new StringContent(receiver.Answer == HttpStatusCode.OK ? "ok" : "receiver says no") };
    }
}

/// <summary>The application's webhooks, as the test sets them.</summary>
public sealed class Endpoints : IWebhookEndpoints
{
    public ConcurrentDictionary<Guid, WebhookEndpoint> All { get; } = new();

    public Task<WebhookEndpoint?> FindAsync(Guid webhookId, CancellationToken cancellationToken) => Task.FromResult(All.GetValueOrDefault(webhookId));
}

[ConnectionStringName("Default")]
public sealed class HostDb(DbContextOptions<HostDb> options) : AbpDbContext<HostDb>(options), IWebhooksDbContext
{
    public DbSet<WebhookDelivery> WebhookDeliveries => Set<WebhookDelivery>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.ConfigureWebhookDeliveries("hook_queue", "hooks", new WebhookDeliveryColumns("ProjectId", "RecordId", "RecordKey"));
    }
}

[DependsOn(typeof(AbpWebhooksModule), typeof(AbpEntityFrameworkCorePostgreSqlModule), typeof(AbpAutofacModule))]
public sealed class QueueHost : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        context.Services.AddAbpDbContext<HostDb>(options => options.ReplaceDbContext<IWebhooksDbContext>());
        Configure<AbpDbContextOptions>(options => options.UseNpgsql());
        context.Services.AddSingleton<Endpoints>();
        context.Services.AddSingleton<IWebhookEndpoints>(sp => sp.GetRequiredService<Endpoints>());
        context.Services.AddSingleton<Receiver>();
        context.Services.AddHttpClient("Webhooks").ConfigurePrimaryHttpMessageHandler(sp => new ReceiverHandler(sp.GetRequiredService<Receiver>()));
        Configure<WebhookSendingOptions>(options => options.HeaderPrefixes.Add("X-Former-"));
    }
}

/// <summary>The application on a database of its own, dropped after.</summary>
public sealed class QueueApp : IAsyncDisposable
{
    public const string Secret = "whsec_test";

    private static readonly string Server =
        Environment.GetEnvironmentVariable("ABP_CLUSTER_TEST_POSTGRES") is { Length: > 0 } server ? server : "Host=localhost;Port=5432;Username=postgres;Password=postgres";

    private readonly IAbpApplicationWithInternalServiceProvider _app;

    private QueueApp(IAbpApplicationWithInternalServiceProvider app) => _app = app;

    public IServiceProvider Services => _app.ServiceProvider;

    public WebhookQueue Queue => Services.GetRequiredService<WebhookQueue>();

    public WebhookDispatcher Dispatcher => Services.GetRequiredService<WebhookDispatcher>();

    public WebhookDispatcher NewDispatcher() => Services.CreateScope().ServiceProvider.GetRequiredService<WebhookDispatcher>();

    public Receiver Receiver => Services.GetRequiredService<Receiver>();

    public IModel Model { get; private set; } = null!;

    public static async Task<QueueApp> StartAsync()
    {
        string database = "abp_webhooks_" + Guid.NewGuid().ToString("N")[..12];
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Default"] = $"{Server};Database={database}" }).Build();
        IAbpApplicationWithInternalServiceProvider app = await AbpApplicationFactory.CreateAsync<QueueHost>(o =>
        {
            o.UseAutofac();
            o.Services.ReplaceConfiguration(configuration);
        });
        await app.InitializeAsync();
        QueueApp queue = new(app);
        await queue.RunAsync(async db =>
        {
            await db.Database.EnsureCreatedAsync();
            queue.Model = db.Model;
            return true;
        });
        return queue;
    }

    public Guid AddEndpoint(bool enabled)
    {
        Guid id = Guid.NewGuid();
        Services.GetRequiredService<Endpoints>().All[id] = new WebhookEndpoint("https://receiver.test/hooks/" + id, Secret, enabled);
        return id;
    }

    public Task<List<WebhookDelivery>> DeliveriesAsync() => RunAsync(db => db.WebhookDeliveries.AsNoTracking().ToListAsync());

    /// <summary>Saves deliveries as made <paramref name="age"/> ago.</summary>
    public Task SaveAsync(IEnumerable<WebhookDelivery> deliveries, Func<WebhookDelivery, TimeSpan> age) => RunAsync(async db =>
    {
        foreach (WebhookDelivery delivery in deliveries)
        {
            db.WebhookDeliveries.Add(delivery);
            db.Entry(delivery).Property(d => d.CreatedAt).CurrentValue = DateTime.UtcNow - age(delivery);
        }

        return await db.SaveChangesAsync();
    });

    private async Task<T> RunAsync<T>(Func<HostDb, Task<T>> work)
    {
        using IServiceScope scope = Services.CreateScope();
        using IUnitOfWork uow = scope.ServiceProvider.GetRequiredService<IUnitOfWorkManager>().Begin(requiresNew: true);
        HostDb db = (HostDb)await scope.ServiceProvider.GetRequiredService<IDbContextProvider<IWebhooksDbContext>>().GetDbContextAsync();
        T result = await work(db);
        await uow.CompleteAsync();
        return result;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await RunAsync(async db => await db.Database.EnsureDeletedAsync());
        }
        finally
        {
            await _app.ShutdownAsync();
            _app.Dispose();
        }
    }
}
