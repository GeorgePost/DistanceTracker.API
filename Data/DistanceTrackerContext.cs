using Microsoft.EntityFrameworkCore;
using DistanceTracker.API.Models;
namespace DistanceTracker.API.Data
{
    public class DistanceTrackerContext:DbContext
    {
        public DistanceTrackerContext(DbContextOptions<DistanceTrackerContext> opotions):base(opotions)
        {
        }
        public DbSet<Trip> Trips { get; set; }
        public DbSet<TripStop> TripStops { get; set; }
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Trip>()
                .HasMany(t => t.TripStops)
                .WithOne(ts => ts.Trip)
                .HasForeignKey(ts => ts.TripId)
                .OnDelete(DeleteBehavior.Cascade);
        }
    }
}
