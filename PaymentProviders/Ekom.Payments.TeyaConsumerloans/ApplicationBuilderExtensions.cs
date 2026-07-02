using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Ekom.Payments.TeyaConsumerloans;

/// <summary>
/// Registers Teya Consumer Loans services.
/// </summary>
public static class ApplicationBuilderExtensions
{
    public static IServiceCollection AddTeyaConsumerloans(this IServiceCollection services)
    {
        services.TryAddSingleton<TeyaConsumerloansPollingService>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, TeyaConsumerloansHostedService>());

        return services;
    }
}

class TeyaConsumerloansHostedService : IHostedService
{
    readonly TeyaConsumerloansPollingService _pollingService;

    public TeyaConsumerloansHostedService(TeyaConsumerloansPollingService pollingService)
    {
        _pollingService = pollingService;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        return _pollingService.StartAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return _pollingService.StopAsync(cancellationToken);
    }
}
