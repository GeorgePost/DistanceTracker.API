using Microsoft.Extensions.Options;
using Stripe;
using Checkout = Stripe.Checkout;
using Portal = Stripe.BillingPortal;

namespace DistanceTracker.API.Services.Billing;

public sealed class StripeGateway(IStripeClient client, IOptions<StripeSettings> options,
    IConfiguration configuration) : IStripeGateway
{
    private StripeSettings Settings => options.Value;
    private string Frontend => configuration["Frontend:BaseUrl"]!.TrimEnd('/');

    public async Task ValidatePriceAsync(CancellationToken ct)
    {
        var price = await new PriceService(client).GetAsync(Settings.PriceId, cancellationToken: ct);
        if (!price.Active || price.Type != "recurring" ||
            price.Livemode != Settings.SecretKey.StartsWith("sk_live_"))
            throw new InvalidOperationException("Stripe:PriceId must identify an active recurring price in the configured API key's mode.");
    }

    public async Task<string?> FindCustomerAsync(string userId, CancellationToken ct)
    {
        string? match = null;
        // Direct list pagination, not eventually-consistent search. Never match by email.
        await foreach (var customer in new CustomerService(client).ListAutoPagingAsync(
            new CustomerListOptions { Limit = 100 }, cancellationToken: ct))
        {
            if (customer.Metadata.GetValueOrDefault("userId") != userId) continue;
            if (match is not null)
                throw new BillingConflictException("Multiple billing customers match this user. Operator review is required.");
            match = customer.Id;
        }
        return match;
    }

    public async Task<string> CreateCustomerAsync(string userId, string? email, string attemptId, CancellationToken ct)
    {
        try
        {
            var customer = await new CustomerService(client).CreateAsync(new CustomerCreateOptions
            {
                Email = email,
                Metadata = new() { ["userId"] = userId, ["customerAttemptId"] = attemptId }
            }, new RequestOptions { IdempotencyKey = $"distancetracker:customer:{userId}:{attemptId}" }, ct);
            return customer.Id;
        }
        catch (StripeException ex) when (ex.HttpStatusCode == System.Net.HttpStatusCode.InternalServerError)
        {
            // Stripe v1 caches this response. A new key is unsafe until the outcome is resolved.
            throw new CustomerCreationUncertainException();
        }
    }
    public async Task<IReadOnlyList<BillingSubscription>> GetSubscriptionsAsync(string customerId, CancellationToken ct)
    {
        var result = new List<BillingSubscription>();
        await foreach (var subscription in new SubscriptionService(client).ListAutoPagingAsync(
            new SubscriptionListOptions { Customer = customerId, Status = "all", Limit = 100 },
            cancellationToken: ct))
        {
            var matchingItems = subscription.Items.Data.Where(i => i.Price.Id == Settings.PriceId).ToList();
            if (matchingItems.Count == 0) continue; // Never grant access for another product.
            result.Add(new(subscription.Id, subscription.Status, subscription.CancelAtPeriodEnd,
                matchingItems.Max(i => (DateTime?)i.CurrentPeriodEnd)));
        }
        return result;
    }

    public async Task<BillingCheckout?> FindOpenCheckoutAsync(string userId, string customerId, CancellationToken ct)
    {
        await foreach (var session in new Checkout.SessionService(client).ListAutoPagingAsync(
            new Checkout.SessionListOptions { Customer = customerId, Status = "open", Limit = 100 },
            cancellationToken: ct))
        {
            if (session.ClientReferenceId == userId && session.Mode == "subscription" &&
                session.Metadata.GetValueOrDefault("priceId") == Settings.PriceId)
                return new(session.Id, session.Url, session.Status, session.ExpiresAt, session.SubscriptionId);
        }
        return null;
    }

    public async Task<BillingCheckout?> GetCheckoutAsync(string sessionId, CancellationToken ct)
    {
        try
        {
            var session = await new Checkout.SessionService(client).GetAsync(sessionId, cancellationToken: ct);
            return new(session.Id, session.Url, session.Status, session.ExpiresAt, session.SubscriptionId);
        }
        catch (StripeException ex) when (ex.StripeError?.Code == "resource_missing")
        {
            return null;
        }
    }

    public async Task<BillingCheckout> CreateCheckoutAsync(string userId, string customerId, int generation, CancellationToken ct)
    {
        var metadata = new Dictionary<string, string> { ["userId"] = userId, ["priceId"] = Settings.PriceId };
        var session = await new Checkout.SessionService(client).CreateAsync(new Checkout.SessionCreateOptions
        {
            Mode = "subscription",
            Customer = customerId,
            ClientReferenceId = userId,
            LineItems = [new() { Price = Settings.PriceId, Quantity = 1 }],
            Metadata = metadata,
            SubscriptionData = new() { Metadata = metadata },
            SuccessUrl = Frontend + "/success?session_id={CHECKOUT_SESSION_ID}",
            CancelUrl = Frontend + "/cancel"
        }, new RequestOptions { IdempotencyKey = $"distancetracker:checkout:{userId}:{generation}" }, ct);
        return new(session.Id, session.Url, session.Status, session.ExpiresAt, session.SubscriptionId);
    }

    public async Task<string> CreatePortalAsync(string customerId, CancellationToken ct)
    {
        var session = await new Portal.SessionService(client).CreateAsync(new Portal.SessionCreateOptions
        {
            Customer = customerId,
            ReturnUrl = Frontend + "/account"
        }, cancellationToken: ct);
        return session.Url;
    }
}
