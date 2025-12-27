using DistanceTracker.API.Models;

namespace DistanceTracker.API.Services.Email
{
    public interface IEmailService
    {
        Task SendEmailAsync(
            string toEmail, 
            string subject, 
            string body
            );
        Task SendEmailConfirmationAsync(ApplicationUser user, string token);
        Task SendPasswordResetAsync(ApplicationUser user, string token);
    }
}
