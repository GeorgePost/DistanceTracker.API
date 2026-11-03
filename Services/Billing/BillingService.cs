using DistanceTracker.API.Data;
using DistanceTracker.API.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DistanceTracker.API.Services.Billing;

public sealed record SubscriptionStatusResponse(string Tier, string? Status, bool CancelAtPeriodEnd,
    DateTime? CurrentPeriodEndUtc, bool CanCreateCheckout);
public sealed class BillingConflictException(string message) : Exception(message);
public sealed class BillingUnavailableException : Exception;
public sealed class BillingUserNotFoundException : Exception;
public sealed class CustomerCreationUncertainException : Exception;

public sealed class BillingService(DistanceTrackerContext db, IStripeGateway stripe,
    IOptions<StripeSettings> options, TimeProvider clock, ILogger<BillingService> logger)
{
    public static readonly HashSet<string> HandledEvents = new(StringComparer.Ordinal)
    {
        "checkout.session.completed", "checkout.session.async_payment_succeeded",
        "checkout.session.async_payment_failed", "checkout.session.expired",
        "customer.subscription.created", "customer.subscription.updated",
        "customer.subscription.deleted", "customer.subscription.paused", "customer.subscription.resumed",
        "invoice.paid", "invoice.payment_failed", "invoice.payment_action_required"
    };

    public static bool GrantsAccess(string? status, bool allowPastDue) =>
        status is "active" or "trialing" || (status == "past_due" && allowPastDue);

    // Also block unpaid/incomplete subscriptions: recover or cancel them through the portal.
    public static bool BlocksCheckout(string? status) =>
        status is not null and not "canceled" and not "incomplete_expired";

    private void EnsureEnabled()
    {
        if (!options.Value.Enabled) throw new BillingUnavailableException();
    }

    private async Task LockAsync(string key, CancellationToken ct)
    {
        // Cross-process transaction lock. All billing reads from Stripe happen AFTER this lock.
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({key}, 0))", ct);
    }

    private async Task<ApplicationUser> LoadUserAsync(string id, CancellationToken ct)
    {
        await LockAsync("billing:user:" + id, ct);
        return await db.Users.SingleOrDefaultAsync(u => u.Id == id, ct)
            ?? throw new BillingUserNotFoundException();
    }

    private async Task<IReadOnlyList<BillingSubscription>> ReconcileAsync(ApplicationUser user, CancellationToken ct)
    {
        IReadOnlyList<BillingSubscription> subscriptions = user.StripeCustomerId is null ? []
            : await stripe.GetSubscriptionsAsync(user.StripeCustomerId, ct);
        // Use current provider state, not the event snapshot. A late deletion for an old
        // subscription must not revoke a different active subscription.

        var selected = subscriptions
            .OrderByDescending(s => GrantsAccess(s.Status, options.Value.AllowPastDueAccess))
            .ThenByDescending(s => BlocksCheckout(s.Status))
            .ThenByDescending(s => s.PeriodEndUtc)
            .ThenBy(s => s.Id, StringComparer.Ordinal).FirstOrDefault();
        user.StripeSubscriptionId = selected?.Id;
        user.StripeSubscriptionStatus = selected?.Status;
        user.StripeCancelAtPeriodEnd = selected?.CancelAtPeriodEnd ?? false;
        user.StripeCurrentPeriodEndUtc = selected?.PeriodEndUtc;
        user.StripeLastSyncedAtUtc = clock.GetUtcNow().UtcDateTime;
        // Identity updates also write user billing fields; reject stale concurrent snapshots.
        user.ConcurrencyStamp = Guid.NewGuid().ToString();
        user.Tier = GrantsAccess(selected?.Status, options.Value.AllowPastDueAccess)
            ? UserTier.Paid : UserTier.Free;
        return subscriptions;
    }

    private void StartCustomerAttempt(ApplicationUser user)
    {
        user.StripeCustomerCreationAttemptId = Guid.NewGuid().ToString("N");
        user.StripeCustomerCreationEmail = user.Email;
        user.StripeCustomerCreationStartedAtUtc = clock.GetUtcNow().UtcDateTime;
        user.StripeCustomerCreationNeedsReview = false;
        user.ConcurrencyStamp = Guid.NewGuid().ToString();
    }

    private static void LinkCustomer(ApplicationUser user, string customerId)
    {
        user.StripeCustomerId = customerId;
        user.StripeCustomerCreationAttemptId = null;
        user.StripeCustomerCreationEmail = null;
        user.StripeCustomerCreationStartedAtUtc = null;
        user.StripeCustomerCreationNeedsReview = false;
        user.ConcurrencyStamp = Guid.NewGuid().ToString();
    }

    private async Task EnsureCustomerAsync(string userId, CancellationToken ct)
    {
        // Commit the exact key and email before the provider call. Timeouts, restarts and
        // simultaneous Identity edits must not change retry parameters.
        await using (var prepare = await db.Database.BeginTransactionAsync(ct))
        {
            var user = await LoadUserAsync(userId, ct);
            if (user.StripeCustomerId is not null) { await prepare.CommitAsync(ct); return; }
            await ReconcileAsync(user, ct); // A stale Paid flag without a customer is not access.
            if (user.StripeCustomerCreationAttemptId is null) StartCustomerAttempt(user);
            await db.SaveChangesAsync(ct);
            await prepare.CommitAsync(ct);
        }

        db.ChangeTracker.Clear();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var pending = await LoadUserAsync(userId, ct);
        if (pending.StripeCustomerId is not null) { await tx.CommitAsync(ct); return; }

        var recovered = await stripe.FindCustomerAsync(userId, ct);
        if (recovered is not null)
        {
            LinkCustomer(pending, recovered);
        }
        else
        {
            // Absence from a list is not proof that an indeterminate request cannot
            // finish later. Do not rotate a failed key or replay one beyond Stripe's window.
            if (pending.StripeCustomerCreationNeedsReview ||
                pending.StripeCustomerCreationStartedAtUtc is null ||
                pending.StripeCustomerCreationStartedAtUtc <= clock.GetUtcNow().UtcDateTime.AddHours(-24))
                throw new BillingConflictException("Billing setup needs support review before checkout can continue.");
            try
            {
                var customerId = await stripe.CreateCustomerAsync(userId, pending.StripeCustomerCreationEmail,
                    pending.StripeCustomerCreationAttemptId!, ct);
                LinkCustomer(pending, customerId);
            }
            catch (CustomerCreationUncertainException)
            {
                pending.StripeCustomerCreationNeedsReview = true;
                pending.ConcurrencyStamp = Guid.NewGuid().ToString();
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                throw;
            }
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    // Operator-only maintenance path: no HTTP endpoint and no customer/payment creation.
    public async Task<string> RecoverCustomerAsync(string userId, bool confirmedNoCustomer, CancellationToken ct)
    {
        EnsureEnabled();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var user = await LoadUserAsync(userId, ct);
        if (user.StripeCustomerId is not null) return "A Stripe customer is already linked; no reset was performed.";
        var recovered = await stripe.FindCustomerAsync(userId, ct);
        if (recovered is not null)
        {
            LinkCustomer(user, recovered);
            await ReconcileAsync(user, ct);
        }
        else
        {
            if (!confirmedNoCustomer)
                throw new BillingConflictException("No customer found, but the original outcome is unknown. Inspect Stripe requests first; use --confirmed-no-customer only after confirming no customer was created and no request remains in flight.");
            await ReconcileAsync(user, ct);
            StartCustomerAttempt(user);
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return recovered is not null ? "Recovered the existing Stripe customer."
            : "Customer attempt reset after operator confirmation. Retry checkout to create a customer.";
    }

    public async Task<BillingCheckout> CreateCheckoutAsync(string userId, CancellationToken ct)
    {
        EnsureEnabled();
        await EnsureCustomerAsync(userId, ct);
        db.ChangeTracker.Clear();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var user = await LoadUserAsync(userId, ct);
        var subscriptions = await ReconcileAsync(user, ct);
        if (subscriptions.Any(s => BlocksCheckout(s.Status)))
        {
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            throw new BillingConflictException("A subscription already exists. Use the billing portal to manage it.");
        }
        if (user.StripeCheckoutSessionId is not null)
        {
            var pending = await stripe.GetCheckoutAsync(user.StripeCheckoutSessionId, ct);
            if (pending is { Status: "open", Url: not null } && pending.ExpiresAtUtc > clock.GetUtcNow().UtcDateTime)
            {
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                return pending;
            }
            if (pending is { Status: "complete" })
            {
                // Browser completion does not take our database lock. The first list
                // may predate the purchase, so re-read after observing completion.
                subscriptions = await ReconcileAsync(user, ct);
                var completedSubscription = subscriptions.SingleOrDefault(s => s.Id == pending.SubscriptionId);
                if (completedSubscription is null || BlocksCheckout(completedSubscription.Status) ||
                    subscriptions.Any(s => BlocksCheckout(s.Status)))
                {
                    await db.SaveChangesAsync(ct);
                    await tx.CommitAsync(ct);
                    throw new BillingConflictException("The previous checkout completed. Refresh subscription status or use the billing portal; its subscription must be resolved before another checkout.");
                }
            }
            else if (pending is not null && pending.Status != "expired")
            {
                // Local clock expiry is not proof that Stripe can no longer accept payment.
                throw new BillingConflictException("The previous checkout is still unresolved. Please retry later.");
            }
            // Rotate only after an existing session is no longer reusable. The old
            // generation survives transaction rollback so retries use the same key.
            user.StripeCheckoutGeneration++;
        }
        var recovered = await stripe.FindOpenCheckoutAsync(user.Id, user.StripeCustomerId!, ct);
        var checkout = recovered ?? await stripe.CreateCheckoutAsync(user.Id, user.StripeCustomerId!,
            user.StripeCheckoutGeneration, ct);
        if (string.IsNullOrWhiteSpace(checkout.Url))
            throw new InvalidOperationException("Stripe did not return a checkout URL.");
        user.StripeCheckoutSessionId = checkout.Id;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return checkout;
    }

    public async Task<SubscriptionStatusResponse> GetStatusAsync(string userId, CancellationToken ct)
    {
        EnsureEnabled();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var user = await LoadUserAsync(userId, ct);
        var subscriptions = await ReconcileAsync(user, ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new(user.Tier.ToString(), user.StripeSubscriptionStatus, user.StripeCancelAtPeriodEnd,
            user.StripeCurrentPeriodEndUtc, !subscriptions.Any(s => BlocksCheckout(s.Status)));
    }

    public async Task<string> CreatePortalAsync(string userId, CancellationToken ct)
    {
        EnsureEnabled();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var user = await LoadUserAsync(userId, ct);
        if (user.StripeCustomerId is null)
            throw new BillingConflictException("No billing customer exists. Start checkout first.");
        var url = await stripe.CreatePortalAsync(user.StripeCustomerId, ct);
        await tx.CommitAsync(ct);
        return url;
    }

    public async Task ProcessEventAsync(BillingEvent message, CancellationToken ct)
    {
        EnsureEnabled();
        if (!HandledEvents.Contains(message.Type)) return;
        if (string.IsNullOrWhiteSpace(message.Id) || string.IsNullOrWhiteSpace(message.CustomerId))
            throw new InvalidWebhookException();

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockAsync("billing:event:" + message.Id, ct);
        if (await db.ProcessedStripeEvents.AnyAsync(e => e.Id == message.Id, ct)) return;

        var userId = await db.Users.Where(u => u.StripeCustomerId == message.CustomerId)
            .Select(u => u.Id).SingleOrDefaultAsync(ct);
        userId ??= message.UserId;
        if (userId is not null)
        {
            var user = await LoadUserAsync(userId, ct);
            if (user.StripeCustomerId is not null && user.StripeCustomerId != message.CustomerId)
                throw new InvalidOperationException("Webhook customer does not match the billing user.");
            user.StripeCustomerId ??= message.CustomerId;
            await ReconcileAsync(user, ct);
        }
        else
        {
            // The account may also sell unrelated products; never map customers by email.
            logger.LogInformation("Ignoring Stripe event {EventId}: no application billing customer.", message.Id);
        }

        db.ProcessedStripeEvents.Add(new()
        {
            Id = message.Id, Type = message.Type, ProcessedAtUtc = clock.GetUtcNow().UtcDateTime
        });
        // Receipt and entitlement changes commit together; a DB failure leaves the event retryable.
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }
}
