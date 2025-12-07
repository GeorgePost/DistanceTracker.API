namespace DistanceTracker.API.Models
{
    public class Trip
    {
        public Guid Id { get; set; }
        public DateTime Date { get; set; }
        public decimal TotalDistance { get; set; }
        public string? Notes { get; set; }
        public List<TripStop> TripStops { get;set; } = new();
    }
}
