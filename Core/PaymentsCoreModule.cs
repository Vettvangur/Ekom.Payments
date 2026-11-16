using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Ekom.Payments;

/// <summary>
/// Registers the services shared by the payment providers. Ekom runs this at startup,
/// so a site only installs the provider packages it wants.
/// </summary>
public sealed class PaymentsCoreModule : IPaymentModule
{
    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IStartupFilter, EkomPaymentsStartupFilter>();
        services.AddSingleton<IDatabaseFactory, DatabaseFactory>();
        services.AddTransient<EnsureTablesExist>();
        services.AddTransient<IOrderService, OrderService>();
        services.AddTransient<PaymentOrderRetriever>();

        var cmsSection = configuration.GetSection("Umbraco:CMS");
        var smtpSection = cmsSection.GetSection("Global:Smtp");

        services.AddTransient<IMailService>(
            sp => new MailService(
                sp.GetRequiredService<ILogger<MailService>>(),
                smtpSection["Host"],
                smtpSection["Port"],
                smtpSection["Username"],
                smtpSection["Password"],
                smtpSection["From"],
                cmsSection["Content:Notifications:Email"]));
    }
}
