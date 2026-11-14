using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Volo.Abp.EntityFrameworkCore;
using Volo.Abp.Uow;

namespace AbpWebhooks;

/// <summary>Where a webhook sends and with what secret, as the application keeps it; null when it was removed.</summary>
public sealed record WebhookEndpoint(string Url, string Secret, bool Enabled);

/// <summary>The application's webhooks, as the sender needs them: it keeps them, their rights and their audit.</summary>
public interface IWebhookEndpoints
{
    Task<WebhookEndpoint?> FindAsync(Guid webhookId, CancellationToken cancellationToken);
}

/// <summary>Tells the other servers a delivery is queued, so whichever is free sends it now (the application's notices).</summary>
public interface IWebhookWakeOthers
{
    void WakeOthers();
}

/// <summary>How deliveries are sent.</summary>
public sealed class WebhookSendingOptions
{
    /// <summary>The named HTTP client that sends: the application gives it its guard (no private addresses, no redirects).</summary>
    public string HttpClientName { get; set; } = "Webhooks";

    /// <summary>
    /// Each header goes under every prefix here (<c>{prefix}Event</c>, <c>Delivery</c>, <c>Timestamp</c>, <c>Signature</c>):
    /// the neutral <c>X-Webhook-</c> first, then any names receivers were set up with before.
    /// </summary>
    public List<string> HeaderPrefixes { get; } = ["X-Webhook-"];

    /// <summary>How much of a refusal's body is read, and how much of it is kept as the error.</summary>
    public int ReadAtMost { get; set; } = 1200;

    public int ErrorLength { get; set; } = 300;

    /// <summary>How long a server holds a delivery it claimed before another may try it.</summary>
    public TimeSpan Lease { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Deliveries claimed at each look.</summary>
    public int Batch { get; set; } = 20;

    /// <summary>How long sent and failed deliveries are kept.</summary>
    public TimeSpan KeepFor { get; set; } = TimeSpan.FromDays(30);
}

/// <summary>Wakes the delivery worker as soon as a delivery is queued, rather than at its next look.</summary>
public sealed class WebhookWakeup(IEnumerable<IWebhookWakeOthers> others)
{
    private readonly SemaphoreSlim _signal = new(0, 1);

    /// <summary>Wakes the worker on every server: whichever is free sends it.</summary>
    public void Signal()
    {
        WakeHere();
        foreach (IWebhookWakeOthers other in others)
        {
            other.WakeOthers();
        }
    }

    /// <summary>Wakes this server's worker only (another server queued the delivery).</summary>
    public void WakeHere()
    {
        if (_signal.CurrentCount == 0)
        {
            try
            {
                _signal.Release();
            }
            catch (SemaphoreFullException)
            {
                // already signalled
            }
        }
    }

    public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken) => _signal.WaitAsync(timeout, cancellationToken);
}

/// <summary>The work on the queue's table, each in a unit of work of its own or the caller's.</summary>
public abstract class WebhookTableWork(IUnitOfWorkManager units, IDbContextProvider<IWebhooksDbContext> tables)
{
    protected async Task<T> RunAsync<T>(Func<IWebhooksDbContext, Task<T>> work, bool requiresNew, CancellationToken cancellationToken)
    {
        using IUnitOfWork uow = units.Begin(requiresNew: requiresNew);
        IWebhooksDbContext db = await tables.GetDbContextAsync();
        T result = await work(db);
        await uow.CompleteAsync(cancellationToken);
        return result;
    }
}

/// <summary>Queues deliveries, and queues one again when an admin asks.</summary>
public sealed class WebhookQueue(IUnitOfWorkManager units, IDbContextProvider<IWebhooksDbContext> tables, WebhookWakeup wakeup) : WebhookTableWork(units, tables)
{
    /// <summary>
    /// Saves the deliveries on their own and wakes the worker. One already queued (an event heard twice: its id comes
    /// from <see cref="WebhookIds.Delivery"/>) is left as it is.
    /// </summary>
    public async Task EnqueueAsync(IReadOnlyCollection<WebhookDelivery> deliveries, CancellationToken cancellationToken)
    {
        if (deliveries.Count == 0)
        {
            return;
        }

        int added = await RunAsync(async db =>
        {
            List<Guid> ids = [.. deliveries.Select(d => d.Id)];
            HashSet<Guid> queued = [.. await db.WebhookDeliveries.Where(d => ids.Contains(d.Id)).Select(d => d.Id).ToListAsync(cancellationToken)];
            List<WebhookDelivery> fresh = [.. deliveries.Where(d => !queued.Contains(d.Id))];
            db.WebhookDeliveries.AddRange(fresh);
            await db.SaveChangesAsync(cancellationToken);
            return fresh.Count;
        }, requiresNew: true, cancellationToken);
        if (added > 0)
        {
            wakeup.Signal();
        }
    }

