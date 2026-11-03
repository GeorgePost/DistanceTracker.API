# Stripe subscriptions

## API

All payments routes require JWT authentication and take no body. Prices, customer IDs and return URLs are server-controlled.

| Method | Route | Result |
|---|---|---|
| POST | /api/payments/create-checkout | Creates/reuses hosted Checkout URL; 409 for an existing subscription |
| GET | /api/payments/subscription | Reconciles tier, status, cancelAtPeriodEnd, currentPeriodEndUtc and canCreateCheckout |
| POST | /api/payments/create-portal | Returns the user's hosted Portal URL; 409 without a customer |
| POST | /api/webhooks/stripe | Signature-authenticated webhook; no JWT required |

Payments return 401 for missing authentication/user, 409 for a business conflict and 503 for provider/database
failure or disabled billing. Webhooks return 400 for invalid/missing signatures or malformed supported events,
200 only after successful processing (or for irrelevant events), and 503 for retryable processing failures.
Billing logs omit secrets, raw bodies, customer email, checkout URLs and portal URLs.

## Access policy

| Current subscription status | Access | New checkout |
|---|---|---|
| active / trialing | Paid | Blocked |
| past_due | Paid by default, configurable | Blocked; use Portal |
| incomplete / unpaid / paused | Free | Blocked; manage existing subscription |
| canceled / incomplete_expired / none | Free | Allowed |

Scheduled cancellation retains access while the subscription is active.
Checkout completion alone never grants Paid: handlers query current Stripe subscriptions and require an eligible
subscription containing the configured PriceId. All relevant subscriptions are considered, so an old subscription's
deletion cannot revoke a newer eligible subscription.
The existing trip calculation policy continues using the stored user Tier.
Webhooks and GET subscription update that tier; provider outages do not automatically revoke access.

AllowPastDueAccess=true follows Stripe's retry/dunning period, not an application-specific fixed duration.
Configure Stripe to move failed subscriptions to unpaid/canceled if indefinite past_due access is not wanted.
Changing this setting takes effect per user at their next webhook/status reconciliation.

## Configuration

Compose .env variables:

```text
STRIPE_ENABLED=true
STRIPE_SECRET_KEY=<test secret API key>
STRIPE_WEBHOOK_SECRET=<listener or endpoint signing secret>
STRIPE_PRICE_ID=<active recurring test price>
STRIPE_ALLOW_PAST_DUE_ACCESS=true
FRONTEND_BASE_URL=http://localhost:3000
```

Compose maps these to Stripe:Enabled, SecretKey, WebhookSecret, PriceId, AllowPastDueAccess, and Frontend:BaseUrl.
Outside Docker use nested JSON or double-underscore environment names such as Stripe__SecretKey.
The existing configured price was verified as yearly; billing frequency is controlled in Stripe.

Enabled startup validates key formats, whitespace, frontend URL and then performs a read-only price retrieval.
The key must work, the price must be active/recurring, and test/live modes must match.
Frontend:BaseUrl must be absolute HTTP(S), HTTPS outside Development, without query, fragment or credentials.
Stripe:Enabled=false skips provider validation; billing routes return 503. Compose still requires its interpolated
variables; .env.example supplies non-secret placeholders for isolated tests.

## Database migration

AddStripeSubscriptionLifecycle adds status/cancellation/end/sync fields, checkout session/generation fields,
a unique nullable StripeCustomerId index and ProcessedStripeEvents.
No existing users, trips or subscription IDs are deleted. Existing Tier values survive migration and become
provider-derived on reconciliation. Resolve any duplicate non-null StripeCustomerId values before applying
the unique index; do not reset data as a workaround.

Normal API startup never migrates automatically. EF tooling uses a design-time factory without starting Redis
or Stripe checks. It reads base JSON, environment JSON (Development by default for tooling), then environment variables.
It does not read Compose .env automatically.

After reviewing/backing up the intended persistent development database:

```powershell
docker compose build api
docker compose up -d --wait postgres redis
docker compose run --rm --no-deps api --migrate
docker compose up -d api
docker compose logs --tail 100 api
```

