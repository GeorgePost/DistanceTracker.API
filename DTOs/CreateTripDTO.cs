namespace DistanceTracker.API.DTOs
{
    public class CreateTripDto
    {
        public DateTime Date { get; set; }
        public List<string> Stops { get; set; } = new();
    }
}
