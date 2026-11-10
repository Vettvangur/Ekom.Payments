using Ekom.Payments.AltaPay;
using Ekom.Payments.AltaPay.Model;
using Ekom.Payments.Borgun;
using Ekom.Payments.Helpers;
using Ekom.Payments.Netgiro;
using Ekom.Payments.Straumur;
using Ekom.Payments.Valitor;
using LinqToDB;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;
using System.Net;
using System.Net.Mail;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;
using BorgunPayment = Ekom.Payments.Borgun.Payment;
using BorgunResponse = Ekom.Payments.Borgun.Response;
using NetgiroPayment = Ekom.Payments.Netgiro.Payment;
using NetgiroResponse = Ekom.Payments.Netgiro.Response;
using StraumurResponse = Ekom.Payments.Straumur.Response;
using ValitorPayment = Ekom.Payments.Valitor.Payment;
using ValitorResponse = Ekom.Payments.Valitor.Response;

namespace Ekom.Payments.Tests;

/// <summary>
/// Payment callbacks are only accepted when they carry the signature the payment provider documents
/// for a confirmed payment of the order they reference. All secrets are made up.
/// </summary>
public sealed class CallbackVerificationTests
{
    const string NetgiroSecret = "netgiro-test-secret";
    const string BorgunSecret = "borgun-test-secret";
    const string ValitorCode = "valitor-test-code";
    const string StraumurKey = "a1b2c3d4e5f60718293a4b5c6d7e8f90";
    const string AltaSecret = "alta-test-secret";
    static readonly Guid NetgiroApplicationId = Guid.Parse("2f1f6c1e-7c4b-4d0c-9a57-3b8d6e241905");

    #region Netgiro

    [Fact]
    public async Task NetgiroAcceptsConfirmationSignedWithAmountAndStatus()
    {
        await using var f = await Fixture.CreateAsync();
        var payment = await f.RequestNetgiroAsync(1990);

        var response = NetgiroConfirmation(payment.Order.UniqueId, "1990", status: "1");

        Assert.IsType<OkResult>(await f.Netgiro().Post(response));
        Assert.True((await f.Orders.GetAsync(payment.Order.UniqueId))!.Paid);
        Assert.Equal(1, f.Completions);
    }

    [Fact]
    public async Task NetgiroAcceptsConfirmationCodeSignatureWhenNetgiroSignatureIsNotSent()
    {
        await using var f = await Fixture.CreateAsync();
        var payment = await f.RequestNetgiroAsync(1990);

        var response = new NetgiroResponse
        {
            ReferenceNumber = payment.Order.UniqueId,
            ConfirmationCode = "6f9619ff-8b86-d011-b42d-00c04fc964ff",
            InvoiceNumber = "1234",
        };
        response.Signature = Sha256Hex(NetgiroSecret + payment.Order.UniqueId + response.ConfirmationCode + response.InvoiceNumber);

        Assert.IsType<OkResult>(await f.Netgiro().Post(response));
        Assert.True((await f.Orders.GetAsync(payment.Order.UniqueId))!.Paid);
        Assert.Equal(1, f.Completions);
    }

    [Fact]
    public async Task NetgiroRepeatedConfirmationCompletesTheOrderOnce()
    {
        await using var f = await Fixture.CreateAsync();
        var payment = await f.RequestNetgiroAsync(1990);

        Assert.IsType<OkResult>(await f.Netgiro().Post(NetgiroConfirmation(payment.Order.UniqueId, "1990", status: "1")));
        Assert.IsType<OkResult>(await f.Netgiro().Post(NetgiroConfirmation(payment.Order.UniqueId, "1990", status: "2")));

        Assert.Equal(1, f.Completions);
    }

