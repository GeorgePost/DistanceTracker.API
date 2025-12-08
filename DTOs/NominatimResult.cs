namespace DistanceTracker.API.DTOs
{
    public class NominatimResult
    {
        public string lat { get; set; }
        public string lon { get; set; }
        public string display_name { get; set; } = string.Empty;
    }
}
