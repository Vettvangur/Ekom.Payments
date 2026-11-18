namespace Ekom.Payments.Straumur;

/// <summary>
/// Straumur takes and reports amounts in minor units with two decimals.
/// ISK has no fractions, but Straumur still takes it with two decimals that must be 00.
/// </summary>
static class StraumurAmount
{
    // Have no fractions, but Straumur takes them with two decimals that must be 00.
    static readonly HashSet<string> _wholeUnitsWithTwoDecimals = new(StringComparer.OrdinalIgnoreCase)
    {
        "ISK",
    };

    /// <summary>The amount as Straumur expects it, e.g. 1990 ISK is 199000 and 19.99 EUR is 1999.</summary>
    public static int ToMinorUnits(decimal amount, string? currency)
    {
        var decimals = currency != null && _wholeUnitsWithTwoDecimals.Contains(currency) ? 0 : 2;

        return checked((int)(Math.Round(amount, decimals, MidpointRounding.AwayFromZero) * 100));
    }

    /// <summary>An amount Straumur reports in minor units, in the currency's own unit.</summary>
    public static decimal FromMinorUnits(decimal minorUnits) => minorUnits / 100m;
}
