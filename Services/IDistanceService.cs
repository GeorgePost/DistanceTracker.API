namespace DistanceTracker.API.Services
{
    public interface IDistanceService
    {
        Task<decimal> CalculateDistanceAsync(decimal lat1, decimal lon1, decimal lat2, decimal lon2);
    }
}
