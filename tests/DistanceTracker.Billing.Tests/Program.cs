using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DistanceTracker.API.Controllers;
using DistanceTracker.API.Data;
using DistanceTracker.API.Models;
using DistanceTracker.API.Services.Billing;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Stripe;
using BillingService = DistanceTracker.API.Services.Billing.BillingService;

var connection = Environment.GetEnvironmentVariable("BILLING_TEST_CONNECTION")
    ?? throw new Exception("Run scripts/Test-Billing.ps1 to provision a disposable PostgreSQL database.");
if (!new Npgsql.NpgsqlConnectionStringBuilder(connection).Database!.StartsWith("billing_tests_"))
    throw new Exception("Tests require a database named billing_tests_*; refusing another database.");
var contextOptions = new DbContextOptionsBuilder<DistanceTrackerContext>().UseNpgsql(connection).Options;
var settings = new StripeSettings { SecretKey = "sk_test_fake", WebhookSecret = "whsec_fake", PriceId = "price_fake" };
var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    { ["Frontend:BaseUrl"] = "http://localhost:3000" }).Build();
var passed = 0;
async Task Test(string name, Func<Task> action)
{
    await action();
    passed++;
    Console.WriteLine("PASS: " + name);
}
void Check(bool condition, string message = "Assertion failed") { if (!condition) throw new Exception(message); }
async Task Throws<T>(Func<Task> action) where T : Exception
{
    try { await action(); } catch (T) { return; }
    throw new Exception("Expected " + typeof(T).Name);
}
DistanceTrackerContext Db() => new(contextOptions);
BillingService Service(DistanceTrackerContext db, FakeStripe fake, bool grace = true) =>
    new(db, fake, Options.Create(new StripeSettings { AllowPastDueAccess = grace }),
        TimeProvider.System, NullLogger<BillingService>.Instance);
async Task<string> User(string? customer = null)
{
    var id = Guid.NewGuid().ToString();
    await using var db = Db();
    db.Users.Add(new() { Id = id, Email = id + "@example.invalid", StripeCustomerId = customer });
    await db.SaveChangesAsync();
    return id;
}
async Task<ApplicationUser> ReadUser(string id)
{
    await using var db = Db();
    return await db.Users.SingleAsync(u => u.Id == id);
}
BillingEvent Event(string customer, string type = "customer.subscription.updated", string? id = null) =>
    new(id ?? "evt_" + Guid.NewGuid().ToString("N"), type, customer, false);

await Test("Migration upgrades existing schema and preserves users; rerun is safe", async () =>
{
    await using var db = Db();
    await db.GetService<IMigrator>().MigrateAsync("20260718224223_AddStripeCustomerId");
    await db.Database.ExecuteSqlRawAsync("INSERT INTO \"AspNetUsers\" (\"Id\", \"AccessFailedCount\", \"EmailConfirmed\", \"LockoutEnabled\", \"PhoneNumberConfirmed\", \"TwoFactorEnabled\", \"Tier\") VALUES ('legacy-user', 0, false, false, false, false, 1)");
    await db.Database.MigrateAsync();
    await db.Database.MigrateAsync();
    Check((await db.Users.SingleAsync(u => u.Id == "legacy-user")).Tier == UserTier.Paid);
    Check(!db.Database.HasPendingModelChanges());
});

await Test("Access policy covers every subscription status and configurable past_due grace", () =>
{
    foreach (var status in new[] { "active", "trialing" }) Check(BillingService.GrantsAccess(status, false));
    Check(BillingService.GrantsAccess("past_due", true));
    Check(!BillingService.GrantsAccess("past_due", false));
    foreach (var status in new[] { "unpaid", "canceled", "incomplete", "incomplete_expired", "paused", "unknown" })
        Check(!BillingService.GrantsAccess(status, true));
    return Task.CompletedTask;
});

await Test("Missing/whitespace keys and insecure production URLs fail validation", () =>
{
    var validator = new StripeSettingsValidator(config, new TestEnvironment());
    Check(validator.Validate(null, settings).Succeeded);
    Check(validator.Validate(null, new StripeSettings()).Failed);
    Check(validator.Validate(null, new StripeSettings { Enabled = false }).Succeeded);
    var whitespace = new StripeSettings { SecretKey = settings.SecretKey, PriceId = settings.PriceId, WebhookSecret = "whsec_bad " };
    Check(validator.Validate(null, whitespace).Failed);
    Check(new StripeSettingsValidator(config, new TestEnvironment { EnvironmentName = "Production" })
        .Validate(null, settings).Failed);
    return Task.CompletedTask;
});

