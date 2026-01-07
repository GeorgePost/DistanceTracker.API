using DistanceTracker.API.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Stripe.Checkout;
using System.Security.Claims;

namespace DistanceTracker.API.Controllers
{
    [ApiController]
    [Authorize]
    [Route("api/[controller]")]
    public class PaymentsController : ControllerBase
    {
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly string _frontendUrl;
        private readonly string _priceId;
        public PaymentsController(UserManager<ApplicationUser> userManager, IConfiguration configuration)
        {
            _userManager = userManager;
            _frontendUrl = configuration["Frontend:BaseUrl"] ?? throw new ArgumentNullException("Frontend:BaseUrl not configured");
            _priceId= configuration["Stripe:PriceId"] ?? throw new ArgumentNullException("Stripe PriceId not configured");
        }
        [Authorize]
        [HttpPost("create-checkout")]
        public async Task<IActionResult> CreateCheckoutSession()
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (userId == null)
            {
                return Unauthorized();
            }
            var user = await _userManager.FindByIdAsync(userId);
            if (user == null)
            {
                return Unauthorized();
            }
            var options = new SessionCreateOptions
            {
                Mode = "subscription",
                LineItems = new List<SessionLineItemOptions>
                {
                    new()
                    {
                        Price = _priceId,
                        Quantity = 1,
                    },
                },
                SuccessUrl = $"{_frontendUrl}/success",
                CancelUrl = $"{_frontendUrl}/cancel",
                CustomerEmail = user.Email,
                Metadata = new Dictionary<string, string>
                {
                    ["userId"] = user.Id
                }
            };
            var service = new SessionService();
            var session = await service.CreateAsync(options);

            return Ok(new {session.Url});
        }
    }
}
