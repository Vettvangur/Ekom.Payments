using Ekom.Models.Payments;
using Ekom.Payments.Helpers;
using Ekom.Services;
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
    readonly IOrderPayments _payments;

    /// <summary>
    /// ctor
    /// </summary>
    public ValitorResponseHelper(
        ILogger<ValitorResponseHelper> logger,
        IOrderPayments payments,
        IConfiguration configuration)
    {
        _logger = logger;
        _payments = payments;
        _configuration = configuration;
    }

    /// <summary>
    /// Gets Order
    /// Only returns <see cref="OrderStatus"/> on successful verification
    /// </summary>
    public async Task<OrderPayment?> GetOrder(string reference)
    {
        if (!string.IsNullOrEmpty(reference)
        && Guid.TryParse(reference, out var guid))
        {
            return await _payments.GetAsync(guid);
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
    public async Task<OrderPayment?> Verify(
        Response valitorResp, 
        string verificationcode = null)
    {
        verificationcode ??= _configuration["Ekom:Payments:Valitor:VerificationCode"];

        if (string.IsNullOrWhiteSpace(verificationcode)
        || string.IsNullOrEmpty(valitorResp?.DigitalSignatureResponse))
        {
            _logger.LogError("Valitor Payment Response - Missing verification code or signature, unable to verify");
            return null;
        }

        string DigitalSignature = CryptoHelpers.GetMD5StringSum(verificationcode + valitorResp.ReferenceNumber);

        if (valitorResp.DigitalSignatureResponse.Equals(DigitalSignature, StringComparison.InvariantCultureIgnoreCase))
        {
            if (Guid.TryParse(valitorResp.ReferenceNumber, out var guid))
            {
                return await _payments.GetAsync(guid);
            }
        }

        return null;
    }
}
