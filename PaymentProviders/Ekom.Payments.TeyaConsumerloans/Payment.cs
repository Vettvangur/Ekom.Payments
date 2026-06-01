using Ekom.Payments.Helpers;
using Ekom.Payments.TeyaConsumerloans.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Globalization;

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

    public Payment(
        ILogger<Payment> logger,
        PaymentsConfiguration settings,
        IUmbracoService uService,
        IOrderService orderService,
        IHttpContextAccessor httpContext,
        IHttpClientFactory httpClientFactory,
        IConfiguration config)
    {
        _logger = logger;
        _uService = uService;
        _orderService = orderService;
        _httpCtx = httpContext.HttpContext ?? throw new NotSupportedException("Payment requests require an httpcontext");
        _httpClientFactory = httpClientFactory;
        _config = config;
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

            ArgumentNullException.ThrowIfNull(teyaSettings.ApiBaseUrl);
            ArgumentNullException.ThrowIfNull(teyaSettings.LoanPortalUrl);

            if (string.IsNullOrWhiteSpace(teyaSettings.Username) && string.IsNullOrWhiteSpace(teyaSettings.ApiKey))
            {
                throw new ArgumentException("Either Username/Password or ApiKey must be configured for Teya Consumer Loans.");
            }

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

            var cancelUrl = PaymentsUriHelper.EnsureFullUri(paymentSettings.CancelUrl, _httpCtx.Request);

            //var tokenRequest = new TokenRequest
            //{
            //    SocialSecurityNumber = paymentSettings.CustomerInfo.NationalRegistryId,
            //    Email = paymentSettings.CustomerInfo.Email,
            //    PhoneNumber = paymentSettings.CustomerInfo.PhoneNumber,
            //    ProgressValidMinutes = 5,
            //    TokenValidMinutes = 2,
            //    LoanInformation =
            //    {
            //        MerchantNumber = teyaSettings.MerchantId,
            //        LoanTypeId
            //    }
            //};

            var request = new LoanApplicationRequest
            {
                Reference = !string.IsNullOrEmpty(paymentSettings.OrderNumber)
                    ? paymentSettings.OrderNumber
                    : orderStatus.UniqueId.ToString(),
                Amount = total,
                Currency = paymentSettings.Currency,
                Language = ParseSupportedLanguage(paymentSettings.Language),
                SuccessUrl = paymentSettings.SuccessUrl.ToString(),
                CancelUrl = cancelUrl.ToString(),
                MerchantId = teyaSettings.MerchantId,
                StoreId = teyaSettings.StoreId,
                ProductCode = teyaSettings.ProductCode,
                Customer = CreateCustomer(paymentSettings.CustomerInfo),
                Items = paymentSettings.Orders.Select(x => new LoanApplicationItem
                {
                    Description = x.Title,
                    Quantity = x.Quantity,
                    UnitPrice = x.Price,
                    Amount = x.GrandTotal,
                }).ToList(),
            };

            var client = new TeyaConsumerloansClient(_httpClientFactory, _logger);
            var tokenResponse = await client.CreateWebTokenAsync(teyaSettings, request).ConfigureAwait(false);
            var redirectUrl = CreateLoanPortalUrl(teyaSettings, tokenResponse.Token);

            orderStatus.CustomData = tokenResponse.Token;
            await _orderService.UpdateAsync(orderStatus).ConfigureAwait(false);

            _logger.LogInformation("Teya Consumer Loans Payment Request - Amount: {Total} OrderId: {OrderId}", total, orderStatus.UniqueId);

            await Events.OnSuccessAsync(this, new SuccessEventArgs
            {
                OrderStatus = orderStatus,
            }).ConfigureAwait(false);

            var cspNonce = CspHelper.GetCspNonce(_httpCtx, _config);
            return FormHelper.CreateRequest(new Dictionary<string, string?>(), redirectUrl.ToString(), "GET", cspNonce: cspNonce);
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

    static Uri CreateLoanPortalUrl(TeyaConsumerloansSettings settings, string token)
    {
        var parameterName = string.IsNullOrWhiteSpace(settings.LoanPortalTokenQueryParameter)
            ? "token"
            : settings.LoanPortalTokenQueryParameter;

        var separator = string.IsNullOrEmpty(settings.LoanPortalUrl.Query) ? "?" : "&";
        return new Uri(settings.LoanPortalUrl + separator + Uri.EscapeDataString(parameterName) + "=" + Uri.EscapeDataString(token));
    }

    static LoanCustomer? CreateCustomer(CustomerInfo? customerInfo)
    {
        if (customerInfo == null)
        {
            return null;
        }

        return new LoanCustomer
        {
            Name = customerInfo.Name,
            Email = customerInfo.Email,
            PhoneNumber = customerInfo.PhoneNumber,
            NationalRegistryId = customerInfo.NationalRegistryId,
            Address = customerInfo.Address,
            City = customerInfo.City,
            PostalCode = customerInfo.PostalCode,
        };
    }

    static string ParseSupportedLanguage(string language)
    {
        var parsed = CultureInfo.GetCultureInfo(language).TwoLetterISOLanguageName.ToUpperInvariant();
        return parsed switch
        {
            "IS" => "is",
            "EN" => "en",
            _ => "is",
        };
    }
}
