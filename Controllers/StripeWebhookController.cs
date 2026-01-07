using DistanceTracker.API.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace DistanceTracker.API.Controllers
{
    [ApiController]
    [Route("api/webhooks/stripe")]
    public class StripeWebhookController : ControllerBase
    {
        private readonly string _webhookSecret;
        private readonly UserManager<ApplicationUser> _userManager;
        public StripeWebhookController(IConfiguration configuration, UserManager<ApplicationUser> userManager)
        {
            _webhookSecret = configuration["Stripe:WebhookSecret"]
                ?? throw new ArgumentNullException("Stripe WebhookSecret not configured");
            _userManager = userManager;
        }
        [HttpPost]
        public async Task<IActionResult> Handle()
        {
            var json = await new StreamReader(Request.Body).ReadToEndAsync();
            try
            {
                var stripeSignature = Request.Headers["Stripe-Signature"];
                var stripeEvent = Stripe.EventUtility.ConstructEvent(
                    json,
                    stripeSignature,
                    _webhookSecret
                );
                if (stripeEvent.Type == "checkout.session.completed")
                {
                    var session = stripeEvent.Data.Object as Stripe.Checkout.Session;
                    var userId = session.Metadata["userId"];
                    var user = await _userManager.FindByIdAsync(userId);
                    if (user != null)
                    {
                        user.Tier = UserTier.Paid;
                        user.StripeCustomerId = session.CustomerId;
                        user.StripeSubscriptionId = session.SubscriptionId;
                        await _userManager.UpdateAsync(user);
                    }
                    else
                    {
                        // Log:user not found
                    }
                }
                if (stripeEvent.Type == "customer.subscription.deleted")
                {
                    // Handle subscription cancellation
                }
                return Ok();
            }
            catch (Stripe.StripeException e)
            {
                return BadRequest();


            }
        }
    }
}
