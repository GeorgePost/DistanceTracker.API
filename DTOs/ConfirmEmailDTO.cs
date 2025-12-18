namespace DistanceTracker.API.DTOs
{
    public class ConfirmEmailDTO
    {
        public string UserId { get; set; } = string.Empty;
        public string Token { get; set; } = string.Empty;
    }
}
