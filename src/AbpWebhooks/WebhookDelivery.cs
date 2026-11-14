using Volo.Abp.Domain.Entities;

namespace AbpWebhooks;

/// <summary>Where a delivery stands: waiting (for its first try or a retry), sent, or failed for good.</summary>
public static class WebhookDeliveryStatus
{
    public const string Queued = "queued";
    public const string Sent = "sent";
    public const string Failed = "failed";
}

/// <summary>
/// One event sent, or to send, to a webhook: its payload as built when it happened, and every try. What it's about is
/// the application's: a scope (a project, a tenant), a subject's id and its name as people know it (a record and its key).
/// </summary>
public class WebhookDelivery : Entity<Guid>
{
    /// <summary>How long to wait after each failure; after the last, it fails for good.</summary>
    public static readonly IReadOnlyList<TimeSpan> Waits = [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15), TimeSpan.FromHours(1), TimeSpan.FromHours(4)];

    protected WebhookDelivery()
    {
    }

    public WebhookDelivery(Guid id, Guid webhookId, string @event, Guid? scopeId, Guid? subjectId, string? subject, string payload, Guid? redeliveryOf = null) : base(id)
    {
        WebhookId = webhookId;
        Event = @event;
        ScopeId = scopeId;
        SubjectId = subjectId;
        Subject = subject;
        Payload = payload;
        RedeliveryOf = redeliveryOf;
        CreatedAt = DateTime.UtcNow;
        NextAttemptAt = CreatedAt;
    }

    public Guid WebhookId { get; private set; }
    public string Event { get; private set; } = "";

    /// <summary>Where what it's about belongs, for the application to list deliveries by (a project).</summary>
    public Guid? ScopeId { get; private set; }

    /// <summary>What it's about (a record), when it's about one.</summary>
    public Guid? SubjectId { get; private set; }

    /// <summary>What it's about as people know it (a record's key).</summary>
    public string? Subject { get; private set; }

    public string Payload { get; private set; } = "{}";
    public string Status { get; set; } = WebhookDeliveryStatus.Queued;
    public int Attempts { get; set; }
    public DateTime? NextAttemptAt { get; set; }
    public int? LastStatusCode { get; set; }
    public string? LastError { get; set; }
    public int? DurationMs { get; set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime? SentAt { get; set; }

    /// <summary>The delivery this one sends again, when an admin asked.</summary>
    public Guid? RedeliveryOf { get; private set; }

    /// <summary>After a failed try: wait longer each time, then give up.</summary>
    public void Failed(DateTime now, int? statusCode, string error)
    {
        Attempts++;
        (LastStatusCode, LastError) = (statusCode, error);
        if (Attempts > Waits.Count)
        {
            (Status, NextAttemptAt) = (WebhookDeliveryStatus.Failed, null);
        }
        else
        {
            (Status, NextAttemptAt) = (WebhookDeliveryStatus.Queued, now + Waits[Attempts - 1]);
        }
    }

    public void Sent(DateTime now, int statusCode)
    {
        Attempts++;
        (Status, NextAttemptAt, LastStatusCode, LastError, SentAt) = (WebhookDeliveryStatus.Sent, null, statusCode, null, now);
    }
}
