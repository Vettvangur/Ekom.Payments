using Ekom.Payments.Helpers;
using Ekom.Payments.TeyaConsumerloans.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Ekom.Payments.TeyaConsumerloans;

/// <summary>
/// Initiates a Teya/Borgun Consumer Loans application and redirects the customer to the loan portal.
/// </summary>
public class Payment : IPaymentProvider
{
    internal const string _ppNodeName = "teyaConsumerloans";

    readonly ILogger<Payment> _logger;
    readonly IUmbracoService _uService;
    readonly IOrderService _orderService;
    readonly HttpContext _httpCtx;
    readonly IHttpClientFactory _httpClientFactory;
    readonly IConfiguration _config;
    readonly TeyaConsumerloansPollingService _pollingService;

    public Payment(
        ILogger<Payment> logger,
        PaymentsConfiguration settings,
        IUmbracoService uService,
        IOrderService orderService,
        IHttpContextAccessor httpContext,
        IHttpClientFactory httpClientFactory,
        IConfiguration config,
        TeyaConsumerloansPollingService pollingService)
    {
        _logger = logger;
        _uService = uService;
        _orderService = orderService;
        _httpCtx = httpContext.HttpContext ?? throw new NotSupportedException("Payment requests require an httpcontext");
        _httpClientFactory = httpClientFactory;
        _config = config;
        _pollingService = pollingService;
    }

    public async Task<string> RequestAsync(PaymentSettings paymentSettings)
    {
        ArgumentNullException.ThrowIfNull(paymentSettings);
        ArgumentNullException.ThrowIfNull(paymentSettings.Orders);
        ArgumentException.ThrowIfNullOrEmpty(paymentSettings.Language);

        try
        {
            _logger.LogInformation("Teya Consumer Loans Payment Request - Start");

            var teyaSettings = paymentSettings.CustomSettings.ContainsKey(typeof(TeyaConsumerloansSettings))
                ? paymentSettings.CustomSettings[typeof(TeyaConsumerloansSettings)] as TeyaConsumerloansSettings
                : new TeyaConsumerloansSettings();
            teyaSettings ??= new TeyaConsumerloansSettings();

            _uService.PopulatePaymentProviderProperties(
                paymentSettings,
                _ppNodeName,
                teyaSettings,
                TeyaConsumerloansSettings.Properties);

            ValidateLoanSettings(teyaSettings);

            var total = paymentSettings.Orders.Sum(x => x.GrandTotal);

            var orderStatus = await _orderService.InsertAsync(
                total,
                paymentSettings,
                teyaSettings,
                paymentSettings.OrderUniqueId.ToString(),
                _httpCtx
            ).ConfigureAwait(false);

            paymentSettings.SuccessUrl = PaymentsUriHelper.EnsureFullUri(paymentSettings.SuccessUrl, _httpCtx.Request);
            paymentSettings.SuccessUrl = PaymentsUriHelper.AddQueryString(paymentSettings.SuccessUrl, "?reference=" + orderStatus.UniqueId);

            paymentSettings.CancelUrl = PaymentsUriHelper.EnsureFullUri(paymentSettings.CancelUrl, _httpCtx.Request);

            var request = new TokenRequest
            {
                SocialSecurityNumber = paymentSettings.CustomerInfo?.NationalRegistryId,
                Email = paymentSettings.CustomerInfo?.Email,
                PhoneNumber = paymentSettings.CustomerInfo?.PhoneNumber,
                ProgressValidMinutes = teyaSettings.ProgressValidMinutes!.Value,
                TokenValidMinutes = teyaSettings.TokenValidMinutes!.Value,
                LoanInformation = new OnlineLoan
                {
                    MerchantNumber = teyaSettings.MerchantId!,
                    LoanTypeId = teyaSettings.LoanTypeId!.Value,
                    Amount = total,
                    Description = CreateDescription(paymentSettings),
                    NumberOfPayments = teyaSettings.NumberOfPayments!.Value,
                    FlexibleNumberOfPayments = teyaSettings.FlexibleNumberOfPayments!.Value,
                    SuccessUrl = paymentSettings.SuccessUrl.ToString(),
                    CancelUrl = paymentSettings.CancelUrl.ToString(),
                },
            };

            var client = new TeyaConsumerloansClient(_httpClientFactory, _logger);
            var token = await client.CreateWebTokenAsync(teyaSettings, request).ConfigureAwait(false);
            var redirectUrl = new Uri(teyaSettings.LoanPortalUrl!, token);

            orderStatus.EkomPaymentSettings = paymentSettings;
            orderStatus.CustomData = token;
            await _orderService.UpdateAsync(orderStatus).ConfigureAwait(false);

            _logger.LogInformation("Teya Consumer Loans Payment Request - Amount: {Total} OrderId: {OrderId}", total, orderStatus.UniqueId);

            _pollingService.Enqueue(orderStatus, teyaSettings);

            var cspNonce = CspHelper.GetCspNonce(_httpCtx, _config);
            return FormHelper.Redirect(redirectUrl.ToString(), cspNonce);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Teya Consumer Loans Payment Request - Payment Request Failed. OrderId: {OrderId} OrderNumber: {OrderNumber}", paymentSettings.OrderUniqueId, paymentSettings.OrderNumber);
            await Events.OnErrorAsync(this, new ErrorEventArgs
            {
                Exception = ex,
            }).ConfigureAwait(false);
            throw;
        }
    }

    static void ValidateLoanSettings(TeyaConsumerloansSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings.ApiBaseUrl);
        ArgumentException.ThrowIfNullOrEmpty(settings.Username);
        ArgumentException.ThrowIfNullOrEmpty(settings.Password);
        ArgumentException.ThrowIfNullOrEmpty(settings.MerchantId);
        ArgumentNullException.ThrowIfNull(settings.LoanTypeId);
        ArgumentNullException.ThrowIfNull(settings.NumberOfPayments);
        ArgumentNullException.ThrowIfNull(settings.FlexibleNumberOfPayments);
        ArgumentException.ThrowIfNullOrEmpty(settings.MerchantId);
        ArgumentNullException.ThrowIfNull(settings.ProgressValidMinutes);
        ArgumentNullException.ThrowIfNull(settings.TokenValidMinutes);

        if (settings.LoanTypeId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(settings.LoanTypeId), settings.LoanTypeId, "Teya Consumer Loans loanTypeId must be configured.");
        }

        if (settings.NumberOfPayments <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(settings.NumberOfPayments), settings.NumberOfPayments, "Teya Consumer Loans numberOfPayments must be configured.");
        }
    }

    static string CreateDescription(PaymentSettings paymentSettings)
    {
        if (!string.IsNullOrWhiteSpace(paymentSettings.OrderName))
        {
            return paymentSettings.OrderName;
        }

        var firstTitle = paymentSettings.Orders.FirstOrDefault()?.Title;
        if (!string.IsNullOrWhiteSpace(firstTitle))
        {
            return firstTitle;
        }

        return !string.IsNullOrWhiteSpace(paymentSettings.OrderNumber)
            ? paymentSettings.OrderNumber
            : paymentSettings.OrderUniqueId.ToString();
    }
}
