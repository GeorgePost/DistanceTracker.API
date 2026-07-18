using DistanceTracker.API.Models;
using Microsoft.Extensions.Options;
using Resend;

namespace DistanceTracker.API.Services.Email
{
    public sealed class ResendEmailService : IEmailService
    {
        private readonly IResend _resendClient;
        private readonly ResendOptions _options;
        private readonly IConfiguration _config;
        private string FrontendUrl =>
    _config.GetValue<string>("Frontend:BaseUrl")
    ?? throw new InvalidOperationException("Frontend:BaseUrl not configured");
        public ResendEmailService(IResend resendClient, IOptions<ResendOptions> options, IConfiguration config)
        {
            _resendClient = resendClient;
            _options = options.Value;
            _config = config;
        }
        public async Task SendEmailAsync(string toEmail, string subject, string body)
        {
            var from = $"{_options.FromName} <{_options.SenderEmail}>";

            var msg = new EmailMessage
            {
                From = from,
                To = toEmail,
                Subject = subject,
                HtmlBody = body
            };

            var response = await _resendClient.EmailSendAsync(msg);

            if (!response.Success)
            {
                throw new Exception(
                    $"Resend failed: {response.Exception}, {response.Content}");
            }
        }
        public async Task SendEmailConfirmationAsync(ApplicationUser user, string token)
        {
            if (string.IsNullOrWhiteSpace(user.Email))
                throw new InvalidOperationException("User does not have an email address.");
            var confirmationLink = $"{FrontendUrl}/confirm-email?userId={user.Id}&token={Uri.EscapeDataString(token)}";
            var subject = "Confirm Your Email";
            var body = $@"
                <p>Hi,</p>
                <p>Thank you for registering. Please confirm your email by clicking the link below:</p>
                <p><a href=""{confirmationLink}"">Confirm Email</a></p>
                <p>If you did not register, please ignore this email.</p>
                <p>Best regards,<br/>{_options.FromName}</p>
            ";
            await SendEmailAsync(user.Email, subject, body);
        }
        public async Task SendPasswordResetAsync(ApplicationUser user, string token)
        {
            if (string.IsNullOrWhiteSpace(user.Email))
                throw new InvalidOperationException("User does not have an email address.");

            var resetLink =
                $"{FrontendUrl}/reset-password?userId={user.Id}&token={Uri.EscapeDataString(token)}";

            var subject = "Reset Your Password";
            var body = $@"
                <p>Hi,</p>
                <p>You requested a password reset.</p>
                <p>Click the link below to reset your password:</p>
                <p><a href=""{resetLink}"">Reset Password</a></p>
                <p>If you did not request this, you can safely ignore this email.</p>
                <p>– {_options.FromName}</p>
            ";

            await SendEmailAsync(user.Email, subject, body);
    }
    }
}
