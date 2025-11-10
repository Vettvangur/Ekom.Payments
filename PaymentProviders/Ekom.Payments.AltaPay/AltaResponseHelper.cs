using System.Security.Cryptography;
using System.Text;

namespace Ekom.Payments.AltaPay;

public static class AltaResponseHelper
{
    public static string CalculateChecksum(ChecksumCalculationRequest request)
    {
        // Build "key=value" pairs sorted by key, joined with commas
        var dict = request.ToDictionary();

        var sb = new StringBuilder(capacity: 256);
        foreach (var kvp in dict.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            if (sb.Length > 0) sb.Append(',');
            sb.Append(kvp.Key).Append('=').Append(kvp.Value ?? string.Empty);
        }

        // MD5 over UTF-8 bytes
        using var md5 = MD5.Create();
        var hash = md5.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));

        // Lowercase hex (matches your previous behavior)
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

public class ChecksumCalculationRequest
{
    public required string Amount { get; set; }
    public required string Currency { get; set; }
    public required string OrderId { get; set; }
    public required string Secret { get; set; }

    public Dictionary<string, string> ToDictionary() => new Dictionary<string, string>
    {
        { "amount", Amount },
        { "currency", Currency },
        { "shop_orderid", OrderId },
        { "secret", Secret }
    };
}
