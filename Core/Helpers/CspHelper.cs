using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

namespace Ekom.Payments.Helpers;

public static class CspHelper
{
    /// <summary>
    /// Returns the CSP nonce from HttpContext.Items.
    /// Falls back to "CspNonce" if configuration key is missing or empty.
    /// Returns null if nonce is not present or empty.
    /// Never throws.
    /// </summary>
    public static string? GetCspNonce(HttpContext? httpCtx, IConfiguration? config)
    {
        if (httpCtx == null)
            return null;

        var key =
            string.IsNullOrWhiteSpace(config?["Ekom:CspKey"])
                ? "CspNonce"
                : config!["Ekom:CspKey"]!.Trim();

        if (!httpCtx.Items.TryGetValue(key, out var value))
            return null;

        var nonce = value?.ToString();

        return string.IsNullOrWhiteSpace(nonce)
            ? null
            : nonce;
    }
}
