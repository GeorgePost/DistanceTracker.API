

using System.Text.Json.Serialization;

namespace DistanceTracker.API.DTOs
{
    public class OpenRouteResponse
    {
        [JsonPropertyName("routes")]
        public List<RouteInfo> Routes { get; set; } = new();
    }

    public class RouteInfo
    {
        [JsonPropertyName("summary")]
        public RouteSummary Summary { get; set; } = new();

        [JsonPropertyName("segments")]
        public List<RouteSegment> Segments { get; set; } = new();
    }

    public class RouteSummary
    {
        [JsonPropertyName("distance")]
        public decimal Distance { get; set; }  // Total distance in meters

        [JsonPropertyName("duration")]
        public decimal Duration { get; set; }  // Total duration in seconds
    }

    public class RouteSegment
    {
        [JsonPropertyName("distance")]
        public decimal Distance { get; set; }  // Segment distance in meters

        [JsonPropertyName("duration")]
        public decimal Duration { get; set; }  // Segment duration in seconds
    }
}