await Test("Eight concurrent checkouts create one customer and one session across DbContexts", async () =>
{
    var id = await User();
    var fake = new FakeStripe();
    var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
    {
        await using var db = Db();
        return await Service(db, fake).CreateCheckoutAsync(id, default);
    }));
    Check(fake.CustomersCreated == 1 && fake.CheckoutsCreated == 1);
    Check(results.Select(s => s.Id).Distinct().Count() == 1);
});

await Test("Expired checkout rotates the idempotency generation", async () =>
{
    var id = await User();
    var fake = new FakeStripe();
    await using (var db = Db()) { await Service(db, fake).CreateCheckoutAsync(id, default); }
    fake.Checkout = fake.Checkout! with { Status = "expired", ExpiresAtUtc = DateTime.UtcNow.AddHours(-1) };
    await using (var db = Db()) { await Service(db, fake).CreateCheckoutAsync(id, default); }
    Check(fake.CheckoutsCreated == 2 && (await ReadUser(id)).StripeCheckoutGeneration == 1);
});

await Test("Existing subscriptions block checkout, including unpaid/incomplete recovery", async () =>
{
    foreach (var status in new[] { "active", "trialing", "past_due", "unpaid", "incomplete", "paused" })
    {
        var fake = new FakeStripe { Subscriptions = [new("sub_existing", status, false, DateTime.UtcNow.AddDays(1))] };
        var id = await User("cus_" + Guid.NewGuid().ToString("N"));
        await using var db = Db();
        await Throws<BillingConflictException>(() => Service(db, fake).CreateCheckoutAsync(id, default));
        Check(fake.CheckoutsCreated == 0);
    }
});

await Test("Provider failure does not persist entitlement or event receipt; retry succeeds", async () =>
{
    var customer = "cus_" + Guid.NewGuid().ToString("N");
    var id = await User(customer);
    var message = Event(customer);
    var fake = new FakeStripe { FailRead = true, Subscriptions = [new("sub_1", "active", false, null)] };
    await using (var db = Db())
        await Throws<HttpRequestException>(() => Service(db, fake).ProcessEventAsync(message, default));
    Check((await ReadUser(id)).Tier == UserTier.Free);
    await using (var db = Db()) Check(!await db.ProcessedStripeEvents.AnyAsync(e => e.Id == message.Id));
    fake.FailRead = false;
    await using (var db = Db()) await Service(db, fake).ProcessEventAsync(message, default);
    Check((await ReadUser(id)).Tier == UserTier.Paid);
});

await Test("Database failure rolls back receipt and entitlement; redelivery commits both", async () =>
{
    var customer = "cus_" + Guid.NewGuid().ToString("N");
    var id = await User(customer);
    var fake = new FakeStripe { Subscriptions = [new("sub_1", "active", false, null)] };
    var message = Event(customer);
    await using (var db = new FailingContext(contextOptions))
        await Throws<DbUpdateException>(() => Service(db, fake).ProcessEventAsync(message, default));
    await using (var db = Db()) Check(!await db.ProcessedStripeEvents.AnyAsync(e => e.Id == message.Id));
    Check((await ReadUser(id)).Tier == UserTier.Free);
    await using (var db = Db()) await Service(db, fake).ProcessEventAsync(message, default);
    Check((await ReadUser(id)).Tier == UserTier.Paid);
});

await Test("Checkout DB failure recovers the externally-created open session", async () =>
{
    var id = await User("cus_" + Guid.NewGuid().ToString("N"));
    var fake = new FakeStripe();
    await using (var db = new FailingContext(contextOptions))
        await Throws<DbUpdateException>(() => Service(db, fake).CreateCheckoutAsync(id, default));
    Check(fake.CheckoutsCreated == 1 && (await ReadUser(id)).StripeCheckoutSessionId is null);
    await using (var db = Db()) await Service(db, fake).CreateCheckoutAsync(id, default);
    Check(fake.CheckoutsCreated == 1 && (await ReadUser(id)).StripeCheckoutSessionId == fake.Checkout!.Id);
});

