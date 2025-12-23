using DistanceTracker.API.Data;
using DistanceTracker.API.Models;
using Microsoft.EntityFrameworkCore;
namespace DistanceTracker.API.Services
{
    public class EnsureCalcTwoTier : ITripCalculationPolicy
    {
        private readonly DistanceTrackerContext _context;
        public EnsureCalcTwoTier(DistanceTrackerContext context ) {
            _context = context;

        }
        public async Task EnsureCanCalculateAsync(ApplicationUser User)
        {
            if(User.Tier == UserTier.Paid)
            {
                return;
            }
            var today = DateTime.UtcNow.Date;
            var usedToday = await _context.Trips.AnyAsync(t => 
                t.UserId == User.Id && t.LastCalculatedAtUTC >= today);
            if(usedToday)
                throw new Exception("Daily Calculation limit reached for Free Tier users.");
        }
    }
}
