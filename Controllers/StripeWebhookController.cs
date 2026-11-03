using DistanceTracker.API.Services.Billing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace DistanceTracker.API.Controllers;

[ApiController]
[AllowAnonymous]
[Route("api/webhooks/stripe")]
public sealed class StripeWebhookController(BillingService billing, StripeWebhookVerifier verifier,
    IOptions<StripeSettings> settings, ILogger<StripeWebhookController> logger) : ControllerBase
{
    [HttpPost]
    [RequestSizeLimit(1024 * 1024)]
    public async Task<IActionResult> Handle(CancellationToken ct)
    {
        if (!settings.Value.Enabled) return StatusCode(503);
        var signature = Request.Headers["Stripe-Signature"].ToString();
        if (string.IsNullOrWhiteSpace(signature)) return BadRequest("Missing Stripe signature.");
        var body = await new StreamReader(Request.Body).ReadToEndAsync(ct);
        BillingEvent message;
        try { message = verifier.Verify(body, signature); }
        catch (InvalidWebhookException) { return BadRequest("Invalid Stripe event or signature."); }

        try
        {
            await billing.ProcessEventAsync(message, ct);
            return Ok();
        }
        catch (InvalidWebhookException) { return BadRequest("Invalid Stripe event."); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            logger.LogError("Stripe event {EventId} could not be committed ({ExceptionType}); delivery should retry.",
                message.Id, ex.GetType().Name);
            return StatusCode(503);
        }
    }
}