await Test("Concurrent duplicate webhook deliveries reconcile and commit once", async () =>
{
    var customer = "cus_" + Guid.NewGuid().ToString("N");
    await User(customer);
    var fake = new FakeStripe { Subscriptions = [new("sub_active", "active", false, null)] };
    var message = Event(customer);
    await Task.WhenAll(Enumerable.Range(0, 6).Select(async _ =>
    {
        await using var db = Db();
        await Service(db, fake).ProcessEventAsync(message, default);
    }));
    await using var verify = Db();
    Check(fake.Reads == 1 && await verify.ProcessedStripeEvents.CountAsync(e => e.Id == message.Id) == 1);
});

await Test("Renewal failure, recovery, scheduled cancellation, deletion and late events reconcile current state", async () =>
{
    var customer = "cus_" + Guid.NewGuid().ToString("N");
    var id = await User(customer);
    var fake = new FakeStripe();
    async Task Apply(string status, string type, bool cancel = false)
    {
        fake.Subscriptions = [new("sub_current", status, cancel, DateTime.UtcNow.AddDays(7))];
        await using var db = Db();
        await Service(db, fake).ProcessEventAsync(Event(customer, type), default);
    }
    await Apply("active", "checkout.session.completed");
    Check((await ReadUser(id)).Tier == UserTier.Paid);
    await Apply("past_due", "invoice.payment_failed");
    Check((await ReadUser(id)).Tier == UserTier.Paid);
    await using (var db = Db()) Check((await Service(db, fake, false).GetStatusAsync(id, default)).Tier == "Free");
    await Apply("unpaid", "customer.subscription.updated");
    Check((await ReadUser(id)).Tier == UserTier.Free);
    await Apply("active", "invoice.paid", true);
    var user = await ReadUser(id);
    Check(user.Tier == UserTier.Paid && user.StripeCancelAtPeriodEnd);
    // Event says an older subscription was deleted; provider still has current active subscription.
    await Apply("active", "customer.subscription.deleted");
    Check((await ReadUser(id)).Tier == UserTier.Paid);
    await Apply("canceled", "customer.subscription.deleted");
    Check((await ReadUser(id)).Tier == UserTier.Free);
});

await Test("Delayed checkout completion never grants access to an incomplete subscription", async () =>
{
    var customer = "cus_" + Guid.NewGuid().ToString("N");
    var id = await User(customer);
    var fake = new FakeStripe { Subscriptions = [new("sub_pending", "incomplete", false, null)] };
    await using (var db = Db()) await Service(db, fake).ProcessEventAsync(Event(customer, "checkout.session.completed"), default);
    Check((await ReadUser(id)).Tier == UserTier.Free);
    fake.Subscriptions = [new("sub_pending", "active", false, null)];
    await using (var db = Db()) await Service(db, fake).ProcessEventAsync(Event(customer, "checkout.session.async_payment_succeeded"), default);
    Check((await ReadUser(id)).Tier == UserTier.Paid);
});

await Test("Portal uses the authenticated user's stored customer; no-customer returns conflict", async () =>
{
    var fake = new FakeStripe();
    var id = await User("cus_portal");
    await using (var db = Db()) Check((await Service(db, fake).CreatePortalAsync(id, default)).StartsWith("https://"));
    Check(fake.PortalCustomer == "cus_portal");
    var empty = await User();
    await using (var db = Db()) await Throws<BillingConflictException>(() => Service(db, fake).CreatePortalAsync(empty, default));
});

await Test("Unmapped account events do not upgrade a user; customer mismatch is rejected", async () =>
{
    var id = await User("cus_original");
    var fake = new FakeStripe { Subscriptions = [new("sub_1", "active", false, null)] };
    await using (var db = Db()) await Service(db, fake).ProcessEventAsync(Event("cus_unrelated"), default);
    Check((await ReadUser(id)).Tier == UserTier.Free);
    await using (var db = Db()) await Throws<InvalidOperationException>(() => Service(db, fake)
        .ProcessEventAsync(Event("cus_different") with { UserId = id }, default));
});

