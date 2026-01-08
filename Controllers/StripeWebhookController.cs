using DistanceTracker.API.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

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
                    if (session?.Metadata == null || !session.Metadata.TryGetValue("userId", out var userId))
                    {
                        return Ok(); // Don't fail webhook
                    }
                    var user = await _userManager.FindByIdAsync(session.Metadata["userId"]);
                    if (user != null)
                    {
                        user.Tier = UserTier.Paid;
                        user.StripeCustomerId = session.CustomerId;
                        user.StripeSubscriptionId = session.SubscriptionId;
                        await _userManager.UpdateAsync(user);
                    }
                    else
                    {
                        return BadRequest("Invalid Stripe Signature");
                    }
                }
                if (stripeEvent.Type == "customer.subscription.deleted")
                {
                    var subscription = stripeEvent.Data.Object as Stripe.Subscription;
                    var user = await _userManager.Users
                        .FirstOrDefaultAsync(u => u.StripeSubscriptionId == subscription.Id);

                    if (user != null)
                    {
                        user.Tier = UserTier.Free;
                        user.StripeSubscriptionId = null;
                        await _userManager.UpdateAsync(user);
                    }
                    else
                    {
                        return BadRequest("Invalid Stripe Signature");
                    }
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
