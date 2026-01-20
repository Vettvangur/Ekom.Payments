using System.Text;
using XSystem.Security.Cryptography;

namespace Ekom.Payments.SiminnPay;

public static class SiminnPayResponseHelper
{
    public static string CalculateChecksum(ChecksumCalculationRequest request)
    {
        var inputData = request.ToDictionary();
        var data = new List<string>();
        foreach (KeyValuePair<string, string> item in inputData.OrderBy(pair => pair.Key).ToList())
        {
            data.Add(item.Key + "=" + item.Value);
        }
        using var md5 = new MD5CryptoServiceProvider();
        byte[] hashbytes = md5.ComputeHash(Encoding.UTF8.GetBytes(string.Join(",", data).ToArray()));
        string hashstring = "";
        for (int i = 0; i < hashbytes.Length; i++)
        {
            hashstring += hashbytes[i].ToString("x2");
        }
        return hashstring;
    }
}

public class ChecksumCalculationRequest
{
    public string Amount { get; set; }
    public string Currency { get; set; }
    public string OrderId { get; set; }
    public string Secret { get; set; }

    public Dictionary<string, string> ToDictionary() => new Dictionary<string, string>
    {
        { "amount", Amount },
        { "currency", Currency },
        { "shop_orderid", OrderId },
        { "secret", Secret }
    };
}
