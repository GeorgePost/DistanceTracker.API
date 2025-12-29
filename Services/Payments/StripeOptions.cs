namespace DistanceTracker.API.Services.Payments
{
    public class StripeOptions
    {
        public string SecretKey { get; set; }
        public string WebhookSecret { get; set; }
    }
}
