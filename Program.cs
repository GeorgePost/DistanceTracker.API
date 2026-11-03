using DistanceTracker.API.Data;
using DistanceTracker.API.Services.Billing;
using DistanceTracker.API.Models;
using DistanceTracker.API.Services;
using DistanceTracker.API.Services.Email;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using Resend;
using SendGrid;
using StackExchange.Redis;
using System.Security.Claims;
using System.Threading.RateLimiting;
var builder = WebApplication.CreateBuilder(args);
var postgresConnection = builder.Configuration.GetConnectionString("Postgres");
if (string.IsNullOrWhiteSpace(postgresConnection))
    throw new InvalidOperationException("ConnectionStrings:Postgres must be configured (environment: ConnectionStrings__Postgres).");

// Explicit maintenance command; never migrate automatically at API startup.
if (args.Contains("--migrate"))
{
    var options = new DbContextOptionsBuilder<DistanceTrackerContext>()
        .UseNpgsql(postgresConnection).Options;
    await using var context = new DistanceTrackerContext(options);
    await context.Database.MigrateAsync();
    return;
}

var dataProtection = builder.Services.AddDataProtection();
var keyPath = builder.Configuration["DataProtection:KeyPath"];
if (!string.IsNullOrWhiteSpace(keyPath))
    dataProtection.PersistKeysToFileSystem(new DirectoryInfo(keyPath));
// Access AppSettings
var jwtSettings = builder.Configuration.GetSection("Jwt");
string secretKey = jwtSettings["SigningKey"] ?? throw new ArgumentNullException("JWT SecretKey not configured");
string issuer = jwtSettings["Issuer"] ?? throw new ArgumentNullException("JWT Issuer not configured");
string audience = jwtSettings["Audience"] ?? throw new ArgumentNullException("JWT Audience not configured");
// Add services to the container.
//Services
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Description = "JWT Authorization header. Enter: Bearer {token}",
        Name = "Authorization",
        In = ParameterLocation.Header,
        Type = SecuritySchemeType.ApiKey,
        Scheme = "Bearer"
    });

    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference
                {
                    Type = ReferenceType.SecurityScheme,
                    Id = "Bearer"
                }
            },
            new string[] {}
        }
    });
});
// Add HttpClient
builder.Services.AddHttpClient();

// Configure Email Service
builder.Services.Configure<ResendOptions>(builder.Configuration.GetSection("Resend"));
builder.Services.Configure<ResendClientOptions>(options =>
{
    options.ApiToken = builder.Configuration["Resend:ApiKey"]!;
});
builder.Services.AddHttpClient<ResendClient>();
builder.Services.AddTransient<IResend, ResendClient>();
// Billing uses an injected client so tests never need real Stripe requests.
builder.Services.AddOptions<StripeSettings>().BindConfiguration("Stripe").ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<StripeSettings>, StripeSettingsValidator>();
builder.Services.AddSingleton<Stripe.IStripeClient>(sp =>
{
    var billing = sp.GetRequiredService<IOptions<StripeSettings>>().Value;
    return new Stripe.StripeClient(billing.Enabled ? billing.SecretKey : "sk_test_billing_disabled");
});
builder.Services.AddScoped<IStripeGateway, StripeGateway>();
builder.Services.AddScoped<BillingService>();
builder.Services.AddSingleton<StripeWebhookVerifier>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHostedService<StripeStartupCheck>();
//Configuring Services
builder.Services.AddScoped<IGeocodingService, NominatimGeocodingService>();
builder.Services.AddScoped<IDistanceService, OpenRouteDistanceService>();
builder.Services.AddScoped<JwtAuth>();
// Explicit development-only mode for disposable database tests without sending email.
if (builder.Configuration.GetValue<bool>("Email:UseFake"))
{
    if (!builder.Environment.IsDevelopment())
        throw new InvalidOperationException("Email:UseFake is only allowed in Development.");
    builder.Services.AddScoped<IEmailService, FakeEmailService>();
}
else
{
    builder.Services.AddScoped<IEmailService, ResendEmailService>();
}
builder.Services.AddScoped<ITripCalculationPolicy, EnsureCalcTwoTier>();
// Database
builder.Services.AddDbContext<DistanceTrackerContext>(options =>
    options.UseNpgsql(postgresConnection));
var redisConnectionString = builder.Configuration.GetSection("Redis")["ConnectionString"];