string Payload(bool live = false, string type = "checkout.session.completed") => JsonSerializer.Serialize(new
{
    id = "evt_signed", @object = "event", api_version = StripeConfiguration.ApiVersion,
    type, livemode = live, request = (object?)null, data = new { @object = new { id = "cs_signed", @object = "checkout.session",
        customer = "cus_signed", metadata = new { userId = "test-user" } } }
});
string Signature(string body, long? timestamp = null)
{
    var time = timestamp ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    var mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(settings.WebhookSecret), Encoding.UTF8.GetBytes(time + "." + body));
    return $"t={time},v1={Convert.ToHexString(mac).ToLowerInvariant()}";
}
await Test("Signed raw payload verifies; missing/invalid/stale/tampered/wrong-mode payloads fail", async () =>
{
    var verifier = new StripeWebhookVerifier(Options.Create(settings));
    var body = Payload();
    var message = verifier.Verify(body, Signature(body));
    Check(message.CustomerId == "cus_signed" && message.UserId == "test-user");
    await Throws<InvalidWebhookException>(() => Task.FromResult(verifier.Verify(body, "")));
    await Throws<InvalidWebhookException>(() => Task.FromResult(verifier.Verify(body, "invalid")));
    await Throws<InvalidWebhookException>(() => Task.FromResult(verifier.Verify(body, Signature(body, 1))));
    await Throws<InvalidWebhookException>(() => Task.FromResult(verifier.Verify(body + " ", Signature(body))));
    var live = Payload(true);
    await Throws<InvalidWebhookException>(() => Task.FromResult(verifier.Verify(live, Signature(live))));
});

await Test("Webhook endpoint returns 400 for missing signatures and 503 on processing failure", async () =>
{
    await using var db = Db();
    var fake = new FakeStripe { FailRead = true };
    var controller = new StripeWebhookController(Service(db, fake), new(Options.Create(settings)),
        Options.Create(settings), NullLogger<StripeWebhookController>.Instance);
    var http = new DefaultHttpContext();
    controller.ControllerContext = new() { HttpContext = http };
    Check((await controller.Handle(default)) is BadRequestObjectResult);
    // Persist the signed payload's customer so reconciliation is attempted.
    await User("cus_signed");
    var body = Payload();
    http.Request.Headers["Stripe-Signature"] = Signature(body);
    http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
    Check((await controller.Handle(default)) is StatusCodeResult { StatusCode: 503 });
});

await Test("Stripe adapter filters other prices and supplies stable checkout/customer idempotency", async () =>
{
    var handler = new FakeHttpHandler();
    using var http = new HttpClient(handler);
    var client = new StripeClient("sk_test_fake", httpClient: new SystemNetHttpClient(http, maxNetworkRetries: 0));
    var gateway = new StripeGateway(client, Options.Create(settings), config);
    handler.Response = """{"id":"price_fake","object":"price","active":true,"livemode":false,"type":"recurring","recurring":{"interval":"year"}}""";
    await gateway.ValidatePriceAsync(default);
    handler.Response = """{"object":"list","has_more":false,"data":[{"id":"sub_other","object":"subscription","status":"active","items":{"object":"list","data":[{"id":"si_other","object":"subscription_item","price":{"id":"price_other","object":"price"},"current_period_end":1900000000}]}},{"id":"sub_ours","object":"subscription","status":"active","items":{"object":"list","data":[{"id":"si_ours","object":"subscription_item","price":{"id":"price_fake","object":"price"},"current_period_end":1900000000}]}}]}""";
    Check((await gateway.GetSubscriptionsAsync("cus_fake", default)).Single().Id == "sub_ours");
    handler.Response = """{"id":"cus_fake","object":"customer"}""";
    await gateway.CreateCustomerAsync("user", "fake@example.invalid", "attempt", default);
    Check(handler.IdempotencyKey == "distancetracker:customer:user:attempt");
    handler.Response = """{"id":"cs_fake","object":"checkout.session","status":"open","url":"https://checkout.stripe.test/fake","expires_at":1900000000}""";
    await gateway.CreateCheckoutAsync("user", "cus_fake", 3, default);
    Check(handler.IdempotencyKey == "distancetracker:checkout:user:3");
    Check(handler.Body.Contains("customer=cus_fake") && handler.Body.Contains("subscription_data"));
    Check(handler.Body.Contains("price_fake") && handler.Body.Contains("client_reference_id=user"));
    handler.Response = """{"id":"price_fake","object":"price","active":false,"livemode":false,"type":"recurring"}""";
    await Throws<InvalidOperationException>(() => gateway.ValidatePriceAsync(default));
});

