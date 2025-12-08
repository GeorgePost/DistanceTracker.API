using DistanceTracker.API.DTOs;
using Microsoft.Extensions.Configuration;
using System.Text.Json;

namespace DistanceTracker.API.Services
{
    public class NominatimGeocodingService : IGeocodingService
    {
        private readonly HttpClient _httpClient;
        private readonly string _baseUrl;
        public NominatimGeocodingService(HttpClient httpClient, IConfiguration configuration)
        {
            _httpClient = httpClient;
            _baseUrl = configuration["ExternalApis:Nominatim:BaseUrl"]
                ?? throw new ArgumentNullException("Nominatim BaseUrl not configured");

            // Set User-Agent once
            var userAgent = configuration["ExternalApis:Nominatim:UserAgent"]
                ?? throw new ArgumentNullException("Nominatim UserAgent not configured");
            _httpClient.DefaultRequestHeaders.Add("User-Agent", userAgent);
        }
        public async Task<(decimal Latitude, decimal Longitude)> GeocodeAddressAsync(string address)
        {
            //Url encode and make url
            var encodedAddress = Uri.EscapeDataString(address);
            var url = $"{_baseUrl}/search?q={encodedAddress}&format=json&limit=1";
            //request
            var response= await _httpClient.GetAsync(url);
            var json = await response.Content.ReadAsStringAsync();
            var results = JsonSerializer.Deserialize <List<NominatimResult>>(json);
            //Add Delay to respect rate limit (1 request per second)
            await Task.Delay(1000);
            if (results != null && results.Count > 0)
            {
                var lat = Decimal.Parse(results[0].lat);
                var lon = Decimal.Parse(results[0].lon);
                return (lat, lon);
            }
            throw new Exception("Geocoding failed for address: " + address);
        }
    }
}
