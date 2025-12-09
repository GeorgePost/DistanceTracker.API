
using System.Text.Json;
using DistanceTracker.API.DTOs;
using System.Net.Http.Json;


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
                ?? throw new ArgumentNullException("Open Route Service BaseUrl not configured");

            // Set User-Agent once
            var ApiKey = configuration["ExternalApis:OpenRouteService:ApiKey"]
                ?? throw new ArgumentNullException("Open Route Service ApiKey not configured");
            _httpClient.DefaultRequestHeaders.Add("Authorization", ApiKey);
        }
        public async Task<List<decimal>> CalculateRouteDistancesAsync(List<(decimal Latitude, decimal Longitude)> coordinates)
        {
            //Url encode and make url
            var url = $"{_baseUrl}/v2/directions/driving-car";
            //request
            var requestBody = new
            {
                coordinates = coordinates
                    .Select(c => new[] { c.Longitude, c.Latitude })
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
        public async Task<decimal> CalculateDistanceAsync(decimal lat1, decimal lon1, decimal lat2, decimal lon2)
        {
            var coordinates = new List<(decimal, decimal)>
            {
                (lat1, lon1),
                (lat2, lon2)
            };

            var distances = await CalculateRouteDistancesAsync(coordinates);
            return distances.FirstOrDefault();
        }
    }
}
