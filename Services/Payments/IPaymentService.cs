namespace DistanceTracker.API.Services.Payments
{
    public interface IPaymentService
    {
        Task<string> CreateCheckoutSessionAsync(Guid userId);
    }
}
