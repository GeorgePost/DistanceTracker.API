using System.Text.Json.Serialization;

namespace DistanceTracker.API.DTOs
{
    public class ForgotPasswordDTO
    {
        [JsonPropertyName("email")]
        public string Email { get; set; } = string.Empty;
    }
}
