using System.Security.Claims;
using DistanceTracker.API.Services.Billing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DistanceTracker.API.Controllers;

[ApiController]
[Authorize]
[Route("api/[controller]")]
public sealed class PaymentsController(BillingService billing, ILogger<PaymentsController> logger) : ControllerBase
{
    [HttpPost("create-checkout")]
    public Task<IActionResult> CreateCheckoutSession(CancellationToken ct) =>
        ExecuteAsync(async id => Ok(new { url = (await billing.CreateCheckoutAsync(id, ct)).Url }));

    [HttpGet("subscription")]
    public Task<IActionResult> GetSubscription(CancellationToken ct) =>
        ExecuteAsync(async id => Ok(await billing.GetStatusAsync(id, ct)));

    [HttpPost("create-portal")]
    public Task<IActionResult> CreatePortal(CancellationToken ct) =>
        ExecuteAsync(async id => Ok(new { url = await billing.CreatePortalAsync(id, ct) }));

    private async Task<IActionResult> ExecuteAsync(Func<string, Task<IActionResult>> action)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null) return Unauthorized();
        try { return await action(userId); }
        catch (BillingUserNotFoundException) { return Unauthorized(); }
        catch (BillingConflictException ex) { return Conflict(new { error = ex.Message }); }
        catch (BillingUnavailableException) { return StatusCode(503, new { error = "Billing is disabled." }); }
        catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            // Log type only: Stripe exceptions may contain provider details or sensitive parameters.
            logger.LogError("Billing operation failed ({ExceptionType}).", ex.GetType().Name);
            return StatusCode(503, new { error = "Billing is temporarily unavailable. Please retry." });
        }
    }
}
