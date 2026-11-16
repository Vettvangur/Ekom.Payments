using LinqToDB;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Ekom.Payments.Providers;

/// <summary>
/// Shared by the payment provider projects as a linked source file, so each provider package
/// stays independent of the Core version it is deployed with.
/// </summary>
static class PaymentRowGuard
{
    /// <summary>
    /// Name of the property recording which payment provider created a payment row.
    /// </summary>
    internal const string ProviderPropertyName = "ekomPaymentProvider";

    /// <summary>
    /// Returns the provider settings to persist with a payment row, recording which provider created it.
    /// </summary>
    internal static JObject Stamp(object providerSettings, string provider)
    {
        var json = JObject.FromObject(providerSettings);
        json[ProviderPropertyName] = provider;

        return json;
    }

    /// <summary>
    /// The provider recorded on the payment row, null for rows created before providers were recorded.
    /// </summary>
    internal static bool TryGetProvider(OrderStatus order, out string? provider)
    {
        provider = null;

        if (string.IsNullOrWhiteSpace(order.EkomPaymentProviderData))
        {
            return false;
        }

        try
        {
            if (JToken.Parse(order.EkomPaymentProviderData) is not JObject json)
            {
                return false;
            }

            var stamp = json[ProviderPropertyName];
            if (stamp == null || stamp.Type == JTokenType.Null)
            {
                return true;
            }

            if (stamp.Type != JTokenType.String)
            {
                return false;
            }

            provider = stamp.Value<string>();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// False when the payment row was created by another payment provider. <br />
    /// Rows created before providers were recorded carry no provider,
    /// callers must then require their own settings to be present on the row.
    /// </summary>
    internal static bool BelongsTo(OrderStatus order, string provider)
        => TryGetProvider(order, out var rowProvider)
        && (rowProvider == null || string.Equals(rowProvider, provider, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Marks the payment row as paid. <br />
    /// Only returns true for the caller that changed the row, concurrent or repeated callbacks get false.
    /// </summary>
    /// <summary>
    /// A payment recorded on the Ekom order belongs to the provider that started it. A payment copied from
    /// a legacy row that predates provider stamps has no provider: the callback's own secret check decides.
    /// </summary>
    internal static bool BelongsTo(Ekom.Models.Payments.OrderPayment payment, string provider)
        => string.IsNullOrEmpty(payment.ProviderType)
        || string.Equals(payment.ProviderType, provider, StringComparison.OrdinalIgnoreCase);

    internal static async Task<bool> TryMarkPaidAsync(IDatabaseFactory databaseFactory, OrderStatus order)
    {
        await using var db = databaseFactory.GetDatabase();

        var updatedRows = await db.OrderStatus
            .Where(x => x.UniqueId == order.UniqueId && !x.Paid)
            .Set(x => x.Paid, true)
            .UpdateAsync()
            .ConfigureAwait(false);

        if (updatedRows != 1)
        {
            return false;
        }

        order.Paid = true;
        return true;
    }

    /// <summary>
    /// Compares two signatures without leaking how much of them matched.
    /// </summary>
    internal static bool SignatureEquals(string? received, string? expected, bool ignoreCase = true)
    {
        if (string.IsNullOrEmpty(received) || string.IsNullOrEmpty(expected))
        {
            return false;
        }

        if (ignoreCase)
        {
            received = received.ToUpperInvariant();
            expected = expected.ToUpperInvariant();
        }

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(received),
            Encoding.UTF8.GetBytes(expected));
    }
}