Use your separate reviewed deployment/backup process for production.

## Stripe-side setup

Install the [Stripe CLI](https://docs.stripe.com/stripe-cli), authenticate and keep this running on the Windows host:

```powershell
stripe login
stripe listen --forward-to http://localhost:8080/api/webhooks/stripe
```

Use test/sandbox mode, not --live. Copy the emitted whsec_ secret into STRIPE_WEBHOOK_SECRET, then:

```powershell
docker compose up -d --force-recreate api
```

An API key does not establish webhook delivery. Listener and Dashboard endpoint signing secrets differ.
A CLI container on the Compose network forwards to http://api:8080/api/webhooks/stripe instead.

Use snapshot events compatible with the installed Stripe SDK. Startup logs the SDK API version (currently 2025-12-15.clover for Stripe.net 50.1.0).
The CLI defaults to the account API version; do not use --latest blindly or turn off SDK compatibility checks.
If versions differ, configure a matching snapshot endpoint and use its forwarding configuration,
or upgrade the SDK deliberately.

For production register a public HTTPS snapshot endpoint at /api/webhooks/stripe with its own signing secret.
Enable these events:

```text
checkout.session.completed
checkout.session.async_payment_succeeded
checkout.session.async_payment_failed
checkout.session.expired
customer.subscription.created
customer.subscription.updated
customer.subscription.deleted
customer.subscription.paused
customer.subscription.resumed
invoice.paid
invoice.payment_failed
invoice.payment_action_required
```

Enable Stripe's Customer Portal in the corresponding test/live account configuration: payment updates, invoice
history and the desired cancellation behavior. Restrict price changes to supported prices: access currently
requires the single configured PriceId. The API creates portal sessions; it does not alter your account settings.

Frontend pages are still required:
- /success can read session_id and call GET /api/payments/subscription; the redirect never grants access.
- /cancel returns users to the app; an open unexpired checkout stays reusable.
- /account is the Portal return destination.

## Reliability

PostgreSQL transaction advisory locks serialize each user's billing operations across API instances.
Customer mapping commits before checkout creation, so arriving webhooks can locate the user.
Customer/session creation uses stable Stripe idempotency keys; checkout generations rotate after expiry/completion.
Open checkouts are reused and recovered after a local save failure, so correctness does not depend solely on
Stripe retaining an idempotency key forever.

Webhook event IDs are durable. Receipts and entitlement updates commit atomically; duplicates are skipped only
after a successful commit. Current provider state is retrieved after acquiring the user lock, protecting against
out-of-order snapshots. Unrelated events/customers are acknowledged without granting access.
Failed EF writes throw and return 503 rather than silently acknowledging a failed Identity update.
Billing writes rotate Identity concurrency stamps so stale simultaneous Identity updates cannot overwrite billing state.

For a larger deployment add durable queuing, periodic reconciliation, stale-sync monitoring and delivery alerts.
Keep event receipts or design an explicit retention policy. The API cannot prevent subscriptions created manually
in Stripe; if duplicates already exist, access remains while any configured-price eligible subscription exists.
Changing price/return URL configuration during an outstanding idempotent operation may require operator recovery.

## Verification

```powershell
dotnet build DistanceTracker.API.slnx
powershell -NoProfile -File scripts/Test-Billing.ps1
docker build -t distancetracker-api:docker-audit .
powershell -NoProfile -File scripts/Test-Docker.ps1
```

Test-Billing provisions a separate PostgreSQL container, runs a regression executable (not xUnit/dotnet test),
and refuses database names outside billing_tests_*. Stripe is mocked, including SDK HTTP requests.
Test containers are stopped and volumes retained. Test-Docker disables billing and uses fake email on an
isolated internal Compose network.

Coverage: schema upgrades/idempotence, grace/access states, invalid config, concurrent checkout and duplicate
webhooks, expiry, duplicate subscription blocking, provider/DB rollback and recovery, delayed events/payments,
scheduled cancellation, portal ownership, customer isolation, signature/replay checks and SDK idempotency options.

Real sandbox Checkout -> webhook -> Paid -> Portal cancellation -> Free still requires your Stripe listener/account
configuration and explicit test-mode interactions. Mocked tests do not prove external webhook delivery or portal setup.

## References

- [Subscription lifecycle](https://docs.stripe.com/billing/subscriptions/webhooks)
- [Webhook signatures, retries and ordering](https://docs.stripe.com/webhooks)
- [Checkout sessions](https://docs.stripe.com/api/checkout/sessions/create)
- [Customer Portal sessions](https://docs.stripe.com/api/customer_portal/sessions/create)
- [CLI listener](https://docs.stripe.com/cli/listen)

## Implementation validation record

Implemented and deployed to the existing Docker Development stack after an additive migration and a validated
pg_dump backup. Backup: C:/Users/Georg/AppData/Local/Temp/distancetracker-before-review-fixes-20261103035603.dump.
No production database or Stripe subscription was modified. Four EF migrations are now applied.

Passed: solution build, Docker builds, Compose configuration, 28 billing regression groups, disposable Docker
registration/confirmation/JWT/trips smoke test, real read-only Stripe price startup validation, updated Swagger,
401 protection on all payment routes, and 400 for an unsigned webhook. The API is running at localhost:8080.
The test runner made no Stripe network calls. Deployment startup performed only a read-only price lookup.
No checkout session, payment, email or subscription mutation was triggered during this implementation.

Existing compiler nullability warnings, optional Alpine GSSAPI warning and the transitive
System.Security.Cryptography.Pkcs vulnerability warnings remain separate issues.
External webhook delivery, portal configuration and a real sandbox purchase/cancellation flow still require
Stripe-side setup and have not been claimed as verified.

## Ponytail review fixes

Checkout creation now re-reads subscriptions after observing a completed Checkout Session.
It creates a replacement only when that session's own subscription is known to be terminal and no
other relevant subscription blocks checkout. A missing/unresolved subscription, or an open session
whose expiry is inferred only from the local clock, does not advance the checkout generation.

A Paid row without a Stripe customer is treated as Free during reconciliation, with stale subscription
fields cleared. The calculation policy also refuses the Paid bypass without a customer, even before
the user visits the subscription endpoint.

Migration AddStripeCustomerCreationRecovery adds a persisted customer-creation attempt ID, email snapshot,
start time and review flag. The key and parameters commit before any creation request. Timeouts keep the
same attempt; a cached Stripe 500 pauses creation. A retry first looks for a customer with the exact userId
metadata and recovers it without creating another. Email alone is never used to establish ownership.
Multiple matching customers require operator review. Direct customer-list pagination is used rather than
eventually-consistent Search; this runs only while a customer is unlinked, but can be expensive on large accounts.

A cached 500 is an unknown outcome, not proof that nothing happened. The app does not automatically rotate
that key. Unknown attempts older than 24 hours also pause rather than replaying a key Stripe may have pruned.
The API returns 409 when support review is required. Pending email/key fields are cleared after linkage.

### Operator recovery

First try read-only Stripe discovery plus local linkage:

```powershell
docker compose run --rm --no-deps api --recover-stripe-customer USER_ID
```

If a matching customer exists, the command links it and reconciles the subscription. It creates no customer,
checkout, payment or subscription. It refuses to replace an existing link and refuses ambiguous matches.

If no customer is found, inspect the original customer-creation request in Stripe's Dashboard. A list with no
match is not proof that an earlier request cannot finish later. Only after confirming that no customer was
created and no request remains in flight, authorize a new persisted attempt:

```powershell
docker compose run --rm --no-deps api --recover-stripe-customer USER_ID --confirmed-no-customer
```

This explicit operator assertion rotates the local attempt key; the next normal checkout request creates the
customer. The command itself makes no creation calls. It is a local maintenance command with server credentials,
not a public HTTP endpoint. Never use the confirmation flag merely to bypass an unresolved provider failure.

Stripe's [error-handling guidance](https://docs.stripe.com/error-low-level) explains why unknown outcomes must
be resolved before using a new key. Test coverage includes the original checkout race, stale Paid access,
cached-error recovery, failed local linkage, email changes, expired retry windows and duplicate customer matches.
