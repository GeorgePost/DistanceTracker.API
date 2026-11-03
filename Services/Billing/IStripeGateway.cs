namespace DistanceTracker.API.Services.Billing;

public sealed record BillingSubscription(string Id, string Status, bool CancelAtPeriodEnd, DateTime? PeriodEndUtc);
public sealed record BillingCheckout(string Id, string? Url, string Status, DateTime ExpiresAtUtc, string? SubscriptionId = null);
public sealed record BillingEvent(string Id, string Type, string? CustomerId, bool LiveMode, string? UserId = null);

public interface IStripeGateway
{
    Task ValidatePriceAsync(CancellationToken ct);
    Task<string?> FindCustomerAsync(string userId, CancellationToken ct);
    Task<string> CreateCustomerAsync(string userId, string? email, string attemptId, CancellationToken ct);
    Task<IReadOnlyList<BillingSubscription>> GetSubscriptionsAsync(string customerId, CancellationToken ct);
    Task<BillingCheckout?> FindOpenCheckoutAsync(string userId, string customerId, CancellationToken ct);
    Task<BillingCheckout?> GetCheckoutAsync(string sessionId, CancellationToken ct);
    Task<BillingCheckout> CreateCheckoutAsync(string userId, string customerId, int generation, CancellationToken ct);
    Task<string> CreatePortalAsync(string customerId, CancellationToken ct);
}
