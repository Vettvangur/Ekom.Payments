
using System.Security.Cryptography;
using System.Text;

namespace Ekom.Payments.Straumur;

public class StraumurResponseHelper
{

    public static string GetHmacSignature(string hmacKey, Response message)
    {
        var values = new string?[] {
            hmacKey,
            message.CheckoutReference,
            message.PayfacReference,
            message.MerchantReference,
            message.Amount,
            message.Currency,
            message.Reason,
            message.Success };

        var payload = string.Join(':', values.Select(x => x ?? string.Empty));
        var binaryPayload = ConvertToByteArray(payload, Encoding.UTF8);
        var binaryKey = ConvertHexToByteArray(hmacKey);
        var hash = ComputeSha256Hash(binaryPayload, binaryKey);
        return Convert.ToBase64String(hash);
    }

    static byte[] ConvertToByteArray(string str, Encoding encoding)
    {
        return encoding.GetBytes(str);
    }

    static byte[] ConvertHexToByteArray(string hex)
    {
        if ((hex.Length % 2) == 1)
        {
            hex += '0';
        }

        var bytes = new byte[hex.Length / 2];
        for (var i = 0; i < hex.Length; i += 2)
        {
            bytes[i / 2] = Convert.ToByte(hex.Substring(i, 2), 16);
        }

        return bytes;
    }

    static byte[] ComputeSha256Hash(byte[] payload, byte[] key)
    {
        using HMACSHA256 hmacSha256Hash = new(key);
        return hmacSha256Hash.ComputeHash(payload);
    }
}