if (!string.IsNullOrEmpty(redisConnectionString))
{
    try
    {
        var redis = ConnectionMultiplexer.Connect(redisConnectionString);
        builder.Services.AddSingleton<IConnectionMultiplexer>(redis);
        Console.WriteLine(" Redis connected");
    }
    catch (Exception ex)
    {
        Console.WriteLine($" Redis connection failed: {ex.Message}");
        Console.WriteLine("Continuing without Redis...");
        // Don't register Redis service
    }
}
else
{
    Console.WriteLine(" Redis not configured, skipping...");
}
// add Identity
builder.Services.AddIdentity<ApplicationUser, IdentityRole>(options =>
{
    // Password strength
    options.Password.RequiredLength = 8;
    options.Password.RequireDigit = true;
    options.Password.RequireLowercase = true;
    options.Password.RequireUppercase = true;
    options.Password.RequireNonAlphanumeric = false;

    // Lockout protection
    options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(10);
    options.Lockout.MaxFailedAccessAttempts = 5;
    options.Lockout.AllowedForNewUsers = true;

    // User
    options.User.RequireUniqueEmail = true;
})
.AddEntityFrameworkStores<DistanceTrackerContext>()
.AddDefaultTokenProviders();
// add Authentication and Authorization
builder.Services.AddAuthentication(options =>
{
    options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
})
.AddJwtBearer(options => {
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidateAudience = true ,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        ValidIssuer = issuer,
        ValidAudience = audience,
        IssuerSigningKey = new SymmetricSecurityKey(System.Text.Encoding.UTF8.GetBytes(secretKey))
    };
});
builder.Services.AddAuthorization();

builder.Services.AddRateLimiter(options =>
{
    options.AddPolicy("TripsWritePolicy", context => {
            var userId= context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            var partitionKey = userId?? context.Connection.RemoteIpAddress?.ToString()?? "anonymous";
            return RateLimitPartition.GetFixedWindowLimiter(
                partitionKey: partitionKey,
                factory: partition => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 5,
                    Window = TimeSpan.FromMinutes(1),
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                    QueueLimit = 0
                });
            }
        );
    options.AddPolicy("RegisterPolicy", context => {
        var partitionKey =  context.Connection.RemoteIpAddress?.ToString() ?? "anonymous";
        return RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: partitionKey,
            factory: partition => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 5,
                Window = TimeSpan.FromMinutes(10),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 0
            });
    }

        );
    options.AddPolicy("LoginPolicy", context =>
    {
        var partitionKey =  context.Connection.RemoteIpAddress?.ToString() ?? "anonymous";
        return RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: partitionKey,
            factory: partition => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 5,
                Window = TimeSpan.FromMinutes(5),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 0
            });
    });
    options.AddPolicy("EmailPolicy", context => {
        var partitionKey = context.Connection.RemoteIpAddress?.ToString() ?? "anonymous";
        return RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: partitionKey,
            factory: partition => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 2,
                Window = TimeSpan.FromMinutes(1),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 0
            });
    }

        );
    options.OnRejected =    (context, token) =>
    {
        context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        return new ValueTask( context.HttpContext.Response.WriteAsync("Too many requests. Please try again later.", token));
    };
});
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll",
        policy => policy
            .AllowAnyOrigin()
            .AllowAnyHeader()
            .AllowAnyMethod());
});
var app = builder.Build();
// This maintenance command never starts HTTP or creates Stripe resources.
var recoveryIndex = Array.IndexOf(args, "--recover-stripe-customer");
if (recoveryIndex >= 0)
{
    if (recoveryIndex + 1 >= args.Length || args[recoveryIndex + 1].StartsWith("--"))
    {
        Console.Error.WriteLine("Usage: --recover-stripe-customer USER_ID [--confirmed-no-customer]");
        Environment.ExitCode = 1;
        return;
    }
    await using var scope = app.Services.CreateAsyncScope();
    try
    {
        Console.WriteLine(await scope.ServiceProvider.GetRequiredService<BillingService>().RecoverCustomerAsync(
            args[recoveryIndex + 1], args.Contains("--confirmed-no-customer"), CancellationToken.None));
    }
    catch (BillingConflictException ex) { Console.Error.WriteLine(ex.Message); Environment.ExitCode = 1; }
    catch (BillingUserNotFoundException) { Console.Error.WriteLine("Application user not found."); Environment.ExitCode = 1; }
    catch (Exception ex) { Console.Error.WriteLine($"Billing recovery failed ({ex.GetType().Name}). Check configuration and provider connectivity."); Environment.ExitCode = 1; }
    return;
}
// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
    app.UseCors("AllowAll");
}
app.UseHttpsRedirection();

app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.MapControllers();

app.Run();
