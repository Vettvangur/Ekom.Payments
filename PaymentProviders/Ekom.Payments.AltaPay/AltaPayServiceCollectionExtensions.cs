using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Net.Http.Headers;
using System.Text;

namespace Ekom.Payments.AltaPay;

public static class AltaPayServiceCollectionExtensions
{
    /// <summary>
    /// Registers AltaPay services and HttpClientFactory support.
    /// Expects settings under "Ekom:Payments:altapay" in IConfiguration.
    /// </summary>
    public static IServiceCollection AddAltaPay(this IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection("Ekom:Payments:altapay");
        services.AddHttpClient("AltaPay", http =>
        {
            var baseUrl = section["BaseAddress"];
            if (!string.IsNullOrWhiteSpace(baseUrl))
                http.BaseAddress = new Uri(baseUrl);

            var user = section["ApiUserName"];
            var pass = section["ApiPassword"];
            if (!string.IsNullOrEmpty(user) && !string.IsNullOrEmpty(pass))
            {
                var token = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{user}:{pass}"));
                http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", token);
            }

            var hostHeader = section["HostOverride"];
            if (!string.IsNullOrWhiteSpace(hostHeader))
            {
                http.DefaultRequestHeaders.Host = hostHeader.Trim();
                http.DefaultRequestHeaders.TryAddWithoutValidation("X-Forwarded-Host", hostHeader.Trim());
            }
        });

        services.AddTransient<AltaService>();
        services.AddSingleton(provider => section);
        return services;
    }
}
