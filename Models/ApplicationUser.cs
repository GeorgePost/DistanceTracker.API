
using Microsoft.AspNetCore.Identity;

namespace DistanceTracker.API.Models
{
    public enum UserTier
    {
        Free=0,
        Paid=1,
    }
    public class ApplicationUser : IdentityUser
    {
        public List<Trip> Trips { get; set; } = new();
        public UserTier Tier { get; set; } = UserTier.Free;
    }
}
