
namespace DistanceTracker.API.Services
{
    public interface IGeocodingService
    {
        Task<(decimal Latitude, decimal Longitude)> GeocodeAddressAsync(string address);
    }
}