await Test("Stale Identity writes cannot overwrite newly committed billing state", async () =>
{
    var customer = "cus_" + Guid.NewGuid().ToString("N");
    var id = await User(customer);
    await using var staleDb = Db();
    var stale = await staleDb.Users.SingleAsync(u => u.Id == id);
    var fake = new FakeStripe { Subscriptions = [new("sub_current", "active", false, null)] };
    await using (var db = Db()) await Service(db, fake).ProcessEventAsync(Event(customer), default);
    stale.EmailConfirmed = true;
    await Throws<DbUpdateConcurrencyException>(() => staleDb.SaveChangesAsync());
    Check((await ReadUser(id)).Tier == UserTier.Paid);
});

await Test("Checkout completion between provider reads blocks a second purchase and saves Paid", async () =>
{
    var id = await User("cus_" + Guid.NewGuid().ToString("N"));
    var fake = new FakeStripe { Checkout = new("cs_old", "https://example.invalid/open", "open", DateTime.UtcNow.AddHours(1)) };
    await using (var db = Db())
    {
        var user = await db.Users.SingleAsync(u => u.Id == id);
        user.StripeCheckoutSessionId = "cs_old";
        await db.SaveChangesAsync();
    }
    fake.OnGetCheckout = () =>
    {
        fake.Checkout = new("cs_old", null, "complete", DateTime.UtcNow.AddHours(1), "sub_just_created");
        fake.Subscriptions = [new("sub_just_created", "active", false, DateTime.UtcNow.AddMonths(1))];
    };
    await using (var db = Db())
        await Throws<BillingConflictException>(() => Service(db, fake).CreateCheckoutAsync(id, default));
    var saved = await ReadUser(id);
    Check(fake.Reads == 2 && fake.CheckoutsCreated == 0);
    Check(saved.Tier == UserTier.Paid && saved.StripeCheckoutGeneration == 0);
});

await Test("Completed/unresolved sessions and local-clock expiry never rotate checkout keys", async () =>
{
    foreach (var state in new[] { "complete", "open" })
    {
        var id = await User("cus_" + Guid.NewGuid().ToString("N"));
        var fake = new FakeStripe { Checkout = new("cs_unresolved", "https://example.invalid/open", state, DateTime.UtcNow.AddHours(-1)) };
        await using (var db = Db())
        {
            var user = await db.Users.SingleAsync(u => u.Id == id);
            user.StripeCheckoutSessionId = "cs_unresolved";
            await db.SaveChangesAsync();
        }
        await using (var db = Db())
            await Throws<BillingConflictException>(() => Service(db, fake).CreateCheckoutAsync(id, default));
        Check(fake.CheckoutsCreated == 0 && (await ReadUser(id)).StripeCheckoutGeneration == 0);
    }
});

await Test("Completed checkout permits a new purchase only after its own subscription is terminal", async () =>
{
    var id = await User("cus_" + Guid.NewGuid().ToString("N"));
    var fake = new FakeStripe
    {
        Checkout = new("cs_previous", null, "complete", DateTime.UtcNow.AddHours(-1), "sub_previous"),
        Subscriptions = [new("sub_previous", "canceled", false, DateTime.UtcNow.AddDays(-1))]
    };
    await using (var db = Db())
    {
        var user = await db.Users.SingleAsync(u => u.Id == id);
        user.StripeCheckoutSessionId = "cs_previous";
        await db.SaveChangesAsync();
    }
    await using (var db = Db()) await Service(db, fake).CreateCheckoutAsync(id, default);
    Check(fake.CheckoutsCreated == 1 && (await ReadUser(id)).StripeCheckoutGeneration == 1);
});

