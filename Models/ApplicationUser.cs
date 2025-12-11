
using Microsoft.AspNetCore.Identity;

namespace DistanceTracker.API.Models
{
    public class ApplicationUser : IdentityUser
    {
        public List<Trip> Trips { get; set; } = new();
    }
}
