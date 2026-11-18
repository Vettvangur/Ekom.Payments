using System.Globalization;

namespace Ekom.Payments.PayPal;

/// <summary>
/// PayPal takes amounts as decimal strings: two decimals, none for the currencies that have none.
/// See https://developer.paypal.com/api/rest/reference/currency-codes/
/// </summary>
static class PayPalAmount
{
    static readonly HashSet<string> _noDecimals = new(StringComparer.OrdinalIgnoreCase)
    {
        "HUF", "JPY", "TWD",
    };

    /// <summary>The amount as PayPal expects it, e.g. "19.90" for EUR and "500" for JPY.</summary>
    public static string Format(decimal amount, string currency)
        => Round(amount, currency).ToString(_noDecimals.Contains(currency) ? "0" : "0.00", CultureInfo.InvariantCulture);

    /// <summary>The amount in the precision PayPal charges the currency in.</summary>
    public static decimal Round(decimal amount, string currency)
        => Math.Round(amount, _noDecimals.Contains(currency) ? 0 : 2, MidpointRounding.AwayFromZero);

    /// <summary>An amount PayPal reports. Null when it is not a number.</summary>
    public static decimal? Parse(string? value)
        => decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var amount) ? amount : null;
}
