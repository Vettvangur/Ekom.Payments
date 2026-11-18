namespace Ekom.Payments.Stripe;

/// <summary>
/// Stripe takes amounts as whole numbers of a currency's minor unit.
/// See https://docs.stripe.com/currencies
/// </summary>
static class StripeAmount
{
    static readonly HashSet<string> _zeroDecimal = new(StringComparer.OrdinalIgnoreCase)
    {
        "BIF", "CLP", "DJF", "GNF", "JPY", "KMF", "KRW", "MGA", "PYG", "RWF", "VND", "VUV", "XAF", "XOF", "XPF",
    };

    // Have no fractions, but Stripe still takes them with two decimals that must be 00.
    static readonly HashSet<string> _wholeUnitsWithTwoDecimals = new(StringComparer.OrdinalIgnoreCase)
    {
        "ISK", "UGX",
    };

    // Stripe takes three decimals of which the last must be 0.
    static readonly HashSet<string> _threeDecimal = new(StringComparer.OrdinalIgnoreCase)
    {
        "BHD", "JOD", "KWD", "OMR", "TND",
    };

    /// <summary>The amount as Stripe expects it, e.g. 1990 ISK is 199000 and 19.90 EUR is 1990.</summary>
    public static long ToMinorUnits(decimal amount, string currency)
    {
        if (_zeroDecimal.Contains(currency))
            return (long)Round(amount, 0);
        if (_wholeUnitsWithTwoDecimals.Contains(currency))
            return (long)Round(amount, 0) * 100;
        if (_threeDecimal.Contains(currency))
            return (long)(Round(amount, 2) * 1000);

        return (long)(Round(amount, 2) * 100);
    }

    /// <summary>An amount Stripe reports, in the currency's own unit.</summary>
    public static decimal FromMinorUnits(long minorUnits, string currency)
    {
        if (_zeroDecimal.Contains(currency))
            return minorUnits;
        if (_threeDecimal.Contains(currency))
            return minorUnits / 1000m;

        return minorUnits / 100m;
    }

    static decimal Round(decimal amount, int decimals)
        => Math.Round(amount, decimals, MidpointRounding.AwayFromZero);
}
