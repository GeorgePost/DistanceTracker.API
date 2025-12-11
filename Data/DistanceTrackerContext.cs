using Microsoft.EntityFrameworkCore;
using DistanceTracker.API.Models;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
namespace DistanceTracker.API.Data
{
    public class DistanceTrackerContext: IdentityDbContext<ApplicationUser>
    {
        public DistanceTrackerContext(DbContextOptions<DistanceTrackerContext> opotions):base(opotions)
        {
        }
        public DbSet<Trip> Trips { get; set; }
        public DbSet<TripStop> TripStops { get; set; }
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Entity<Trip>()
                .HasMany(t => t.TripStops)
                .WithOne(ts => ts.Trip)
                .HasForeignKey(ts => ts.TripId)
                .OnDelete(DeleteBehavior.Cascade);
            modelBuilder.Entity<ApplicationUser>()
                .HasMany(u=>u.Trips)
                .WithOne(t=> t.User)
                .HasForeignKey(t=> t.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        }
    }
}
