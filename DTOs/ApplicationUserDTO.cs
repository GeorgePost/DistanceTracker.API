namespace DistanceTracker.API.DTOs
{
    public class ApplicationUserDTO
    {
        
        public Guid UserId { get; init; }
        public string UserName { get; init; } = string.Empty;
        public string UserEmail { get; init; } = string.Empty;
    }
}
