namespace AbpWebhooks.Tests;

/// <summary>
/// What a receiver sees: the signature over a known body and a delivery's id for a known event and webhook, against
/// values worked out apart from this code (Python's hmac and uuid); the receiver's check; and the waits between tries.
/// </summary>
public class SignatureTests
{
    private const string Secret = "whsec_test";
    private const string Body = "{\"event\":\"record.created\"}";
    private static readonly DateTimeOffset At = DateTimeOffset.FromUnixTimeSeconds(1700000000);

    [Fact]
    public void The_signature_is_hmac_sha256_of_the_timestamp_and_body_in_lowercase_hex()
    {
        Assert.Equal("66aa3bae19d510e9de0df97cbe4b5992e2a3ca7f3286480c583ae1cd7a301e43", WebhookSignature.Sign(Secret, "1700000000", Body));
        Assert.Equal("sha256=66aa3bae19d510e9de0df97cbe4b5992e2a3ca7f3286480c583ae1cd7a301e43", WebhookSignature.Header(Secret, "1700000000", Body));
    }

    [Fact]
    public void A_delivery_id_comes_from_the_event_and_the_webhook()
    {
        Guid eventId = Guid.Parse("11111111-2222-3333-4444-555555555555");
        Guid webhook = Guid.Parse("66666666-7777-8888-9999-aaaaaaaaaaaa");
        Assert.Equal(Guid.Parse("1390a6f4-ff03-8f52-ad01-381d80e9a694"), WebhookIds.Delivery(eventId, webhook));
        Assert.NotEqual(WebhookIds.Delivery(eventId, webhook), WebhookIds.Delivery(webhook, eventId));
    }

    [Fact]
    public void The_body_as_signed_passes_and_nothing_else_does()
    {
        string header = WebhookSignature.Header(Secret, "1700000000", Body);
        Assert.True(WebhookSignature.Verify(Secret, "1700000000", Body, header, At));
        Assert.False(WebhookSignature.Verify(Secret, "1700000000", Body + " ", header, At));
        Assert.False(WebhookSignature.Verify("whsec_other", "1700000000", Body, header, At));
        Assert.False(WebhookSignature.Verify(Secret, "1700000000", Body, null, At));
        Assert.False(WebhookSignature.Verify(Secret, "1700000000", Body, header.ToUpperInvariant(), At));
    }

    [Fact]
    public void Only_a_fresh_delivery_passes()
    {
        string header = WebhookSignature.Header(Secret, "1700000000", Body);
        Assert.True(WebhookSignature.Verify(Secret, "1700000000", Body, header, At.AddMinutes(4)));
        Assert.False(WebhookSignature.Verify(Secret, "1700000000", Body, header, At.AddMinutes(6)));
        Assert.True(WebhookSignature.Verify(Secret, "1700000000", Body, header, At.AddHours(1), TimeSpan.FromHours(2)));
        Assert.False(WebhookSignature.Verify(Secret, "-1", Body, header, At));
        Assert.False(WebhookSignature.Verify(Secret, "soon", Body, header, At));
    }

    [Fact]
    public void A_failed_delivery_waits_longer_each_time_then_fails_for_good()
    {
        WebhookDelivery delivery = new(Guid.NewGuid(), Guid.NewGuid(), "record.created", null, null, null, "{}");
        DateTime now = DateTime.UtcNow;
        for (int i = 0; i < WebhookDelivery.Waits.Count; i++)
        {
            delivery.Failed(now, 500, "no");
            Assert.Equal((WebhookDeliveryStatus.Queued, now + WebhookDelivery.Waits[i]), (delivery.Status, delivery.NextAttemptAt));
        }

        delivery.Failed(now, 500, "no");
        Assert.Equal((WebhookDeliveryStatus.Failed, null, 6), (delivery.Status, delivery.NextAttemptAt, delivery.Attempts));
        Assert.Equal([TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15), TimeSpan.FromHours(1), TimeSpan.FromHours(4)], WebhookDelivery.Waits);
    }
}
