using Ekom.Payments.AspNetCore;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Ekom.Payments.Umb;

static class ApplicationBuilderExtensions
{
    public static IServiceCollection AddEkomPayments(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IStartupFilter, EkomPaymentsStartupFilter>();

        services.AddAspNetCoreEkomPayments(configuration);

        services.AddTransient<IUmbracoService, UmbracoService>();

        services.AddHttpClient("straumur", client =>
        {
            client.DefaultRequestHeaders.Add("X-API-key", configuration["Ekom:Payments:straumur:apikey"]);
        });

        return services;
    }
}