    [Theory]
    // The values a payment request is signed with, presented as a payment confirmation
    [InlineData("1990", "2f1f6c1e-7c4b-4d0c-9a57-3b8d6e241905")]
    [InlineData("19902f1f6c1e-7c4b-4d0c-9a57-3b8d6e", "241905")]
    public async Task NetgiroRejectsThePaymentRequestSignatureAsConfirmation(string confirmationCode, string invoiceNumber)
    {
        await using var f = await Fixture.CreateAsync();
        var payment = await f.RequestNetgiroAsync(1990);

        var response = new NetgiroResponse
        {
            ReferenceNumber = payment.Order.UniqueId,
            ConfirmationCode = confirmationCode,
            InvoiceNumber = invoiceNumber,
            Signature = payment.Form["Signature"],
        };

        Assert.IsType<BadRequestResult>(await f.Netgiro().Post(response));
        Assert.False((await f.Orders.GetAsync(payment.Order.UniqueId))!.Paid);
        Assert.Equal(0, f.Completions);
    }

    [Fact]
    public async Task NetgiroRejectsThePaymentRequestSignatureAsNetgiroSignature()
    {
        await using var f = await Fixture.CreateAsync();
        var payment = await f.RequestNetgiroAsync(5);

        // TransactionId + InvoiceNumber + TotalAmount + Status spells out TotalAmount + ApplicationId
        var response = new NetgiroResponse
        {
            ReferenceNumber = payment.Order.UniqueId,
            TransactionId = "52f1f6c1e-7c4b-4d0c-9a57-3b8d6e241",
            InvoiceNumber = "90",
            TotalAmount = "5",
            Status = "5",
            NetgiroSignature = payment.Form["Signature"],
        };

        Assert.IsType<BadRequestResult>(await f.Netgiro().Post(response));
        Assert.False((await f.Orders.GetAsync(payment.Order.UniqueId))!.Paid);
    }

    [Theory]
    [InlineData("1", "1")]
    [InlineData("1990", "5")]
    [InlineData("1990", "")]
    public async Task NetgiroRejectsConfirmationForAnotherAmountOrACanceledPayment(string totalAmount, string status)
    {
        await using var f = await Fixture.CreateAsync();
        var payment = await f.RequestNetgiroAsync(1990);

        // Correctly signed, but not a payment of this order
        var response = NetgiroConfirmation(payment.Order.UniqueId, totalAmount, status);

        Assert.IsType<BadRequestResult>(await f.Netgiro().Post(response));
        Assert.False((await f.Orders.GetAsync(payment.Order.UniqueId))!.Paid);
    }

    [Fact]
    public async Task NetgiroRejectsUnsignedConfirmation()
    {
        await using var f = await Fixture.CreateAsync();
        var payment = await f.RequestNetgiroAsync(1990);

        var response = NetgiroConfirmation(payment.Order.UniqueId, "1990", status: "1");
        response.NetgiroSignature = Sha256Hex("guess");

        Assert.IsType<BadRequestResult>(await f.Netgiro().Post(response));
        Assert.False((await f.Orders.GetAsync(payment.Order.UniqueId))!.Paid);
    }

    [Fact]
    public async Task NetgiroDoesNotConfirmPaymentsOfOtherProviders()
    {
        await using var f = await Fixture.CreateAsync();
        var valitor = await f.RequestValitorAsync(1990);

        // The payment has no Netgiro secret, anyone can compute a signature without one
        var response = new NetgiroResponse
        {
            ReferenceNumber = valitor.Order.UniqueId,
            ConfirmationCode = "6f9619ff-8b86-d011-b42d-00c04fc964ff",
            InvoiceNumber = "1234",
        };
        response.Signature = Sha256Hex(valitor.Order.UniqueId + response.ConfirmationCode + response.InvoiceNumber);

        Assert.IsType<NotFoundResult>(await f.Netgiro().Post(response));
        Assert.False((await f.Orders.GetAsync(valitor.Order.UniqueId))!.Paid);
        Assert.Equal(0, f.Completions);
    }

    [Fact]
    public async Task NetgiroDoesNotConfirmPaymentsWithoutASecret()
    {
        await using var f = await Fixture.CreateAsync();
        // A payment of another provider created before providers were recorded on payments
        var order = await f.InsertAsync(1990, new ValitorSettings { MerchantId = "1", VerificationCode = ValitorCode }, provider: null);

        var response = new NetgiroResponse
        {
            ReferenceNumber = order.UniqueId,
            ConfirmationCode = "6f9619ff-8b86-d011-b42d-00c04fc964ff",
            InvoiceNumber = "1234",
        };
        response.Signature = Sha256Hex(order.UniqueId + response.ConfirmationCode + response.InvoiceNumber);

        Assert.IsType<BadRequestResult>(await f.Netgiro().Post(response));
        Assert.False((await f.Orders.GetAsync(order.UniqueId))!.Paid);
    }

