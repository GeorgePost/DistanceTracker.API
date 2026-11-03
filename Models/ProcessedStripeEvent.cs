namespace DistanceTracker.API.Models;

public sealed class ProcessedStripeEvent
{
    public string Id { get; set; } = "";
    public string Type { get; set; } = "";
    public DateTime ProcessedAtUtc { get; set; }
}
