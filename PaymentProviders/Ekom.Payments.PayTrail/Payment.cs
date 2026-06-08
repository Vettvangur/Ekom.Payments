using Ekom.Payments.Helpers;
using Ekom.Payments.PayTrail.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Globalization;

namespace Ekom.Payments.PayTrail;

/// <summary>
/// Initiate a payment request with PayTrail
/// </summary>
public class Payment : IPaymentProvider
{
    internal const string _ppNodeName = "payTrail";
    const string reportPath = "/ekom/payments/paytrailresponse";

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
            _logger.LogInformation("PayTrail Payment Request - Start");

            var payTrailSettings = paymentSettings.CustomSettings.ContainsKey(typeof(PayTrailSettings))
                ? paymentSettings.CustomSettings[typeof(PayTrailSettings)] as PayTrailSettings
                : new PayTrailSettings();
            payTrailSettings ??= new PayTrailSettings();

            _uService.PopulatePaymentProviderProperties(
                paymentSettings,
                _ppNodeName,
                payTrailSettings,
                PayTrailSettings.Properties);

            ArgumentException.ThrowIfNullOrEmpty(payTrailSettings.AccountId);
            ArgumentException.ThrowIfNullOrEmpty(payTrailSettings.SecretKey);
            ArgumentNullException.ThrowIfNull(payTrailSettings.ApiBaseUrl);

            var orderLines = paymentSettings.Orders.ToList();
            var total = orderLines.Sum(x => x.GrandTotal);
            decimal amountMinorUnits = ToMinorUnits(total, paymentSettings.Currency);

            var orderStatus = await _orderService.InsertAsync(
                total,
                paymentSettings,
                payTrailSettings,
                null,
                _httpCtx
            ).ConfigureAwait(false);

            var reportUrl = paymentSettings.ReportUrl == null
                ? PaymentsUriHelper.EnsureFullUri(new Uri(reportPath, UriKind.Relative), _httpCtx.Request)
                : PaymentsUriHelper.EnsureFullUri(paymentSettings.ReportUrl, _httpCtx.Request);
            var callbackUrl = PaymentsUriHelper.AddQueryString(reportUrl, "?callback=true");

            var createPaymentRequest = new CreatePaymentRequest
            {
                Stamp = orderStatus.UniqueId.ToString(),
                Reference = !string.IsNullOrEmpty(paymentSettings.OrderNumber) ? paymentSettings.OrderNumber : orderStatus.ReferenceId.ToString(CultureInfo.InvariantCulture),
                Amount = amountMinorUnits,
                Currency = paymentSettings.Currency,
                Language = ParseSupportedLanguage(paymentSettings.Language),
                Items = CreatePaymentItems(orderLines, paymentSettings.Currency, amountMinorUnits),
                Customer = CreateCustomer(paymentSettings.CustomerInfo),
                RedirectUrls = new PaymentUrls
                {
                    Success = reportUrl.ToString(),
                    Cancel = reportUrl.ToString(),
                },
                CallbackUrls = new PaymentUrls
                {
                    Success = callbackUrl.ToString(),
                    Cancel = callbackUrl.ToString(),
                },
            };

            LogPaymentItemAmountMismatch(paymentSettings, createPaymentRequest);

            var svc = new PayTrailService(_httpClientFactory, _logger);
            var response = await svc.CreatePaymentAsync(payTrailSettings, createPaymentRequest).ConfigureAwait(false);

            orderStatus.CustomData = response.TransactionId;
            await _orderService.UpdateAsync(orderStatus).ConfigureAwait(false);

            _logger.LogInformation("PayTrail Payment Request - Amount: {Total} OrderId: {OrderId} TransactionId: {TransactionId}", total, orderStatus.UniqueId, response.TransactionId);