await Test("Legacy Paid without a customer cannot bypass daily limits and reconciles to Free", async () =>
{
    await using (var db = Db())
    {
        var user = await db.Users.SingleAsync(u => u.Id == "legacy-user");
        user.StripeSubscriptionId = "sub_stale";
        user.StripeSubscriptionStatus = "active";
        user.StripeCancelAtPeriodEnd = true;
        user.StripeCurrentPeriodEndUtc = DateTime.UtcNow.AddDays(30);
        db.Trips.Add(new() { Id = Guid.NewGuid(), UserId = user.Id, DateUTC = DateTime.UtcNow, LastCalculatedAtUTC = DateTime.UtcNow });
        await db.SaveChangesAsync();
        await Throws<Exception>(() => new DistanceTracker.API.Services.EnsureCalcTwoTier(db).EnsureCanCalculateAsync(user));
    }
    await using (var db = Db())
    {
        var status = await Service(db, new()).GetStatusAsync("legacy-user", default);
        Check(status.Tier == "Free" && status.Status is null && status.CanCreateCheckout);
    }
    var saved = await ReadUser("legacy-user");
    Check(saved.Tier == UserTier.Free && saved.StripeSubscriptionId is null &&
        saved.StripeSubscriptionStatus is null && !saved.StripeCancelAtPeriodEnd && saved.StripeCurrentPeriodEndUtc is null);
});

await Test("Cached customer 500 is not replayed; only confirmed operator recovery rotates the durable key", async () =>
{
    var id = await User();
    var fake = new FakeStripe();
    fake.CreateCustomerOverride = async (userId, email, attemptId, ct) =>
    {
        Check((await ReadUser(userId)).StripeCustomerCreationAttemptId == attemptId, "Attempt must be durable before the API call");
        if (fake.CustomersCreated == 1) throw new CustomerCreationUncertainException();
        fake.Customers[userId] = "cus_recovered_" + userId;
        return fake.Customers[userId];
    };
    await using (var db = Db())
        await Throws<CustomerCreationUncertainException>(() => Service(db, fake).CreateCheckoutAsync(id, default));
    var first = await ReadUser(id);
    Check(first.StripeCustomerCreationNeedsReview && first.StripeCustomerCreationAttemptId is not null);
    await using (var db = Db())
        await Throws<BillingConflictException>(() => Service(db, fake).CreateCheckoutAsync(id, default));
    await using (var db = Db())
        await Throws<BillingConflictException>(() => Service(db, fake).RecoverCustomerAsync(id, false, default));
    Check(fake.CustomersCreated == 1 && fake.CheckoutsCreated == 0);
    await using (var db = Db()) await Service(db, fake).RecoverCustomerAsync(id, true, default);
    var reset = await ReadUser(id);
    Check(!reset.StripeCustomerCreationNeedsReview && reset.StripeCustomerCreationAttemptId != first.StripeCustomerCreationAttemptId);
    await using (var db = Db()) await Service(db, fake).CreateCheckoutAsync(id, default);
    var saved = await ReadUser(id);
    Check(fake.CustomersCreated == 2 && fake.CustomerAttemptKeys.Distinct().Count() == 2 &&
        fake.CheckoutsCreated == 1 && saved.StripeCustomerCreationAttemptId is null &&
        saved.StripeCustomerCreationEmail is null && saved.StripeCustomerCreationStartedAtUtc is null);
});

await Test("A customer created despite a 500 is recovered without a second creation request", async () =>
{
    var id = await User();
    var fake = new FakeStripe();
    fake.CreateCustomerOverride = (userId, email, attemptId, ct) =>
    {
        fake.Customers[userId] = "cus_late_" + userId;
        throw new CustomerCreationUncertainException();
    };
    await using (var db = Db())
        await Throws<CustomerCreationUncertainException>(() => Service(db, fake).CreateCheckoutAsync(id, default));
    await using (var db = Db()) await Service(db, fake).CreateCheckoutAsync(id, default);
    var saved = await ReadUser(id);
    Check(fake.CustomersCreated == 1 && saved.StripeCustomerId == fake.Customers[id] && !saved.StripeCustomerCreationNeedsReview);
    await using (var db = Db()) await Service(db, fake).RecoverCustomerAsync(id, true, default);
    Check(fake.CustomersCreated == 1 && (await ReadUser(id)).StripeCustomerId == saved.StripeCustomerId);
});

