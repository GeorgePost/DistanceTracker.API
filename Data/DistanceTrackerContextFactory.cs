using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace DistanceTracker.API.Data;

// EF tooling does not start Redis, hosted services or call Stripe.
public sealed class DistanceTrackerContextFactory : IDesignTimeDbContextFactory<DistanceTrackerContext>
{
    public DistanceTrackerContext CreateDbContext(string[] args)
    {
        var environment = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
            ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Development";
        var configuration = new ConfigurationBuilder().SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile($"appsettings.{environment}.json", optional: true)
            .AddEnvironmentVariables().Build();
        var connection = configuration.GetConnectionString("Postgres")
            ?? throw new InvalidOperationException("ConnectionStrings:Postgres must be configured.");
        return new(new DbContextOptionsBuilder<DistanceTrackerContext>().UseNpgsql(connection).Options);
    }
}
