
using DistanceTracker.API.Models;

namespace DistanceTracker.API.Services.Email
{
    public class FakeEmailService : IEmailService
    {
        private readonly ILogger<FakeEmailService> _logger;
        public FakeEmailService(ILogger<FakeEmailService> logger)
        {
            _logger = logger;
        }
        public Task SendEmailAsync(string toEmail, string subject, string body)
        {
            _logger.LogInformation(
                """
                ===== FAKE EMAIL SENT =====
                To: {To}
                Subject: {Subject}
                Body:
                {Body}
                ==========================
                """,
                toEmail,
                subject,
                body
            );
            return Task.CompletedTask;
        }
        public Task SendEmailConfirmationAsync(ApplicationUser user,string token)
        {
            _logger.LogInformation(
                """
                ===== FAKE EMAIL SENT =====
                To: {To}
                Subject: {Subject}
                Body:
                {Body}
                ==========================
                """,
                user.Email,
                "Confirm Your Email",
                $"Your token is {token}"
            );
            return Task.CompletedTask;
        }
        public Task SendPasswordResetAsync(ApplicationUser user, string token)
        {
            _logger.LogInformation(
                """
                ===== FAKE EMAIL SENT =====
                To: {To}
                Subject: {Subject}
                Body:
                {Body}
                ==========================
                """,
                user.Email,
                "Forgot Password - DistanceTracker",
                $"Your reset token:\n\n{token}"
            );
            return Task.CompletedTask;
        }
    }
}
