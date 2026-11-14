using Microsoft.Extensions.DependencyInjection;
using Volo.Abp.EntityFrameworkCore;
using Volo.Abp.Modularity;

namespace AbpWebhooks;

/// <summary>
/// Signed outgoing webhooks. The application lists this module in its DependsOn, maps the queue in its own context
/// (<see cref="WebhookDeliveriesModel.ConfigureWebhookDeliveries"/>, the context implementing <see cref="IWebhooksDbContext"/>
/// and standing in for it), and registers its <see cref="IWebhookEndpoints"/>: where each of its webhooks sends and
/// with what secret. It queues deliveries with <see cref="WebhookQueue"/>; the worker on each server sends them.
/// The client that sends (<see cref="WebhookSendingOptions.HttpClientName"/>, "Webhooks" with a 10-second timeout by
/// default) goes wherever an admin typed: give it the application's guard (no private addresses, no redirects).
/// </summary>
[DependsOn(typeof(AbpEntityFrameworkCoreModule))]
public class AbpWebhooksModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        context.Services.AddWebhookDeliveries();
        context.Services.AddHttpClient(new WebhookSendingOptions().HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(10));
    }
}
