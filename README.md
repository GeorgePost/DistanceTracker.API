# DistanceTracker API

A REST API for logging and calculating multi-stop trip distances. Users create trips with an ordered list of address stops; the API geocodes each address via Nominatim (OpenStreetMap), computes driving distances between stops via OpenRouteService, and persists the results. A two-tier subscription model (Free/Paid) controls how often distance calculations can be triggered, with Stripe handling subscription payments.

---

## Tech Stack

| Layer | Technology |
|---|---|
| Framework | ASP.NET Core (.NET 10) |
| Database | PostgreSQL via Npgsql + EF Core 10 |
| Cache | Redis (optional, geocode caching) |
| Auth | ASP.NET Identity + JWT Bearer |
| Payments | Stripe Subscriptions |
| Email | SendGrid |
| Geocoding | Nominatim (OpenStreetMap) |
| Routing | OpenRouteService |
| Docs | Swagger / Swashbuckle |

### Architecture

The project follows a layered architecture:

- **Controllers** receive HTTP requests, validate input via DTOs, and delegate to services.
- **Services** contain all business logic and external API integrations behind interfaces, keeping controllers thin and the implementations swappable.
- **DTOs** define the exact shape of request and response payloads, decoupling the API contract from internal models.
- **Models** are the EF Core entity classes mapped directly to the database.
- **Data** holds the `DbContext` and relationship configuration.
- **Migrations** track every schema change as versioned EF Core migration files.

---

## Folder Structure

```
DistanceTracker.API/
├── Controllers/
│   ├── AuthController.cs          # Registration, login, email confirmation, password reset
│   ├── TripsController.cs         # Full CRUD for trips + distance calculation trigger
│   ├── PaymentsController.cs      # Creates Stripe Checkout sessions
│   └── StripeWebhookController.cs # Handles Stripe subscription lifecycle events
│
├── Models/
│   ├── ApplicationUser.cs         # Identity user extended with tier and Stripe fields
│   ├── Trip.cs                    # Trip entity (date, total distance, notes)
│   └── TripStop.cs                # Individual stop (address, lat/lon, order, distance to next)
│
├── DTOs/
│   ├── CreateTripDTO.cs           # Inbound: date + list of address strings
│   ├── TripResponseDTO.cs         # Outbound: trip with resolved stops
│   ├── TripStopDTO.cs             # Outbound: stop detail
│   ├── LoginUserDTO.cs            # Inbound: email + password
│   ├── RegisterUserDTO.cs         # Inbound: email + password
│   ├── AuthResponseDTO.cs         # Outbound: user info + JWT
│   ├── ForgotPasswordDTO.cs       # Inbound: email
│   ├── ResetPasswordDTO.cs        # Inbound: email + token + new password
│   ├── ConfirmEmailDTO.cs         # Inbound: userId + token
│   ├── GeoCacheDTO.cs             # Internal: Redis-cached lat/lon
│   ├── NominatimResult.cs         # Deserialization: Nominatim API response
│   └── OpenRouteServiceDTO.cs     # Deserialization: OpenRouteService API response
│
├── Services/
│   ├── JwtAuth.cs                         # JWT token generation
│   ├── NominatimGeocodingService.cs       # Address -> lat/lon, with Redis caching
│   ├── OpenRouteDistanceService.cs        # Coordinate list -> driving distances (km)
│   ├── EnsureCalcTwoTier.cs               # Enforces free-tier daily calculation limit
│   ├── IGeocodingService.cs               # Interface for geocoding
│   ├── IDistanceService.cs                # Interface for distance calculation
│   ├── ITripCalculationPolicy.cs          # Interface for calculation access policy
│   └── Email/
│       ├── SendGridEmailService.cs        # Sends confirmation and password reset emails
│       ├── FakeEmailService.cs            # No-op implementation for local development
│       ├── IEmailService.cs               # Interface for email sending
│       └── SendGridOptions.cs             # Strongly-typed SendGrid config binding
│
├── Data/
│   └── DistanceTrackerContext.cs  # EF Core DbContext; configures relationships and indexes
│
├── Migrations/                    # EF Core migration history (PostgreSQL target)
│
└── Program.cs                     # Service registration, middleware pipeline, rate limiters
```

---

## Running Locally

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- PostgreSQL instance (local or Docker)
- Redis instance (optional — geocode caching degrades gracefully without it)
- OpenRouteService API key (free tier available at openrouteservice.org)
- SendGrid API key (free tier available at sendgrid.com)
- Stripe account (test mode keys sufficient for local development)

### Setup

1. **Clone the repository**

   ```bash
   git clone <repo-url>
   cd DistanceTracker.API
   ```