            var cspNonce = CspHelper.GetCspNonce(_httpCtx, _config);
            return FormHelper.CreateRequest(new Dictionary<string, string?>(), response.Href!.ToString(), "GET", cspNonce: cspNonce);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PayTrail Payment Request - Payment Request Failed. OrderId: {OrderId} OrderNumber: {OrderNumber}", paymentSettings.OrderUniqueId, paymentSettings.OrderNumber);
            await Events.OnErrorAsync(this, new ErrorEventArgs
            {
                Exception = ex,
            });
            throw;
        }
    }

    internal static int ToMinorUnits(decimal amount, string currency)
    {
        var multiplier = IsZeroDecimalCurrency(currency) ? 1 : 100;
        return Convert.ToInt32(Math.Round(amount * multiplier, 0, MidpointRounding.AwayFromZero));
    }

    internal static bool IsZeroDecimalCurrency(string currency)
    {
        return currency.Equals("ISK", StringComparison.InvariantCultureIgnoreCase)
            || currency.Equals("JPY", StringComparison.InvariantCultureIgnoreCase)
            || currency.Equals("KRW", StringComparison.InvariantCultureIgnoreCase);
    }

    internal static decimal CalculateVatPercentage(OrderItem lineItem)
    {
        if (lineItem.VAT <= 0)
        {
            return 0;
        }

        var netAmount = lineItem.GrandTotal - lineItem.VAT;

        if (netAmount <= 0)
        {
            return 0;
        }

        return Math.Round(lineItem.VAT / netAmount * 100, 1, MidpointRounding.AwayFromZero);
    }

    internal static List<PaymentItem> CreatePaymentItems(IReadOnlyList<OrderItem> orderLines, string currency, decimal amountMinorUnits)
    {
        var items = orderLines
            .Select((lineItem, index) => CreatePaymentItem(lineItem, index, currency))
            .ToList();

        AdjustLastPaymentItemForTotalRounding(items, amountMinorUnits);

        return items;
    }

    static PaymentItem CreatePaymentItem(OrderItem lineItem, int index, string currency)
    {
        var lineTotalMinorUnits = ToMinorUnits(lineItem.GrandTotal, currency);
        var units = 1m;
        var unitPriceMinorUnits = lineTotalMinorUnits;

        if (lineItem.Quantity > 0)
        {
            var calculatedUnitPriceMinorUnits = lineTotalMinorUnits / lineItem.Quantity;

            if (calculatedUnitPriceMinorUnits == Math.Truncate(calculatedUnitPriceMinorUnits))
            {
                units = lineItem.Quantity;
                unitPriceMinorUnits = Convert.ToInt32(calculatedUnitPriceMinorUnits);
            }
        }

        return new PaymentItem
        {
            UnitPrice = unitPriceMinorUnits,
            Units = units,
            VatPercentage = CalculateVatPercentage(lineItem),
            ProductCode = index.ToString(CultureInfo.InvariantCulture),
            Description = lineItem.Title,
        };
    }

    static void AdjustLastPaymentItemForTotalRounding(List<PaymentItem> items, decimal amountMinorUnits)
    {
        if (items.Count == 0)
        {
            return;
        }

        var itemsTotalMinorUnits = CalculateItemsTotalMinorUnits(items);
        var differenceMinorUnits = amountMinorUnits - itemsTotalMinorUnits;

        if (differenceMinorUnits == 0)
        {
            return;
        }

        var lastItem = items[^1];
        var adjustedLineTotalMinorUnits = lastItem.UnitPrice * lastItem.Units + differenceMinorUnits;

        lastItem.UnitPrice = Convert.ToInt32(adjustedLineTotalMinorUnits);
        lastItem.Units = 1;
    }

    static decimal CalculateItemsTotalMinorUnits(IEnumerable<PaymentItem> items)
    {
        return items.Sum(x => x.UnitPrice * x.Units);
    }

    static PaymentCustomer? CreateCustomer(CustomerInfo customerInfo)
    {
        if (customerInfo == null)
        {
            return null;
        }

        var names = (customerInfo.Name ?? string.Empty).Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);

        return new PaymentCustomer
        {
            Email = customerInfo.Email,
            FirstName = names.FirstOrDefault(),
            LastName = names.Length > 1 ? names[1] : null,
            Phone = customerInfo.PhoneNumber,
        };
    }

    static string ParseSupportedLanguage(string language)
    {
        var parsed = CultureInfo.GetCultureInfo(language).TwoLetterISOLanguageName.ToUpperInvariant();

        return parsed switch
        {
            "FI" => "FI",
            "SV" => "SV",
            "EN" => "EN",
            _ => "EN",
        };
    }

    void LogPaymentItemAmountMismatch(PaymentSettings paymentSettings, CreatePaymentRequest request)
    {
        var items = request.Items.ToList();
        var requestAmountMinorUnits = request.Amount;
        var itemsTotalMinorUnits = CalculateItemsTotalMinorUnits(items);
        var differenceMinorUnits = requestAmountMinorUnits - itemsTotalMinorUnits;

        if (differenceMinorUnits == 0)
        {
            return;
        }

        var orderLines = paymentSettings.Orders?.ToList() ?? [];

        _logger.LogWarning(
            "PayTrail Payment Request - Amount mismatch before sending. Amount: {Amount} ItemsTotal: {ItemsTotal} Difference: {Difference} Currency: {Currency} OrderId: {OrderId} OrderNumber: {OrderNumber} OrderLines: {@OrderLines} PayTrailItems: {@PayTrailItems}",
            requestAmountMinorUnits,
            itemsTotalMinorUnits,
            differenceMinorUnits,
            paymentSettings.Currency,
            paymentSettings.OrderUniqueId,
            paymentSettings.OrderNumber,
            orderLines.Select((lineItem, index) => new
            {
                Index = index,
                lineItem.Title,
                lineItem.Price,
                lineItem.Quantity,
                lineItem.GrandTotal,
                lineItem.VAT,
                lineItem.Discount,
                ExpectedLineTotal = ToMinorUnits(lineItem.GrandTotal, paymentSettings.Currency),
                SentUnitPrice = index < items.Count ? items[index].UnitPrice : 0,
                SentLineTotal = index < items.Count ? items[index].UnitPrice * items[index].Units : 0,
            }).ToList(),
            items.Select((item, index) => new
            {
                Index = index,
                item.Description,
                item.ProductCode,
                item.UnitPrice,
                item.Units,
                LineTotal = item.UnitPrice * item.Units,
                item.VatPercentage,
            }).ToList());
    }
}
