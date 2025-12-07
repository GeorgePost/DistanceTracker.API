namespace DistanceTracker.API.DTOs
{
    public class TripStopDTO
    {
        public Guid Id { get; set; }
        public string Address { get; set; }
        public decimal Latitude { get; set; }
        public decimal Longitude { get; set; }
        public int Order { get; set; }
        public decimal? DistanceToNext { get; set; }
    }
}