    /// <summary>
    /// A delivery's payload sent again, as a new delivery with a new id (so a receiver sees it's another try); null when the
    /// webhook has no such delivery.
    /// </summary>
    public async Task<WebhookDelivery?> RedeliverAsync(Guid webhookId, Guid deliveryId, CancellationToken cancellationToken)
    {
        WebhookDelivery? again = await RunAsync(async db =>
        {
            WebhookDelivery? old = await db.WebhookDeliveries.AsNoTracking().FirstOrDefaultAsync(d => d.Id == deliveryId && d.WebhookId == webhookId, cancellationToken);
            if (old is null)
            {
                return null;
            }

            Guid id = Guid.NewGuid();
            WebhookDelivery copy = new(id, webhookId, old.Event, old.ScopeId, old.SubjectId, old.Subject, WithDeliveryId(old.Payload, id), redeliveryOf: old.Id);
            db.WebhookDeliveries.Add(copy);
            await db.SaveChangesAsync(cancellationToken);
            return copy;
        }, requiresNew: true, cancellationToken);
        if (again is not null)
        {
            wakeup.Signal();
        }

        return again;
    }

    /// <summary>The payload with its <c>id</c> set to the delivery's.</summary>
    public static string WithDeliveryId(string payload, Guid deliveryId)
    {
        JsonObject json = JsonNode.Parse(payload)!.AsObject();
        json["id"] = deliveryId;
        return json.ToJsonString();
    }
}

