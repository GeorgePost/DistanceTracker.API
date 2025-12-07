namespace DistanceTracker.API.DTOs
{
    public class TripResponseDTO
    {
        public Guid Id { get; set; }
        public DateTime Date { get; set; }
        public decimal TotalDistance { get; set; }
        public string? Notes { get; set; }
        public List<TripStopDTO> Stops { get; set; }
    }
}
