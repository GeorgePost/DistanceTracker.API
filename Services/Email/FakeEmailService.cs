
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
    }
}
