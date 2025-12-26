namespace DistanceTracker.API.Services.Email
{
    public class SendGridOptions
    {
        public string ApiKey { get; set; } = null!;
        public string SenderEmail { get; set; } = null!;
        public string FromName { get; set; } = null!;
    }
}
