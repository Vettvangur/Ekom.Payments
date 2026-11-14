using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Ekom.Payments;

/// <summary>
/// Finds the payment row a request refers to, using the registered <see cref="IOrderRetriever"/>s.
/// Replaces <c>EkomPayments.GetOrder</c>, which moved out when the provider registry moved to Ekom.
/// </summary>
public class PaymentOrderRetriever
{
    internal static readonly List<Type> orderRetrievers = new();

    private readonly ILogger<PaymentOrderRetriever> _logger;
    private readonly IServiceProvider _serviceProvider;

    public PaymentOrderRetriever(ILogger<PaymentOrderRetriever> logger, IServiceProvider serviceProvider)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
    }

    public OrderStatus? GetOrder(HttpRequest request, string? ppNameOverride = null)
    {
        foreach (var orType in orderRetrievers)
        {
            if (ActivatorUtilities.CreateInstance(_serviceProvider, orType) is not IOrderRetriever retriever)
            {
                _logger.LogError("Failed to create instance of {OrderRetriever}", orType);
                continue;
            }

            var order = retriever.Get(request, ppNameOverride!);
            if (order != null) return order;
        }

        return null;
    }
}
