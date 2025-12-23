using DistanceTracker.API.Models;

namespace DistanceTracker.API.Services
{
    public interface ITripCalculationPolicy
    {
        Task EnsureCanCalculateAsync(ApplicationUser User);
    }
}
