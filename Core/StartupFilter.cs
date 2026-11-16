using Ekom.Payments.Helpers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;

namespace Ekom.Payments;

class EkomPaymentsStartupFilter : IStartupFilter
{
    readonly ILogger<EkomPaymentsStartupFilter> _logger;
    readonly EnsureTablesExist _ensureTablesExist;

    public EkomPaymentsStartupFilter(EnsureTablesExist ensureTablesExist, ILogger<EkomPaymentsStartupFilter> logger)
    {
        _ensureTablesExist = ensureTablesExist;
        _logger = logger;
    }

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        RegisterOrderRetrievers();

        try
        {
            _ensureTablesExist.Create();
        }
#pragma warning disable CA1031 // The site must still start; payments that need the tables fail with a clear error.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogError(ex, "Failed to create EkomPayments tables");
        }

        next(app);

        // Maps the payment providers' callback controllers.
        app.UseEndpoints(endpoints => endpoints.MapControllers());
    };

    private void RegisterOrderRetrievers()
    {
        var retrieverType = typeof(IOrderRetriever);
        var orderRetrievers = AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(x => TypeHelper.GetConcreteTypesWithInterface(x, retrieverType))
            .ToList();

        _logger.LogDebug("Found {OrderRetrieversCount} payment order retrievers", orderRetrievers.Count);

        foreach (var retriever in orderRetrievers)
        {
            if (!PaymentOrderRetriever.orderRetrievers.Contains(retriever))
            {
                PaymentOrderRetriever.orderRetrievers.Add(retriever);
            }
        }
    }
}
