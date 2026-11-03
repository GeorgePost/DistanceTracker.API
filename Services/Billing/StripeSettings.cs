using Microsoft.Extensions.Options;

namespace DistanceTracker.API.Services.Billing;

public sealed class StripeSettings
{
    public bool Enabled { get; set; } = true;
    public string SecretKey { get; set; } = "";
    public string WebhookSecret { get; set; } = "";
    public string PriceId { get; set; } = "";
    public bool AllowPastDueAccess { get; set; } = true;
}

public sealed class StripeSettingsValidator(IConfiguration configuration, IHostEnvironment environment)
    : IValidateOptions<StripeSettings>
{
    public ValidateOptionsResult Validate(string? name, StripeSettings settings)
    {
        if (!settings.Enabled) return ValidateOptionsResult.Success;
        var errors = new List<string>();
        static bool Clean(string value) => !string.IsNullOrWhiteSpace(value) && value == value.Trim()
            && !value.Contains("replace-with", StringComparison.OrdinalIgnoreCase);
        if (!Clean(settings.SecretKey) ||
            !(settings.SecretKey.StartsWith("sk_test_") || settings.SecretKey.StartsWith("sk_live_")))
            errors.Add("Stripe:SecretKey must be a Stripe secret API key without surrounding whitespace.");
        if (!Clean(settings.WebhookSecret) || !settings.WebhookSecret.StartsWith("whsec_"))
            errors.Add("Stripe:WebhookSecret must be a webhook signing secret without surrounding whitespace.");
        if (!Clean(settings.PriceId) || !settings.PriceId.StartsWith("price_"))
            errors.Add("Stripe:PriceId must be a Stripe recurring price ID.");
        var frontend = configuration["Frontend:BaseUrl"];
        if (!Uri.TryCreate(frontend, UriKind.Absolute, out var uri) ||
            (uri.Scheme != "http" && uri.Scheme != "https") ||
            !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            (!environment.IsDevelopment() && uri.Scheme != "https"))
            errors.Add("Frontend:BaseUrl must be an absolute HTTP(S) URL (HTTPS outside Development), without query, fragment or credentials.");
        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}

public sealed class StripeStartupCheck(IServiceProvider services, IOptions<StripeSettings> settings,
    ILogger<StripeStartupCheck> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!settings.Value.Enabled) return;
        using var scope = services.CreateScope();
        try
        {
            await scope.ServiceProvider.GetRequiredService<IStripeGateway>().ValidatePriceAsync(cancellationToken);
        }
        catch (Stripe.StripeException)
        {
            // Never include provider response bodies, authorization headers or keys in startup errors.
            throw new InvalidOperationException("Stripe startup validation failed. Check the API key, recurring price, account and network connectivity.");
        }
        logger.LogInformation("Stripe recurring price validated. Snapshot webhook API version: {ApiVersion}. Delivery requires a listener or registered endpoint.", Stripe.StripeConfiguration.ApiVersion);
    }
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
