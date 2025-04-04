using Ekom.Payments.Helpers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Ekom.Payments.Valitor;

/// <summary>
/// An alternative to subscribing to the valitor callback event.
/// This helper can be invoked in the view or controller that receives the redirect from Valitor.
/// Only returns <see cref="OrderStatus"/> on successful verification
/// </summary>
public class ValitorResponseHelper
{
    readonly ILogger<ValitorResponseHelper> _logger;
    readonly IConfiguration _configuration;
    readonly IOrderService _orderSvc;

    /// <summary>
    /// ctor
    /// </summary>
    public ValitorResponseHelper(
        ILogger<ValitorResponseHelper> logger,
        IOrderService orderSvc,
        IConfiguration configuration)
    {
        _logger = logger;
        _orderSvc = orderSvc;
        _configuration = configuration;
    }

    /// <summary>
    /// Gets Order
    /// Only returns <see cref="OrderStatus"/> on successful verification
    /// </summary>
    public async Task<OrderStatus?> GetOrder(string reference)
    {
        if (!string.IsNullOrEmpty(reference)
        && Guid.TryParse(reference, out var guid))
        {
            return await _orderSvc.GetAsync(guid);
        }
        else
        {
            return null;
        }
    }

    /// <summary>
    /// An alternative to subscribing to the valitor callback event.
    /// This helper can be invoked in the view or controller that receives the redirect from Valitor.
    /// Only returns <see cref="OrderStatus"/> on successful verification
    /// </summary>
    public async Task<OrderStatus?> Verify(
        Response valitorResp, 
        string verificationcode = null)
    {
        verificationcode ??= _configuration["Ekom:Payments:Valitor:VerificationCode"];
        string DigitalSignature = CryptoHelpers.GetMD5StringSum(verificationcode + valitorResp.ReferenceNumber);

        if (valitorResp.DigitalSignatureResponse.Equals(DigitalSignature, StringComparison.InvariantCultureIgnoreCase))
        {
            if (Guid.TryParse(valitorResp.ReferenceNumber, out var guid))
            {
                return await _orderSvc.GetAsync(guid);
            }
        }

        return null;
    }
}