await Test("Customer timeout retries keep both key and email stable across Identity email changes", async () =>
{
    var id = await User();
    var fake = new FakeStripe();
    fake.CreateCustomerOverride = (userId, email, attemptId, ct) =>
    {
        if (fake.CustomersCreated == 1) throw new HttpRequestException("Simulated timeout");
        return Task.FromResult("cus_timeout_" + userId);
    };
    await using (var db = Db())
        await Throws<HttpRequestException>(() => Service(db, fake).CreateCheckoutAsync(id, default));
    var pending = await ReadUser(id);
    Check(!pending.StripeCustomerCreationNeedsReview);
    await using (var db = Db())
    {
        var user = await db.Users.SingleAsync(u => u.Id == id);
        user.Email = "changed@example.invalid";
        await db.SaveChangesAsync();
    }
    await using (var db = Db()) await Service(db, fake).CreateCheckoutAsync(id, default);
    Check(fake.CustomerAttemptKeys.Count == 2 && fake.CustomerAttemptKeys.Distinct().Count() == 1);
    Check(fake.CustomerAttemptEmails.All(email => email == pending.StripeCustomerCreationEmail));
});

await Test("Unknown customer attempts older than the idempotency window require recovery", async () =>
{
    var id = await User();
    var fake = new FakeStripe
    {
        CreateCustomerOverride = (_, _, _, _) => throw new HttpRequestException("Simulated timeout")
    };
    await using (var db = Db())
        await Throws<HttpRequestException>(() => Service(db, fake).CreateCheckoutAsync(id, default));
    await using (var db = Db())
    {
        var user = await db.Users.SingleAsync(u => u.Id == id);
        user.StripeCustomerCreationStartedAtUtc = DateTime.UtcNow.AddDays(-2);
        await db.SaveChangesAsync();
    }
    await using (var db = Db())
        await Throws<BillingConflictException>(() => Service(db, fake).CreateCheckoutAsync(id, default));
    Check(fake.CustomersCreated == 1 && fake.CheckoutsCreated == 0);
});

await Test("Stripe customer lookup uses user metadata, rejects duplicates, and classifies cached 500", async () =>
{
    var handler = new FakeHttpHandler();
    using var http = new HttpClient(handler);
    var client = new StripeClient("sk_test_fake", httpClient: new SystemNetHttpClient(http, maxNetworkRetries: 0));
    var gateway = new StripeGateway(client, Options.Create(settings), config);
    handler.Response = """{"object":"list","has_more":false,"data":[{"id":"cus_other","object":"customer","email":"same@example.invalid","metadata":{"userId":"other"}},{"id":"cus_ours","object":"customer","email":"same@example.invalid","metadata":{"userId":"user"}}]}""";
    Check(await gateway.FindCustomerAsync("user", default) == "cus_ours");
    handler.Response = """{"object":"list","has_more":false,"data":[{"id":"cus_a","object":"customer","metadata":{"userId":"user"}},{"id":"cus_b","object":"customer","metadata":{"userId":"user"}}]}""";
    await Throws<BillingConflictException>(() => gateway.FindCustomerAsync("user", default));
    handler.Status = HttpStatusCode.InternalServerError;
    handler.Response = """{"error":{"type":"api_error","message":"Simulated cached error"}}""";
    await Throws<CustomerCreationUncertainException>(() => gateway.CreateCustomerAsync("user", "email@example.invalid", "attempt", default));
    Check(handler.IdempotencyKey == "distancetracker:customer:user:attempt");
});

await Test("Customer mapping save failure recovers the provider customer without creating another", async () =>
{
    var id = await User();
    var fake = new FakeStripe();
    await using (var db = new FailSecondSaveContext(contextOptions))
        await Throws<DbUpdateException>(() => Service(db, fake).CreateCheckoutAsync(id, default));
    var pending = await ReadUser(id);
    Check(pending.StripeCustomerId is null && pending.StripeCustomerCreationAttemptId is not null);
    await using (var db = Db()) await Service(db, fake).CreateCheckoutAsync(id, default);
    Check(fake.CustomersCreated == 1 && (await ReadUser(id)).StripeCustomerId == fake.Customers[id]);
});
Console.WriteLine("Stripe snapshot API version: " + StripeConfiguration.ApiVersion);
Console.WriteLine($"All {passed} billing regression groups passed. No Stripe network calls were made.");

