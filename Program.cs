using DistanceTracker.API.Data;
using DistanceTracker.API.Models;
using DistanceTracker.API.Services;
using DistanceTracker.API.Services.Email;
using DistanceTracker.API.Services.Payments;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using SendGrid;
using StackExchange.Redis;
using System;
using System.Collections.Concurrent;
using System.Security.Claims;
using System.Threading.RateLimiting;
var builder = WebApplication.CreateBuilder(args);
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
// Add HttpClient and Geocoding Service
builder.Services.AddHttpClient();
builder.Services.Configure<SendGridOptions>(builder.Configuration.GetSection("SendGrid"));

builder.Services.AddSingleton<ISendGridClient>(sp =>
{
    var options = sp.GetRequiredService<IOptions<SendGridOptions>>().Value;
    return new SendGridClient(options.ApiKey);
});
builder.Services.Configure<StripeOptions>(builder.Configuration.GetSection("Stripe"));

builder.Services.AddScoped<IGeocodingService, NominatimGeocodingService>();
builder.Services.AddScoped<IDistanceService, OpenRouteDistanceService>();
builder.Services.AddScoped<JwtAuth>();
builder.Services.AddScoped<IEmailService, SendGridEmailService>();
builder.Services.AddScoped<ITripCalculationPolicy, EnsureCalcTwoTier>();
// Database
builder.Services.AddDbContext<DistanceTrackerContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Postgres")));
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
// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
    app.UseCors("AllowAll");
}
// Ensure Stripe is configured in Production
if (app.Environment.IsProduction())
{
    var stripeKey = app.Configuration["Stripe:SecretKey"];
    if (string.IsNullOrWhiteSpace(stripeKey))
        throw new InvalidOperationException("Stripe is not configured.");
}

app.UseHttpsRedirection();

app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.MapControllers();

app.Run();
