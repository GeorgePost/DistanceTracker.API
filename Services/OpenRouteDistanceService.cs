
using System.Text.Json;
using DistanceTracker.API.DTOs;



namespace DistanceTracker.API.Services
{
    public class OpenRouteDistanceService :IDistanceService
    {
        private readonly HttpClient _httpClient;
        private readonly string _baseUrl;
        public OpenRouteDistanceService(HttpClient httpClient, IConfiguration configuration)
        {
            _httpClient = httpClient;
            _baseUrl = configuration["ExternalApis:OpenRouteService:BaseUrl"]
                ?? throw new ArgumentNullException("Nominatim BaseUrl not configured");

            // Set User-Agent once
            var ApiKey = configuration["ExternalApis:OpenRouteService:ApiKey"]
                ?? throw new ArgumentNullException("Nominatim ApiKey not configured");
            _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {ApiKey}");
        }
        public async Task<List<decimal>> CalculateDistanceAsync(List<(decimal Latitude, decimal Longitude)> coordinates)
        {
            //Url encode and make url
            var url = $"{_baseUrl}/v2/directions/driving-car";
            //request
            var requestBody = new
            {
                coordinates = coordinates
                    .Select(c => new[] { c.Latitude, c.Longitude })
                    .ToArray()
            };
           
            var response = await _httpClient.PostAsJsonAsync(url,requestBody);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync();
            var result= JsonSerializer.Deserialize<OpenRouteResponse>(json);
            //Parse JSON to get distance
            if (result?.Routes == null || result.Routes.Count == 0)
            {
                throw new Exception("No routes found in OpenRouteService response.");
            }
            // Extract distances between each pair of points
            var distance = result.Routes[0].Segments
                .Select(segment => segment.Distance / 1000m) // Convert meters to kilometers
                .ToList();
            return distance;
            
        }
    }
}