2. **Configure application secrets**

   Create or populate `appsettings.Development.json` (never commit secrets):

   ```json
   {
     "ConnectionStrings": {
       "Postgres": "Host=localhost;Port=5432;Database=distancetracker;Username=postgres;Password=yourpassword"
     },
     "Jwt": {
       "SigningKey": "your-secret-key-at-least-32-characters-long",
       "Issuer": "DistanceTracker.API",
       "Audience": "DistanceTracker.Client"
     },
     "Stripe": {
       "SecretKey": "sk_test_...",
       "PriceId": "price_...",
       "WebhookSecret": "whsec_..."
     },
     "SendGrid": {
       "ApiKey": "SG...",
       "SenderEmail": "noreply@yourdomain.com",
       "FromName": "DistanceTracker"
     },
     "Redis": {
       "ConnectionString": "localhost:6379"
     },
     "ExternalApis": {
       "Nominatim": {
         "BaseUrl": "https://nominatim.openstreetmap.org",
         "UserAgent": "DistanceTrackerApp/1.0 contact@yourdomain.com"
       },
       "OpenRouteService": {
         "BaseUrl": "https://api.openrouteservice.org",
         "ApiKey": "your-ors-api-key"
       }
     },
     "Frontend": {
       "BaseUrl": "http://localhost:3000"
     }
   }
   ```

3. **Apply database migrations**

   ```bash
   dotnet ef database update
   ```

4. **Run the API**

   ```bash
   dotnet run
   ```

   Swagger UI is available at `https://localhost:{port}/swagger` in Development mode.

---

## API Endpoints

### Auth — `/api/auth`

| Method | Route | Auth | Description |
|--------|-------|------|-------------|
| `POST` | `/api/auth/register` | None | Register a new user. Sends an email confirmation link. |
| `POST` | `/api/auth/login` | None | Log in with email and password. Returns a JWT valid for 1 hour. |
| `POST` | `/api/auth/confirm-email` | None | Confirm email with the token from the confirmation email. |
| `POST` | `/api/auth/forgot-password` | None | Send a password reset link to the provided email. |
| `POST` | `/api/auth/reset-password` | None | Reset the password using the token from the reset email. |

### Trips — `/api/trips`

All trip endpoints require a valid JWT in the `Authorization: Bearer <token>` header.

| Method | Route | Description |
|--------|-------|-------------|
| `GET` | `/api/trips` | List all trips for the authenticated user, ordered by date descending. Optional `StartDate` and `EndDate` UTC query parameters filter by date range. |
| `GET` | `/api/trips/{id}` | Get a single trip with its stops. |
| `POST` | `/api/trips` | Create a new trip. Accepts a date and an ordered list of address strings; each address is geocoded on creation. |
| `PUT` | `/api/trips/{id}` | Update an existing trip's date and stops. Stops are re-geocoded and distances are cleared until recalculated. |
| `DELETE` | `/api/trips/{id}` | Delete a trip and all its stops (cascade). |
| `POST` | `/api/trips/{id}/calculate` | Trigger driving distance calculation for a trip via OpenRouteService. Free-tier users are limited to one calculation per day; Paid users are unrestricted. |

### Payments — `/api/payments`

Requires a valid JWT.

| Method | Route | Description |
|--------|-------|-------------|
| `POST` | `/api/payments/create-checkout` | Creates a Stripe Checkout session for the Pro subscription. Returns the hosted checkout URL. |

### Webhooks

| Method | Route | Description |
|--------|-------|-------------|
| `POST` | `/api/webhooks/stripe` | Receives Stripe events. Handles `checkout.session.completed` (upgrades user to Paid tier) and `customer.subscription.deleted` (downgrades back to Free). Signature verified against `Stripe:WebhookSecret`. |

---

## Environment Variables Reference

| Key | Required | Description |
|-----|----------|-------------|
| `ConnectionStrings:Postgres` | Yes | PostgreSQL connection string |
| `Jwt:SigningKey` | Yes | HMAC-SHA256 signing key (min 32 chars) |
| `Jwt:Issuer` | Yes | JWT issuer claim value |
| `Jwt:Audience` | Yes | JWT audience claim value |
| `Stripe:SecretKey` | Yes (prod) | Stripe secret API key |
| `Stripe:PriceId` | Yes | Stripe Price ID for the subscription product |
| `Stripe:WebhookSecret` | Yes | Stripe webhook signing secret |
| `SendGrid:ApiKey` | Yes | SendGrid API key |
| `SendGrid:SenderEmail` | Yes | From address for outbound emails |
| `SendGrid:FromName` | Yes | Display name for outbound emails |
| `ExternalApis:Nominatim:BaseUrl` | Yes | Nominatim base URL |
| `ExternalApis:Nominatim:UserAgent` | Yes | User-Agent header sent to Nominatim (required by their ToS) |
| `ExternalApis:OpenRouteService:BaseUrl` | Yes | OpenRouteService base URL |
| `ExternalApis:OpenRouteService:ApiKey` | Yes | OpenRouteService API key |
| `Frontend:BaseUrl` | Yes | Base URL of the frontend app (used in email confirmation and password reset links) |
| `Redis:ConnectionString` | No | Redis connection string. If absent, geocode results are not cached. |

---

## Rate Limiting

All sensitive endpoints are protected by fixed-window rate limiters partitioned per user (authenticated) or IP (anonymous):

| Policy | Endpoint(s) | Limit |
|--------|-------------|-------|
| `LoginPolicy` | `POST /api/auth/login` | 5 requests / 5 min |
| `RegisterPolicy` | `POST /api/auth/register` | 5 requests / 10 min |
| `EmailPolicy` | `forgot-password`, `reset-password`, `confirm-email` | 2 requests / 1 min |
| `TripsWritePolicy` | `POST`, `PUT`, `DELETE`, `/calculate` on trips | 5 requests / 1 min |
