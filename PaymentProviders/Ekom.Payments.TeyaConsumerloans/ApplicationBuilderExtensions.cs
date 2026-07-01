using Microsoft.Extensions.DependencyInjection;

namespace Ekom.Payments.TeyaConsumerloans;

/// <summary>
/// Registers Teya Consumer Loans services.
/// </summary>
public static class ApplicationBuilderExtensions
{
    public static IServiceCollection AddTeyaConsumerloans(this IServiceCollection services)
    {
        services.AddSingleton<TeyaConsumerloansPollingService>();
        services.AddHostedService(sp => sp.GetRequiredService<TeyaConsumerloansPollingService>());

        return services;
    }
}
