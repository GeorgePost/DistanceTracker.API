namespace DistanceTracker.API.Models
{
    public class Trip
    {
        public Guid Id { get; set; }
        public DateTime DateUTC { get; set; }
        public decimal TotalDistance { get; set; }
        public string? Notes { get; set; }
        public string? UserId { get; set; }
        public ApplicationUser? User { get; set; }
        public List<TripStop> TripStops { get;set; } = new();
    }
}