/// <summary>
/// Sends queued deliveries: each due one is claimed by moving its next attempt on (another server that looked at the same
/// moment finds it gone), POSTed with its event, id, timestamp and signature under each header prefix, and marked sent
/// on a 2xx; anything else waits (<see cref="WebhookDelivery.Waits"/>) and is tried again, then fails for good. A delivery
/// to a webhook that's off waits while it's off; one to a removed webhook fails.
/// </summary>
public sealed class WebhookDispatcher(IUnitOfWorkManager units, IDbContextProvider<IWebhooksDbContext> tables, IWebhookEndpoints endpoints,
    IHttpClientFactory http, IOptions<WebhookSendingOptions> options) : WebhookTableWork(units, tables)
{
    /// <summary>Sends the deliveries that are due, a batch at a time.</summary>
    public async Task SendDueAsync(CancellationToken cancellationToken)
    {
        WebhookSendingOptions o = options.Value;
        DateTime now = DateTime.UtcNow;
        List<WebhookDelivery> due = await RunAsync(db => db.WebhookDeliveries.AsNoTracking()
            .Where(d => d.Status == WebhookDeliveryStatus.Queued && d.NextAttemptAt <= now).OrderBy(d => d.NextAttemptAt).Take(o.Batch).ToListAsync(cancellationToken),
            requiresNew: false, cancellationToken);
        foreach (WebhookDelivery candidate in due)
        {
            DateTime? seen = candidate.NextAttemptAt;
            int claimed = await RunAsync(db => db.WebhookDeliveries
                .Where(d => d.Id == candidate.Id && d.Status == WebhookDeliveryStatus.Queued && d.NextAttemptAt == seen)
                .ExecuteUpdateAsync(u => u.SetProperty(d => d.NextAttemptAt, now + o.Lease), cancellationToken), requiresNew: true, cancellationToken);
            if (claimed == 1)
            {
                await SendAsync(candidate.Id, cancellationToken);
            }
        }
    }

    /// <summary>Removes sent and failed deliveries older than <see cref="WebhookSendingOptions.KeepFor"/>.</summary>
    public Task<int> PruneAsync(CancellationToken cancellationToken)
    {
        DateTime before = DateTime.UtcNow - options.Value.KeepFor;
        return RunAsync(db => db.WebhookDeliveries.Where(d => d.CreatedAt < before && d.Status != WebhookDeliveryStatus.Queued).ExecuteDeleteAsync(cancellationToken),
            requiresNew: true, cancellationToken);
    }

    private Task SendAsync(Guid deliveryId, CancellationToken cancellationToken) => RunAsync(async db =>
    {
        WebhookDelivery delivery = await db.WebhookDeliveries.FirstAsync(d => d.Id == deliveryId, cancellationToken);
        WebhookEndpoint? endpoint = await endpoints.FindAsync(delivery.WebhookId, cancellationToken);
        DateTime now = DateTime.UtcNow;
        if (endpoint is null)
        {
            delivery.Failed(now, null, "The webhook was removed.");
        }
        else if (!endpoint.Enabled)
        {
            delivery.NextAttemptAt = now + TimeSpan.FromMinutes(1); // waits while it's off
        }
        else
        {
            await PostAsync(delivery, endpoint, cancellationToken);
        }

        return await db.SaveChangesAsync(cancellationToken);
    }, requiresNew: true, cancellationToken);

    /// <summary>One try: only the status is kept, and the start of a refusal; the rest of an answer isn't read.</summary>
    private async Task PostAsync(WebhookDelivery delivery, WebhookEndpoint endpoint, CancellationToken cancellationToken)
    {
        WebhookSendingOptions o = options.Value;
        Stopwatch clock = Stopwatch.StartNew();
        try
        {
            string timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
            using HttpRequestMessage request = new(HttpMethod.Post, endpoint.Url) { Content = new StringContent(delivery.Payload, Encoding.UTF8, "application/json") };
            string signature = WebhookSignature.Header(endpoint.Secret, timestamp, delivery.Payload);
            foreach (string prefix in o.HeaderPrefixes)
            {
                request.Headers.Add(prefix + "Event", delivery.Event);
                request.Headers.Add(prefix + "Delivery", delivery.Id.ToString());
                request.Headers.Add(prefix + "Timestamp", timestamp);
                request.Headers.Add(prefix + "Signature", signature);
            }

            using HttpClient client = http.CreateClient(o.HttpClientName);
            using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            delivery.DurationMs = (int)clock.ElapsedMilliseconds;
            if (response.IsSuccessStatusCode)
            {
                delivery.Sent(DateTime.UtcNow, (int)response.StatusCode);
            }
            else
            {
                string body = await ReadAtMostAsync(response.Content, o.ReadAtMost, cancellationToken);
                delivery.Failed(DateTime.UtcNow, (int)response.StatusCode, $"{(int)response.StatusCode} {response.ReasonPhrase}: {body[..Math.Min(body.Length, o.ErrorLength)]}");
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or CryptographicException or IOException && !cancellationToken.IsCancellationRequested)
        {
            delivery.DurationMs = (int)clock.ElapsedMilliseconds;
            delivery.Failed(DateTime.UtcNow, null, ex.Message);
        }
    }

    private static async Task<string> ReadAtMostAsync(HttpContent content, int bytes, CancellationToken cancellationToken)
    {
        await using Stream stream = await content.ReadAsStreamAsync(cancellationToken);
        byte[] buffer = new byte[bytes];
        int read = 0;
        while (read < bytes && await stream.ReadAsync(buffer.AsMemory(read, bytes - read), cancellationToken) is var n and > 0)
        {
            read += n;
        }

        return Encoding.UTF8.GetString(buffer, 0, read);
    }
}

/// <summary>The worker on each server: sends what's due when woken, or every five seconds, and prunes once an hour.</summary>
public sealed class WebhookDeliveryWorker(IServiceScopeFactory scopes, WebhookWakeup wakeup, ILogger<WebhookDeliveryWorker> logger) : BackgroundService
{
    private DateTime _pruned = DateTime.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using AsyncServiceScope scope = scopes.CreateAsyncScope();
                WebhookDispatcher dispatcher = scope.ServiceProvider.GetRequiredService<WebhookDispatcher>();
                await dispatcher.SendDueAsync(stoppingToken);
                if (DateTime.UtcNow - _pruned > TimeSpan.FromHours(1))
                {
                    await dispatcher.PruneAsync(stoppingToken);
                    _pruned = DateTime.UtcNow;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Sending webhook deliveries failed; trying again shortly.");
            }

            await wakeup.WaitAsync(TimeSpan.FromSeconds(5), stoppingToken);
        }
    }
}

/// <summary>The delivery queue's services, for the application's module to add.</summary>
public static class WebhookDeliveryServices
{
    public static IServiceCollection AddWebhookDeliveries(this IServiceCollection services)
    {
        services.AddSingleton<WebhookWakeup>();
        services.AddTransient<WebhookQueue>();
        services.AddTransient<WebhookDispatcher>();
        services.AddHostedService<WebhookDeliveryWorker>();
        services.AddOptions<WebhookSendingOptions>();
        return services;
    }
}
