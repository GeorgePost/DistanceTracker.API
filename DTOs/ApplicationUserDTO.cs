namespace DistanceTracker.API.DTOs
{
    public class ApplicationUserDTO
    {
        
        public Guid UserId { get; init; }
        public string UserEmail { get; init; } = string.Empty;
    }
}