    [Fact]
    public async Task NetgiroStillConfirmsPaymentsCreatedBeforeProvidersWereRecorded()
    {
        await using var f = await Fixture.CreateAsync();
        var order = await f.InsertAsync(
            1990,
            new NetgiroSettings { ApplicationId = NetgiroApplicationId, Secret = NetgiroSecret },
            provider: null);

        Assert.IsType<OkResult>(await f.Netgiro().Post(NetgiroConfirmation(order.UniqueId, "1990", status: "1")));
        Assert.True((await f.Orders.GetAsync(order.UniqueId))!.Paid);
    }

    [Fact]
    public async Task NetgiroBindsJsonConfirmationWithNumericValues()
    {
        var reference = Guid.NewGuid();
        var http = new DefaultHttpContext();
        http.Request.ContentType = "application/json";
        http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(
            $$"""{"referenceNumber":"{{reference}}","transactionid":"t-1","invoiceNumber":1234,"totalAmount":1990,"status":1,"netgiroSignature":"abc","signature":"def"}"""));

        var context = new DefaultModelBindingContext
        {
            ActionContext = new ActionContext { HttpContext = http },
            ModelState = new ModelStateDictionary(),
            ModelName = "netgiroResponse",
        };
        await new NetgiroResponseModelBinder().BindModelAsync(context);

