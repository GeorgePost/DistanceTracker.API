
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
        public string? StripeCustomerId { get; set; }
        public string? StripeCustomerCreationAttemptId { get; set; }
        public string? StripeCustomerCreationEmail { get; set; }
        public DateTime? StripeCustomerCreationStartedAtUtc { get; set; }
        public bool StripeCustomerCreationNeedsReview { get; set; }
        public string? StripeSubscriptionId { get; set; }
        public string? StripeSubscriptionStatus { get; set; }
        public bool StripeCancelAtPeriodEnd { get; set; }
        public DateTime? StripeCurrentPeriodEndUtc { get; set; }
        public DateTime? StripeLastSyncedAtUtc { get; set; }
        public string? StripeCheckoutSessionId { get; set; }
        public int StripeCheckoutGeneration { get; set; }
    }
}
