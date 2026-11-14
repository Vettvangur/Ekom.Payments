using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Ekom.Payments.TeyaConsumerloans;

/// <summary>Registers the Teya Consumer Loans services when the package is installed.</summary>
public sealed class TeyaConsumerloansModule : IPaymentModule
{
    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
        => services.AddTeyaConsumerloans();
}
