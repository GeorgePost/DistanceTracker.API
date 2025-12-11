using DistanceTracker.API.Data;
using DistanceTracker.API.Models;
using DistanceTracker.API.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
//Services
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
// Add HttpClient and Geocoding Service
builder.Services.AddHttpClient();
builder.Services.AddScoped<IGeocodingService, NominatimGeocodingService>();
builder.Services.AddScoped<IDistanceService, OpenRouteDistanceService>();
// Database
builder.Services.AddDbContext<DistanceTrackerContext>(options => options.UseSqlite("Data Source=distancetracker.db"));

// add Identity
builder.Services.AddIdentity<ApplicationUser, IdentityRole>(options =>
{
    options.Password.RequireNonAlphanumeric = false;
    options.Password.RequireUppercase = false;
    options.Password.RequireDigit = false;
    options.Password.RequireLowercase = false;
    options.Password.RequiredLength = 6;
})
.AddEntityFrameworkStores<DistanceTrackerContext>()
.AddDefaultTokenProviders();
// add Authentication and Authorization
builder.Services.AddAuthentication();
builder.Services.AddAuthorization();
var app = builder.Build();
// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();

app.UseAuthorization();

app.MapControllers();

app.Run();
