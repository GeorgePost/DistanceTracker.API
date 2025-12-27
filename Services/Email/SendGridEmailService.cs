using DistanceTracker.API.Models;
using Microsoft.Extensions.Options;
using SendGrid;
using SendGrid.Helpers.Mail;

namespace DistanceTracker.API.Services.Email
{
    public class SendGridEmailService : IEmailService
    {
        private readonly ISendGridClient _sendGridClient;
        private readonly SendGridOptions _options;
        private readonly IConfiguration _config;
        public SendGridEmailService(ISendGridClient sendGridClient, IOptions<SendGridOptions> options, IConfiguration config)
        {
            _sendGridClient = sendGridClient;
            _options = options.Value;
            _config = config;
        }
        public async Task SendEmailAsync(string toEmail, string subject, string body)
        {
            var msg= new SendGridMessage()
            {
                From = new EmailAddress(_options.SenderEmail, _options.FromName),
                Subject = subject,
                HtmlContent = body
            }; 
            msg.AddTo(new EmailAddress(toEmail));
            await _sendGridClient.SendEmailAsync(msg);
        }
        public async Task SendEmailConfirmationAsync(ApplicationUser user, string token)
        {
            var frontendUrl = _config.GetSection("Frontend")["BaseUrl"] ?? throw new ArgumentNullException("FrontendUrl not configured");
            var confirmationLink = $"{frontendUrl}/confirm-email?userId={user.Id}&token={Uri.EscapeDataString(token)}";
            var subject = "Confirm Your Email";
            var body = $@"
                <p>Hi,</p>
                <p>Thank you for registering. Please confirm your email by clicking the link below:</p>
                <p><a href=""{confirmationLink}"">Confirm Email</a></p>
                <p>If you did not register, please ignore this email.</p>
                <p>Best regards,<br/>{_options.FromName}</p>
            ";
            var msg = new SendGridMessage()
            {
                From = new EmailAddress(_options.SenderEmail, _options.FromName),
                Subject = subject,
                HtmlContent = body
            };
            //recipient
            msg.AddTo(new EmailAddress(user.Email, user.UserName));
            var response = await _sendGridClient.SendEmailAsync(msg);
            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Body.ReadAsStringAsync();
                throw new Exception($"SendGrid failed: {response.StatusCode}, {error}");
            }
        }
        public async Task SendPasswordResetAsync(ApplicationUser user, string token)
        {
            var frontendUrl = _config.GetSection("Frontend")["BaseUrl"]
                ?? throw new ArgumentNullException("Frontend:BaseUrl not configured");

            var resetLink =
                $"{frontendUrl}/reset-password?userId={user.Id}&token={Uri.EscapeDataString(token)}";

            var subject = "Reset Your Password";
            var body = $@"
                <p>Hi,</p>
                <p>You requested a password reset.</p>
                <p>Click the link below to reset your password:</p>
                <p><a href=""{resetLink}"">Reset Password</a></p>
                <p>If you did not request this, you can safely ignore this email.</p>
                <p>– {_options.FromName}</p>
            ";

            var msg = new SendGridMessage
            {
                From = new EmailAddress(_options.SenderEmail, _options.FromName),
                Subject = subject,
                HtmlContent = body
            };

            msg.AddTo(new EmailAddress(user.Email!));

            var response = await _sendGridClient.SendEmailAsync(msg);
            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Body.ReadAsStringAsync();
                throw new Exception($"SendGrid failed: {response.StatusCode}, {error}");
            }
        }
    }
}
