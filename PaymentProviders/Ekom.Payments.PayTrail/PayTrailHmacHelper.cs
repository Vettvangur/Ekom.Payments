using Ekom.Payments.Providers;
using System.Security.Cryptography;
using System.Text;

namespace Ekom.Payments.PayTrail;

public static class PayTrailHmacHelper
{
    /// <summary>
    /// The HMAC algorithm used when none is configured.
    /// </summary>
    public const string DefaultAlgorithm = "sha256";

    /// <summary>
    /// True for the HMAC algorithms PayTrail supports: sha256 and sha512.
    /// </summary>
    public static bool IsSupportedAlgorithm(string? algorithm)
        => string.Equals(algorithm, "sha256", StringComparison.OrdinalIgnoreCase)
        || string.Equals(algorithm, "sha512", StringComparison.OrdinalIgnoreCase);

    public static IDictionary<string, string> CreateHeaders(PayTrailSettings settings, string method)
    {
        var headers = new Dictionary<string, string>
        {
            ["checkout-account"] = settings.AccountId,
            ["checkout-algorithm"] = settings.Algorithm,
            ["checkout-method"] = method,
            ["checkout-nonce"] = Guid.NewGuid().ToString(),
            ["checkout-timestamp"] = DateTime.UtcNow.ToString("O"),
        };

        if (!string.IsNullOrWhiteSpace(settings.PlatformName))
        {
            headers["platform-name"] = settings.PlatformName;
        }

        return headers;
    }

    public static string CalculateHmac(string secret, IEnumerable<KeyValuePair<string, string>> parameters, string body = "", string algorithm = DefaultAlgorithm)
    {
        if (string.IsNullOrWhiteSpace(algorithm))
        {
            algorithm = DefaultAlgorithm;
        }

        if (!IsSupportedAlgorithm(algorithm))
        {
            throw new ArgumentException($"PayTrail HMAC algorithm \"{algorithm}\" is not supported. Use sha256 or sha512.", nameof(algorithm));
        }

        var payload = string.Join(
            "\n",
            parameters
                .Where(x => x.Key.StartsWith("checkout-", StringComparison.InvariantCultureIgnoreCase))
                .OrderBy(x => x.Key, StringComparer.Ordinal)
                .Select(x => $"{x.Key.ToLowerInvariant()}:{x.Value}")
                .Concat([body]));

        var secretBytes = Encoding.UTF8.GetBytes(secret);
        var payloadBytes = Encoding.UTF8.GetBytes(payload);

        if (algorithm.Equals("sha512", StringComparison.InvariantCultureIgnoreCase))
        {
            using var hmac = new HMACSHA512(secretBytes);
            return Convert.ToHexString(hmac.ComputeHash(payloadBytes)).ToLowerInvariant();
        }

        using var hmacSha256 = new HMACSHA256(secretBytes);
        return Convert.ToHexString(hmacSha256.ComputeHash(payloadBytes)).ToLowerInvariant();
    }

    public static bool IsValidSignature(string secret, IEnumerable<KeyValuePair<string, string>> parameters, string signature, string body = "")
    {
        var algorithm = parameters.FirstOrDefault(x => x.Key.Equals("checkout-algorithm", StringComparison.InvariantCultureIgnoreCase)).Value ?? DefaultAlgorithm;

        // The algorithm comes from the request itself: only the ones PayTrail supports are accepted.
        if (!IsSupportedAlgorithm(algorithm))
        {
            return false;
        }

        var hmac = CalculateHmac(secret, parameters, body, algorithm);

        return PaymentRowGuard.SignatureEquals(signature, hmac);
    }
}