sealed class FakeStripe : IStripeGateway
{
    public int CustomersCreated, CheckoutsCreated, Reads;
    public bool FailRead;
    public string? PortalCustomer;
    public Action? OnGetCheckout;
    public Func<string, string?, string, CancellationToken, Task<string>>? CreateCustomerOverride;
    public readonly List<string> CustomerAttemptKeys = [];
    public readonly List<string?> CustomerAttemptEmails = [];
    public readonly ConcurrentDictionary<string, string> Customers = new();
    public readonly ConcurrentDictionary<string, BillingCheckout> Checkouts = new();
    private BillingCheckout? latest;
    public BillingCheckout? Checkout
    {
        get => latest;
        set { latest = value; if (value is not null) Checkouts[value.Id] = value; }
    }
    public IReadOnlyList<BillingSubscription> Subscriptions = [];
    public Task ValidatePriceAsync(CancellationToken ct) => Task.CompletedTask;
    public Task<string?> FindCustomerAsync(string userId, CancellationToken ct) =>
        Task.FromResult(Customers.TryGetValue(userId, out var id) ? id : null);
    public Task<string> CreateCustomerAsync(string userId, string? email, string attemptId, CancellationToken ct)
    {
        Interlocked.Increment(ref CustomersCreated);
        CustomerAttemptKeys.Add(attemptId);
        CustomerAttemptEmails.Add(email);
        if (CreateCustomerOverride is not null) return CreateCustomerOverride(userId, email, attemptId, ct);
        var customer = "cus_" + userId;
        Customers[userId] = customer;
        return Task.FromResult(customer);
    }
    public Task<IReadOnlyList<BillingSubscription>> GetSubscriptionsAsync(string customerId, CancellationToken ct)
    {
        Interlocked.Increment(ref Reads);
        if (FailRead) throw new HttpRequestException("Simulated provider outage");
        return Task.FromResult(Subscriptions);
    }
    public Task<BillingCheckout?> GetCheckoutAsync(string id, CancellationToken ct)
    {
        OnGetCheckout?.Invoke();
        return Task.FromResult(Checkouts.GetValueOrDefault(id));
    }
    public Task<BillingCheckout?> FindOpenCheckoutAsync(string userId, string customerId, CancellationToken ct) =>
        Task.FromResult(Checkouts.Values.FirstOrDefault(s => s.Status == "open"));
    public Task<BillingCheckout> CreateCheckoutAsync(string userId, string customerId, int generation, CancellationToken ct)
    {
        Interlocked.Increment(ref CheckoutsCreated);
        Checkout = new("cs_" + generation, "https://checkout.stripe.test/fake", "open", DateTime.UtcNow.AddHours(1));
        return Task.FromResult(Checkout);
    }
    public Task<string> CreatePortalAsync(string customerId, CancellationToken ct)
    { PortalCustomer = customerId; return Task.FromResult("https://billing.stripe.test/fake"); }
}
sealed class FailingContext(DbContextOptions<DistanceTrackerContext> options) : DistanceTrackerContext(options)
{
    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
        throw new DbUpdateException("Simulated database write failure");
}
sealed class FailSecondSaveContext(DbContextOptions<DistanceTrackerContext> options) : DistanceTrackerContext(options)
{
    private int saves;
    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        if (++saves == 2) throw new DbUpdateException("Simulated customer mapping save failure");
        return base.SaveChangesAsync(cancellationToken);
    }
}
sealed class TestEnvironment : IHostEnvironment
{
    public string EnvironmentName { get; set; } = "Development";
    public string ApplicationName { get; set; } = "BillingTests";
    public string ContentRootPath { get; set; } = ".";
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}
sealed class FakeHttpHandler : HttpMessageHandler
{
    public HttpStatusCode Status = HttpStatusCode.OK;
    public string Response = "{}";
    public string Body = "", IdempotencyKey = "";
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
        IdempotencyKey = request.Headers.TryGetValues("Idempotency-Key", out var values) ? values.Single() : "";
        return new(Status) { Content = new StringContent(Response, Encoding.UTF8, "application/json") };
    }
}
