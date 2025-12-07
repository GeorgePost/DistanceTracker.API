namespace DistanceTracker.API.Models
{
    public class TripStop
    {
        public Guid Id { get; set; }
        public Guid TripId { get; set; }

        public string Address { get; set; } = string.Empty;
        public decimal Latitude { get; set; }
        public decimal Longitude { get; set; }
        public int Order { get; set; }
        public decimal? DistanceToNext { get; set; }

        public Trip Trip { get; set; } = null!;
    }
}
