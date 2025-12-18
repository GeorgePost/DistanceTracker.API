using System.Text.Json.Serialization;

namespace DistanceTracker.API.DTOs
{
    public class ResetPasswordDTO
    {
        [JsonPropertyName("email")]
        public string Email { get; set; } = string.Empty;
        [JsonPropertyName("token")]
        public string Token { get; set; } = string.Empty;
        [JsonPropertyName("newPassword")]
        public string NewPassword { get; set; } = string.Empty;
    }
}
