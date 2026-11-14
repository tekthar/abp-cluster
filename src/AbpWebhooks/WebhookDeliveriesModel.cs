using Microsoft.EntityFrameworkCore;
using Volo.Abp.EntityFrameworkCore;

namespace AbpWebhooks;

/// <summary>
/// The delivery queue's table, in the application's own context: the context implements this, maps the table with
/// <see cref="WebhookDeliveriesModel.ConfigureWebhookDeliveries"/>, and stands in for it
/// (<c>options.ReplaceDbContext&lt;IWebhooksDbContext&gt;()</c>), as for any ABP module's tables.
/// </summary>
public interface IWebhooksDbContext : IEfCoreDbContext
{
    DbSet<WebhookDelivery> WebhookDeliveries { get; }
}

/// <summary>
/// The names of the columns that say what a delivery is about, so a table made before keeps its own. The rest are named
/// as the properties are.
/// </summary>
public sealed record WebhookDeliveryColumns(string ScopeId = "ScopeId", string SubjectId = "SubjectId", string Subject = "Subject");

public static class WebhookDeliveriesModel
{
    /// <summary>Maps the delivery queue: <paramref name="table"/> in <paramref name="schema"/> (the context's default when null).</summary>
    public static ModelBuilder ConfigureWebhookDeliveries(this ModelBuilder builder, string table = "webhook_deliveries", string? schema = null,
        WebhookDeliveryColumns? columns = null)
    {
        columns ??= new WebhookDeliveryColumns();
        builder.Entity<WebhookDelivery>(b =>
        {
            b.ToTable(table, schema);
            b.Property(x => x.Event).HasMaxLength(64).IsRequired();
            b.Property(x => x.ScopeId).HasColumnName(columns.ScopeId);
            b.Property(x => x.SubjectId).HasColumnName(columns.SubjectId);
            b.Property(x => x.Subject).HasColumnName(columns.Subject).HasMaxLength(64);
            b.Property(x => x.Payload).HasColumnType("jsonb").IsRequired();
            b.Property(x => x.Status).HasMaxLength(16).IsRequired();
            b.HasIndex(x => new { x.WebhookId, x.CreatedAt });
            b.HasIndex(x => new { x.Status, x.NextAttemptAt });
        });
        return builder;
    }
}