        var response = Assert.IsType<NetgiroResponse>(context.Result.Model);
        Assert.Equal(reference, response.ReferenceNumber);
        Assert.Equal("t-1", response.TransactionId);
        Assert.Equal("1234", response.InvoiceNumber);
        Assert.Equal("1990", response.TotalAmount);
        Assert.Equal("1", response.Status);
        Assert.Equal("abc", response.NetgiroSignature);
        Assert.Equal("def", response.Signature);
    }

    static NetgiroResponse NetgiroConfirmation(Guid reference, string totalAmount, string status)
    {
        var response = new NetgiroResponse
        {
            ReferenceNumber = reference,
            TransactionId = "6f9619ff-8b86-d011-b42d-00c04fc964ff",
            InvoiceNumber = "1234",
            TotalAmount = totalAmount,
            Status = status,
        };
        // SHA256(SecretKey + ReferenceNumber + TransactionId + InvoiceNumber + TotalAmount + Status)
        response.NetgiroSignature = Sha256Hex(
            NetgiroSecret + reference + response.TransactionId + response.InvoiceNumber + totalAmount + status);

        return response;
    }

    #endregion

    #region Borgun

    [Fact]
    public async Task BorgunAcceptsOrderHashForThePaymentsOrderId()
    {
        await using var f = await Fixture.CreateAsync();
        var payment = await f.RequestBorgunAsync(1990);

        var response = BorgunConfirmation(payment.Order.UniqueId, payment.Form["orderid"], payment.Form["amount"]);

        Assert.Equal("1990,00", payment.Form["amount"]);
        Assert.IsType<OkResult>(await f.Borgun().Post(response));
        Assert.True((await f.Orders.GetAsync(payment.Order.UniqueId))!.Paid);
        Assert.Equal(1, f.Completions);
    }

    [Fact]
    public async Task BorgunRejectsThePaymentRequestHashAsOrderHash()
    {
        await using var f = await Fixture.CreateAsync();
        var payment = await f.RequestBorgunAsync(1990);

        // The payment request is signed for merchantid|returnurlsuccess|returnurlsuccessserver|orderid|amount|currency
        var response = new BorgunResponse
        {
            Reference = payment.Order.UniqueId.ToString(),
            Status = "OK",
            OrderHash = payment.Form["checkhash"],
            OrderId = string.Join('|',
                payment.Form["merchantid"], payment.Form["returnurlsuccess"], payment.Form["returnurlsuccessserver"], payment.Form["orderid"]),
        };

        Assert.IsType<BadRequestResult>(await f.Borgun().Post(response));
        Assert.False((await f.Orders.GetAsync(payment.Order.UniqueId))!.Paid);
        Assert.Equal(0, f.Completions);
    }

    [Fact]
    public async Task BorgunRejectsTheOrderHashOfAnotherPayment()
    {
        await using var f = await Fixture.CreateAsync();
        var paid = await f.RequestBorgunAsync(1990, orderNumber: "IS0001");
        var unpaid = await f.RequestBorgunAsync(1990, orderNumber: "IS0002");

        var response = BorgunConfirmation(unpaid.Order.UniqueId, paid.Form["orderid"], paid.Form["amount"]);

        Assert.IsType<BadRequestResult>(await f.Borgun().Post(response));
        Assert.False((await f.Orders.GetAsync(unpaid.Order.UniqueId))!.Paid);
    }

    [Fact]
    public async Task BorgunDoesNotLogTheExpectedHash()
    {
        await using var f = await Fixture.CreateAsync();
        var payment = await f.RequestBorgunAsync(1990);
        var expected = BorgunConfirmation(payment.Order.UniqueId, payment.Form["orderid"], payment.Form["amount"]).OrderHash;

        var response = BorgunConfirmation(payment.Order.UniqueId, payment.Form["orderid"], payment.Form["amount"]);
        response.OrderHash = new string('0', expected.Length);

        var log = new RecordingLogger<BorgunResponseController>();
        Assert.IsType<BadRequestResult>(await f.Borgun(log).Post(response));
        Assert.NotEmpty(log.Messages);
        Assert.DoesNotContain(log.Messages, x => x.Contains(expected, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task BorgunDoesNotConfirmPaymentsOfOtherProviders()
    {
        await using var f = await Fixture.CreateAsync();
        var netgiro = await f.RequestNetgiroAsync(1990);

        var response = BorgunConfirmation(netgiro.Order.UniqueId, "IS0001", "1990,00");

        Assert.IsType<NotFoundResult>(await f.Borgun().Post(response));
        Assert.False((await f.Orders.GetAsync(netgiro.Order.UniqueId))!.Paid);
    }

    static BorgunResponse BorgunConfirmation(Guid reference, string orderId, string amount) => new()
    {
        Reference = reference.ToString(),
        Status = "OK",
        OrderId = orderId,
        AuthorizationCode = "123456",
        CreditCardNumber = "****1111",
        // HMAC SHA256 of orderid|amount|currency
        OrderHash = HmacHex(Encoding.UTF8.GetBytes(BorgunSecret), $"{orderId}|{amount}|ISK"),
    };

    #endregion

    #region Valitor

    [Fact]
    public async Task ValitorAcceptsSignedConfirmation()
    {
        await using var f = await Fixture.CreateAsync();
        var payment = await f.RequestValitorAsync(1990);

        var response = new ValitorResponse
        {
            ReferenceNumber = payment.Order.UniqueId.ToString(),
            DigitalSignatureResponse = Sha256Hex(ValitorCode + payment.Order.UniqueId),
        };

        Assert.IsType<OkResult>(await f.Valitor().Post(response));
        Assert.True((await f.Orders.GetAsync(payment.Order.UniqueId))!.Paid);
        Assert.Equal(1, f.Completions);
    }

    [Fact]
    public async Task ValitorDoesNotConfirmPaymentsOfOtherProviders()
    {
        await using var f = await Fixture.CreateAsync();
        var netgiro = await f.RequestNetgiroAsync(1990);
        var legacy = await f.InsertAsync(
            1990,
            new NetgiroSettings { ApplicationId = NetgiroApplicationId, Secret = NetgiroSecret },
            provider: null);

        foreach (var order in new[] { netgiro.Order, legacy })
        {
            // Neither payment has a Valitor verification code
            var response = new ValitorResponse
            {
                ReferenceNumber = order.UniqueId.ToString(),
                DigitalSignatureResponse = Sha256Hex(order.UniqueId.ToString()),
            };

            Assert.IsNotType<OkResult>(await f.Valitor().Post(response));
            Assert.False((await f.Orders.GetAsync(order.UniqueId))!.Paid);
        }

        Assert.Equal(0, f.Completions);
    }

    [Fact]
    public async Task ValitorHelperDoesNotVerifyWithoutAVerificationCode()
    {
        await using var f = await Fixture.CreateAsync();
        var payment = await f.RequestValitorAsync(1990);
        var helper = new ValitorResponseHelper(
            NullLogger<ValitorResponseHelper>.Instance, f.Orders, new ConfigurationBuilder().Build());
        var reference = payment.Order.UniqueId.ToString();

        Assert.Null(await helper.Verify(new ValitorResponse
        {
            ReferenceNumber = reference,
            DigitalSignatureResponse = CryptoHelpers.GetMD5StringSum(reference),
        }));
        Assert.NotNull(await helper.Verify(
            new ValitorResponse
            {
                ReferenceNumber = reference,
                DigitalSignatureResponse = CryptoHelpers.GetMD5StringSum(ValitorCode + reference),
            },
            ValitorCode));
    }

    #endregion

    #region Straumur

    [Fact]
    public async Task StraumurAcceptsSignedConfirmation()
    {
        await using var f = await Fixture.CreateAsync();
        var order = await f.InsertAsync(1990, new StraumurSettings { HmacKey = StraumurKey }, "straumur");

        var response = StraumurConfirmation(order.UniqueId, StraumurKey);

        Assert.IsType<OkResult>(await f.Straumur().Post(response));
        Assert.True((await f.Orders.GetAsync(order.UniqueId))!.Paid);
        Assert.Equal(1, f.Completions);
    }

    [Fact]
    public async Task StraumurDoesNotConfirmPaymentsWithoutAHmacKey()
    {
        await using var f = await Fixture.CreateAsync();
        var order = await f.InsertAsync(1990, new StraumurSettings { HmacKey = "" }, "straumur");

        var response = StraumurConfirmation(order.UniqueId, hmacKey: "");

        Assert.IsNotType<OkResult>(await f.Straumur().Post(response));
        Assert.False((await f.Orders.GetAsync(order.UniqueId))!.Paid);
        Assert.Equal(0, f.Completions);
    }

    [Fact]
    public async Task StraumurDoesNotConfirmPaymentsOfOtherProviders()
    {
        await using var f = await Fixture.CreateAsync();
        // Shares no settings with Straumur, apart from the name of the key
        var order = await f.InsertAsync(1990, new { HmacKey = "" }, "otherprovider");

        Assert.IsType<NotFoundResult>(await f.Straumur().Post(StraumurConfirmation(order.UniqueId, hmacKey: "")));
        Assert.False((await f.Orders.GetAsync(order.UniqueId))!.Paid);
    }

    static StraumurResponse StraumurConfirmation(Guid reference, string hmacKey)
    {
        var response = new StraumurResponse
        {
            CheckoutReference = "checkout-1",
            PayfacReference = "payfac-1",
            MerchantReference = reference.ToString(),
            Amount = "199000",
            Currency = "ISK",
            Reason = "383528:1111:03/2030",
            Success = "true",
            AdditionalData = new PaymentEventDetails { CardNumber = "****1111" },
        };
        // HMAC SHA256, hex key, of checkoutReference:payfacReference:merchantReference:amount:currency:reason:success
        response.HmacSignature = Convert.ToBase64String(new HMACSHA256(Convert.FromHexString(hmacKey)).ComputeHash(
            Encoding.UTF8.GetBytes(string.Join(':',
                response.CheckoutReference, response.PayfacReference, response.MerchantReference,
                response.Amount, response.Currency, response.Reason, response.Success))));

        return response;
    }

    #endregion

    #region AltaPay

    [Fact]
    public async Task AltaPayAcceptsChecksummedConfirmationWhenAnotherProviderWasTriedFirst()
    {
        await using var f = await Fixture.CreateAsync();
        var netgiro = await f.RequestNetgiroAsync(1990);
        var alta = await f.InsertAsync(1990, AltaPaySettings(AltaSecret), "altapay");

        var result = await f.AltaPay().Post(AltaPayConfirmation(f.OrderId, AltaChecksum(f.OrderId, "1990.00", AltaSecret)));

        Assert.IsType<RedirectResult>(result);
        Assert.True((await f.Orders.GetAsync(alta.UniqueId))!.Paid);
        Assert.False((await f.Orders.GetAsync(netgiro.Order.UniqueId))!.Paid);
        Assert.Equal(1, f.Completions);
    }

    [Fact]
    public async Task AltaPayRejectsWrongChecksum()
    {
        await using var f = await Fixture.CreateAsync();
        var alta = await f.InsertAsync(1990, AltaPaySettings(AltaSecret), "altapay");

        var result = await f.AltaPay().Post(AltaPayConfirmation(f.OrderId, AltaChecksum(f.OrderId, "1.00", AltaSecret)));

        Assert.IsNotType<RedirectResult>(result);
        Assert.False((await f.Orders.GetAsync(alta.UniqueId))!.Paid);
    }

    [Fact]
    public async Task AltaPayDoesNotConfirmPaymentsOfOtherProviders()
    {
        await using var f = await Fixture.CreateAsync();
        var netgiro = await f.RequestNetgiroAsync(1990);
        var legacy = await f.InsertAsync(
            1990,
            new NetgiroSettings { ApplicationId = NetgiroApplicationId, Secret = NetgiroSecret },
            provider: null);

        // Neither payment has an AltaPay secret to check a checksum with
        var result = await f.AltaPay().Post(AltaPayConfirmation(f.OrderId, checksum: null));

        Assert.IsType<NotFoundResult>(result);
        Assert.False((await f.Orders.GetAsync(netgiro.Order.UniqueId))!.Paid);
        Assert.False((await f.Orders.GetAsync(legacy.UniqueId))!.Paid);
        Assert.Equal(0, f.Completions);
    }

    static AltaSettings AltaPaySettings(string? secret) => new()
    {
        Terminal = "Test Terminal",
        ApiUserName = "api-user",
        ApiPassword = "not-a-secret",
        BaseAddress = new Uri("https://alta.example/merchant/API/"),
        CustomerInformationSharedSecret = secret,
    };

    static PaymentResponse AltaPayConfirmation(Guid shopOrderId, string? checksum) => new()
    {
        ShopOrderId = shopOrderId,
        Checksum = checksum,
        Xml = WebUtility.UrlEncode(
            "<?xml version=\"1.0\"?><APIResponse version=\"20170228\"><Header><ErrorCode>0</ErrorCode></Header><Body>"
            + "<Result>Success</Result><Transactions><Transaction><TransactionId>1</TransactionId>"
            + $"<ShopOrderId>{shopOrderId}</ShopOrderId><MerchantCurrency>352</MerchantCurrency>"
            + "<MerchantCurrencyAlpha>ISK</MerchantCurrencyAlpha><CreditCardMaskedPan>411111******1111</CreditCardMaskedPan>"
            + "</Transaction></Transactions></Body></APIResponse>"),
    };

    /// <summary>
    /// MD5 of the comma separated key=value pairs, sorted by key
    /// </summary>
    static string AltaChecksum(Guid shopOrderId, string amount, string secret)
        => Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(
            $"amount={amount},currency=352,secret={secret},shop_orderid={shopOrderId}"))).ToLowerInvariant();

    #endregion

    #region Stored payment settings

    [Fact]
    public async Task CardDetailsAreNotStoredWithThePayment()
    {
        await using var f = await Fixture.CreateAsync();
        var settings = f.PaymentSettings(1990, orderNumber: "IS0001");
        settings.CardNumber = "4111111111111111";
        settings.CardCVV = "737";
        settings.CardExpirationMonth = 3;
        settings.CardExpirationYear = 2030;
        settings.VirtualCardNumber = "virtual-card-token";

        var order = await f.Orders.InsertAsync(1990, settings, new { }, f.OrderId.ToString(), new DefaultHttpContext());
        var stored = (await f.Orders.GetAsync(order.UniqueId))!;

        Assert.DoesNotContain("4111111111111111", stored.EkomPaymentSettingsData);
        Assert.DoesNotContain("737", stored.EkomPaymentSettingsData);
        Assert.DoesNotContain("Card", string.Join(' ', JObject.Parse(stored.EkomPaymentSettingsData)
            .Properties().Select(x => x.Name).Where(x => x != nameof(PaymentSettings.VirtualCardNumber))));

        // The request still has the card details, everything else is stored as before
        Assert.Equal("4111111111111111", settings.CardNumber);
        var expected = JObject.Parse(Newtonsoft.Json.JsonConvert.SerializeObject(settings));
        foreach (var property in new[] { "CardNumber", "CardCVV", "CardExpirationMonth", "CardExpirationYear" })
        {
            Assert.True(expected.Remove(property));
        }
        Assert.Equal(expected.ToString(Newtonsoft.Json.Formatting.None), stored.EkomPaymentSettingsData);

        var restored = stored.EkomPaymentSettings;
        Assert.Null(restored.CardNumber);
        Assert.Null(restored.CardCVV);
        Assert.Equal("virtual-card-token", restored.VirtualCardNumber);
        Assert.Equal("IS0001", restored.OrderNumber);
        Assert.Equal(f.OrderId.ToString(), restored.OrderCustomData["ekomOrderUniqueId"]);
    }

    #endregion

    static string Sha256Hex(string input)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input))).ToLowerInvariant();

    static string HmacHex(byte[] key, string message)
        => Convert.ToHexString(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(message)));

    sealed record StartedPayment(OrderStatus Order, Dictionary<string, string> Form);

    sealed class Fixture : IAsyncDisposable, IDatabaseFactory, IUmbracoService, IHttpContextAccessor, IMailService
    {
        readonly string _path = Path.Combine(Path.GetTempPath(), $"callback-test-{Guid.NewGuid():N}.sqlite");
        readonly IConfiguration _configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:umbracoDbDSN_ProviderName"] = "Microsoft.Data.Sqlite",
        }).Build();
        readonly PaymentsConfiguration _settings = new() { SendEmailAlerts = false };

        /// <summary>
        /// The order in the shop, shared by all payment attempts made in a test
        /// </summary>
        public Guid OrderId { get; } = Guid.NewGuid();
        public IOrderService Orders { get; private set; } = null!;
        public int Completions { get; private set; }
        public HttpContext? HttpContext { get; set; } = CreateHttpContext();
        public string From { get; set; } = "";
        public string To { get; set; } = "";

        public static Task<Fixture> CreateAsync()
        {
            var f = new Fixture();
            f.Orders = new OrderService(f._settings, f);
            new AspNetCore.EnsureTablesExist(f, f._configuration, NullLogger<AspNetCore.EnsureTablesExist>.Instance).Create();
            Events.SuccessAsync += f.CompleteAsync;
            return Task.FromResult(f);
        }

        public PaymentSettings PaymentSettings(decimal total, string orderNumber = "")
        {
            var settings = new PaymentSettings
            {
                OrderUniqueId = OrderId, OrderNumber = orderNumber, OrderName = "Order",
                Orders = [new OrderItem { Title = "Item", Price = total, GrandTotal = total, Quantity = 1 }],
                Currency = "ISK", Store = "test", Language = "is-IS",
                SuccessUrl = new Uri($"https://shop.example/receipt?orderId={OrderId}"),
                CancelUrl = new Uri("https://shop.example/cancel"),
                ErrorUrl = new Uri("https://shop.example/error"),
            };
            settings.OrderCustomData["ekomOrderUniqueId"] = OrderId.ToString();
            return settings;
        }

        public Task<StartedPayment> RequestNetgiroAsync(decimal total)
        {
            var settings = PaymentSettings(total);
            settings.CustomSettings[typeof(NetgiroSettings)] = new NetgiroSettings
            {
                ApplicationId = NetgiroApplicationId, Secret = NetgiroSecret,
                PaymentPageUrl = new Uri("https://netgiro.example/securepay/"),
            };
            return RequestAsync(settings, new NetgiroPayment(
                NullLogger<NetgiroPayment>.Instance, _settings, this, Orders, this, _configuration));
        }

        public Task<StartedPayment> RequestBorgunAsync(decimal total, string orderNumber = "")
        {
            var settings = PaymentSettings(total, orderNumber);
            settings.CustomSettings[typeof(BorgunSettings)] = new BorgunSettings
            {
                MerchantId = "9275444", SecretCode = BorgunSecret, PaymentGatewayId = 16,
                PaymentPageUrl = new Uri("https://borgun.example/securepay/"),
            };
            return RequestAsync(settings, new BorgunPayment(
                NullLogger<BorgunPayment>.Instance, _settings, this, Orders, this, _configuration));
        }

        public Task<StartedPayment> RequestValitorAsync(decimal total)
        {
            var settings = PaymentSettings(total);
            settings.CustomSettings[typeof(ValitorSettings)] = new ValitorSettings
            {
                MerchantId = "1", VerificationCode = ValitorCode,
                PaymentPageUrl = new Uri("https://valitor.example/"),
            };
            return RequestAsync(settings, new ValitorPayment(
                NullLogger<ValitorPayment>.Instance, _settings, this, Orders, this, _configuration));
        }

        async Task<StartedPayment> RequestAsync(PaymentSettings settings, IPaymentProvider provider)
        {
            var html = await provider.RequestAsync(settings);
            var form = Regex.Matches(html, "<input type=\"hidden\" name=\"([^\"]*)\" value=\"([^\"]*)\">")
                .ToDictionary(x => WebUtility.HtmlDecode(x.Groups[1].Value), x => WebUtility.HtmlDecode(x.Groups[2].Value));

            await using var db = GetDatabase();
            var order = await db.OrderStatus.OrderByDescending(x => x.ReferenceId).FirstAsync();
            return new StartedPayment(order, form);
        }

        /// <summary>
        /// Stores a payment the way a provider does, without contacting the provider
        /// </summary>
        public Task<OrderStatus> InsertAsync(decimal total, object providerSettings, string? provider)
        {
            var stored = JObject.FromObject(providerSettings);
            if (provider != null)
            {
                stored["ekomPaymentProvider"] = provider;
            }
            return Orders.InsertAsync(total, PaymentSettings(total), stored, OrderId.ToString(), new DefaultHttpContext());
        }

        public NetgiroResponseController Netgiro() => Controller(new NetgiroResponseController(
            NullLogger<NetgiroResponseController>.Instance, _settings, Orders, this, this));

        public BorgunResponseController Borgun(ILogger<BorgunResponseController>? logger = null) => Controller(new BorgunResponseController(
            logger ?? NullLogger<BorgunResponseController>.Instance, _settings, Orders, this, this));

        public ValitorResponseController Valitor() => Controller(new ValitorResponseController(
            NullLogger<ValitorResponseController>.Instance, _settings, Orders, this, this));

        public StraumurResponseController Straumur() => Controller(new StraumurResponseController(
            NullLogger<StraumurResponseController>.Instance, _settings, Orders, this, this));

        public AltaResponseController AltaPay() => Controller(new AltaResponseController(
            NullLogger<AltaResponseController>.Instance, _settings, Orders, this, this, this));

        static T Controller<T>(T controller) where T : ControllerBase
        {
            controller.ControllerContext = new ControllerContext { HttpContext = CreateHttpContext() };
            return controller;
        }

        static HttpContext CreateHttpContext()
        {
            var http = new DefaultHttpContext();
            http.Request.Scheme = "https";
            http.Request.Host = new HostString("shop.example");
            return http;
        }

        Task CompleteAsync(object sender, SuccessEventArgs args) { Completions++; return Task.CompletedTask; }

        public void PopulatePaymentProviderProperties(
            PaymentSettings settings, string ppNodeName, object? customProperties, IEnumerable<PropertyInfo>? customPropertyList)
        {
            settings.PaymentProviderName = ppNodeName;
        }

        public Task SendAsync(MailMessage mailMessage) => Task.CompletedTask;
        public DbContext GetDatabase() => new(ProviderName.SQLiteMS, $"Data Source={_path};Pooling=False");
        public SqlConnection GetSqlConnection() => throw new NotSupportedException();

        public ValueTask DisposeAsync()
        {
            Events.SuccessAsync -= CompleteAsync;
            File.Delete(_path);
            return ValueTask.CompletedTask;
        }
    }

    sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, exception));
    }
}
