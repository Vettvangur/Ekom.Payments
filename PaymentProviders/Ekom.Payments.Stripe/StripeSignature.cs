using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Ekom.Payments.Stripe;

/// <summary>
/// Verifies the <c>Stripe-Signature</c> header of a webhook.
/// See https://docs.stripe.com/webhooks#verify-manually
/// </summary>
static class StripeSignature
{
    /// <summary>Stripe's own libraries accept a webhook for five minutes after it was signed.</summary>
    public static readonly TimeSpan DefaultTolerance = TimeSpan.FromMinutes(5);

    /// <summary>
    /// True when the header holds a signature of the payload made with the secret, no older than the tolerance.
    /// </summary>
    /// <param name="payload">The request body exactly as it was received.</param>
    /// <param name="header">The Stripe-Signature header: <c>t=timestamp,v1=signature[,v1=signature...]</c>.</param>
    /// <param name="secret">The webhook endpoint's signing secret.</param>
    /// <param name="now">The current time.</param>
    /// <param name="tolerance">How old a signature may be. Five minutes when empty.</param>
    public static bool IsValid(string payload, string? header, string? secret, DateTimeOffset now, TimeSpan? tolerance = null)
    {
        if (string.IsNullOrEmpty(header) || string.IsNullOrWhiteSpace(secret))
        {
            return false;
        }

        string? timestamp = null;
        var signatures = new List<string>();

        foreach (var entry in header.Split(','))
        {
            var separator = entry.IndexOf('=');
            if (separator <= 0) continue;

            var key = entry[..separator].Trim();
            var value = entry[(separator + 1)..].Trim();

            // Only v1 signatures count. Stripe adds other entries for debugging and a fake v0 in sandboxes.
            if (key == "t") timestamp = value;
            else if (key == "v1") signatures.Add(value);
        }

        if (timestamp == null
            || signatures.Count == 0
            || !long.TryParse(timestamp, NumberStyles.None, CultureInfo.InvariantCulture, out var signedAt))
        {
            return false;
        }

        var expected = Compute(payload, timestamp, secret);

        // Several signatures are sent while a rolled secret is still active: one has to match.
        var matches = false;
        foreach (var signature in signatures)
        {
            matches |= CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(signature.ToLowerInvariant()),
                Encoding.UTF8.GetBytes(expected));
        }

        if (!matches)
        {
            return false;
        }

        var age = now.ToUnixTimeSeconds() - signedAt;

        return Math.Abs(age) <= (long)(tolerance ?? DefaultTolerance).TotalSeconds;
    }

    /// <summary>HMAC SHA256 of "timestamp.payload" with the signing secret as key, in lowercase hex.</summary>
    internal static string Compute(string payload, string timestamp, string secret)
    {
        var hash = HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(secret.Trim()),
            Encoding.UTF8.GetBytes(timestamp + "." + payload));

        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
