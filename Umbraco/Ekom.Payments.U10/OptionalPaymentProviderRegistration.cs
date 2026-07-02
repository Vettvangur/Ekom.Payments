using Microsoft.Extensions.DependencyInjection;
using System.Reflection;

namespace Ekom.Payments.Umb;

static class OptionalPaymentProviderRegistration
{
    public static IServiceCollection AddOptionalPaymentProviderServices(this IServiceCollection services)
    {
        TryAddProvider(
            services,
            "Ekom.Payments.TeyaConsumerloans",
            "Ekom.Payments.TeyaConsumerloans.ApplicationBuilderExtensions",
            "AddTeyaConsumerloans");

        return services;
    }

    static void TryAddProvider(
        IServiceCollection services,
        string assemblyName,
        string extensionTypeName,
        string methodName)
    {
        Assembly assembly;

        try
        {
            assembly = Assembly.Load(assemblyName);
        }
        catch (FileNotFoundException)
        {
            return;
        }
        catch (FileLoadException)
        {
            return;
        }
        catch (BadImageFormatException)
        {
            return;
        }

        var extensionType = assembly.GetType(extensionTypeName, throwOnError: false);
        var method = extensionType?.GetMethod(
            methodName,
            BindingFlags.Public | BindingFlags.Static,
            binder: null,
            types: new[] { typeof(IServiceCollection) },
            modifiers: null);

        if (method == null)
        {
            return;
        }

        try
        {
            method.Invoke(null, new object[] { services });
        }
        catch (TargetInvocationException ex) when (ex.InnerException != null)
        {
            throw new InvalidOperationException($"Failed to register optional payment provider '{assemblyName}'.", ex.InnerException);
        }
    }
}
