namespace DistanceTracker.API.Services
{
    public interface IDistanceService
    {
        Task<List<decimal>> CalculateDistanceAsync(List<(decimal Latitude, decimal Longitude)> coordinates);
    }
}
