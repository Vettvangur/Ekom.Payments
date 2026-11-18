using Ekom.Models.Payments;
using Ekom.Payments.Helpers;
using Ekom.Payments.Providers;
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
    /// Only returns the payment on successful verification. <br />
    /// Verifies the signature as <see cref="ValitorResponseController"/> does: SHA-256 of the verification code
    /// followed by the reference number. The verification code is <paramref name="verificationcode"/> when given,
    /// otherwise the one stored with the payment when it was started.
    /// </summary>
    public async Task<OrderPayment?> Verify(
        Response valitorResp, 
        string? verificationcode = null)
    {
        if (string.IsNullOrEmpty(valitorResp?.DigitalSignatureResponse)
        || !Guid.TryParse(valitorResp.ReferenceNumber, out var paymentId))
        {
            _logger.LogError("Valitor Payment Response - Missing signature or reference, unable to verify");
            return null;
        }

        var payment = await _payments.GetAsync(paymentId);
        if (payment == null || !PaymentRowGuard.BelongsTo(payment, Payment._ppNodeName))
        {
            _logger.LogWarning("Valitor Payment Response - Unable to find Valitor payment. PaymentId: {PaymentId}", paymentId);
            return null;
        }

        verificationcode ??= payment.GetProviderData<ValitorSettings>()?.VerificationCode;

        if (string.IsNullOrWhiteSpace(verificationcode))
        {
            _logger.LogError(
                "Valitor Payment Response - Missing verification code, unable to verify. " + PaymentLog.Template,
                payment.OrderNumber, payment.OrderUniqueId, payment.PaymentId);
            return null;
        }

        var digitalSignature = CryptoHelpers.GetSHA256HexStringSum(verificationcode + valitorResp.ReferenceNumber);

        return PaymentRowGuard.SignatureEquals(valitorResp.DigitalSignatureResponse, digitalSignature)
            ? payment
            : null;
    }
}
