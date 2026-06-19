using Microsoft.Extensions.DependencyInjection;
using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.DependencyInjection;

namespace Ekom.Payments.TeyaConsumerloans;

/// <summary>
/// Umbraco auto-discovers this from any loaded assembly.
/// Registers the polling background service with no changes required in the host project.
/// </summary>
public class TeyaConsumerloansComposer : IComposer
{
    public void Compose(IUmbracoBuilder builder)
    {
        builder.Services.AddSingleton<TeyaConsumerloansPollingService>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<TeyaConsumerloansPollingService>());
    }
}
