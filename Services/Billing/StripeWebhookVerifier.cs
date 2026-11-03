using Microsoft.Extensions.Options;
using Stripe;

namespace DistanceTracker.API.Services.Billing;

public sealed class StripeWebhookVerifier(IOptions<StripeSettings> options)
{
    public BillingEvent Verify(string body, string signature)
    {
        if (string.IsNullOrWhiteSpace(signature))
            throw new InvalidWebhookException();
        Event stripeEvent;
        try
        {
            // Reject malformed envelopes before handing them to the SDK converter.
            using var json = System.Text.Json.JsonDocument.Parse(body);
            var root = json.RootElement;
            if (root.ValueKind != System.Text.Json.JsonValueKind.Object ||
                !root.TryGetProperty("request", out _) ||
                !root.TryGetProperty("data", out var data) ||
                data.ValueKind != System.Text.Json.JsonValueKind.Object ||
                !data.TryGetProperty("object", out var payload) ||
                payload.ValueKind != System.Text.Json.JsonValueKind.Object)
                throw new InvalidWebhookException();
            // Keep API-version validation enabled. Configure snapshot events for this SDK's API version.
            stripeEvent = EventUtility.ConstructEvent(body, signature, options.Value.WebhookSecret);
        }
        catch (Exception ex) when (ex is StripeException or ArgumentException or
            Newtonsoft.Json.JsonException or System.Text.Json.JsonException)
        {
            throw new InvalidWebhookException();
        }
        if (stripeEvent?.Data?.Object is null || string.IsNullOrWhiteSpace(stripeEvent.Id))
            throw new InvalidWebhookException();
        var customerId = stripeEvent.Data.Object switch
        {
            Stripe.Checkout.Session session => session.CustomerId,
            Subscription subscription => subscription.CustomerId,
            Invoice invoice => invoice.CustomerId,
            _ => null
        };
        if (stripeEvent.Livemode != options.Value.SecretKey.StartsWith("sk_live_"))
            throw new InvalidWebhookException();
        var metadata = stripeEvent.Data.Object switch
        {
            Stripe.Checkout.Session session => session.Metadata,
            Subscription subscription => subscription.Metadata,
            _ => null
        };

        var userId = metadata is not null && metadata.TryGetValue("userId", out var id) ? id : null;
        return new(stripeEvent.Id, stripeEvent.Type, customerId, stripeEvent.Livemode, userId);
    }
}

public sealed class InvalidWebhookException : Exception;
