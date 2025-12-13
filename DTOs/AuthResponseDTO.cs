namespace DistanceTracker.API.DTOs
{
    public class AuthResponseDTO
    {
        public ApplicationUserDTO User { get; set; }=null!;
        public string Token { get; set; }=string.Empty;
    }
}
