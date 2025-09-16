using Ekom.Payments.AltaPay.Model;
using System.Net.Http.Headers;
using System.Text;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using System.Xml.Linq;

namespace Ekom.Payments.AltaPay;

public class AltaPaymentService(IHttpClientFactory httpClientFactory, ILogger<Payment> _logger, AltaPaymentConfig config)
{
    private HttpClient Client { get; init; } = httpClientFactory.CreateClient();

    public async Task<string> CreatePaymentRequestAsync(CreateMerchantPaymentRequest request)
    {
        var client = httpClientFactory.CreateClient();
        client.BaseAddress = config.BaseAddress;
        var byteArray = Encoding.ASCII.GetBytes($"{config.UserName}:{config.Password}");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(byteArray));

        var form = new Dictionary<string, string>
        {
            { "terminal", config.Terminal },         // AltaPay terminal name
            { "shop_orderid", request.OrderId },                // Your internal order id
            { "amount", request.Amount.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) },
            { "currency", request.Currency },
            // Return URLs
            { "config[callback_ok]", request.CallbackOk },
            { "config[callback_fail]", request.CallbackFail },
            { "config[callback_notification]", request.CallbackNotification }
        };
        using var content = new FormUrlEncodedContent(form);
        var response = await client.PostAsync("createPaymentRequest", content);
        response.EnsureSuccessStatusCode();
        var xml = XDocument.Parse(await response.Content.ReadAsStringAsync());
        // Extract the payment URL (redirect the customer here)
        var url = xml.Descendants("Url").FirstOrDefault()?.Value;
        return url;
    }

    public async Task AuthenticateAsync()
    {
        var httpClient = httpClientFactory.CreateClient();
        var credentials = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{config.UserName}:{config.Password}"));
        var authorization = new AuthenticationHeaderValue("Basic", credentials);
        httpClient.DefaultRequestHeaders.Authorization = authorization;

        var responseMessage = await httpClient.PostAsync(config.AuthenticationUrl, null);
        var responseContent = await responseMessage.Content.ReadFromJsonAsync<AuthenticationResponse>();

        Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", responseContent?.Token);
    }

    public async Task<Session> CreateSession()
    {
        var responseMessage = await Client.PostAsync(config.SessionUrl, null);
        var session = await responseMessage.Content.ReadFromJsonAsync<Session>();
        return session ?? throw new InvalidOperationException("Session response was null");
    }

    public async Task<bool> UpdateSession(Session session)
    {
        var responseMessage = await Client.PutAsJsonAsync($"{config.SessionUrl}/{session.SessionId}", session);
        return responseMessage.IsSuccessStatusCode;
    }

    private async Task<PaymentMethod> GetPaymentMethod(string sessionId)
    {
        var responseMessage = await Client.GetAsync($"{config.SessionUrl}/{sessionId}/payment-methods");
        var methodsList = await responseMessage.Content.ReadFromJsonAsync<PaymentMethodResponse>();

        var cardPaymentMethod = methodsList?.Methods.FirstOrDefault(pm => pm.Type == "CARD");
        return cardPaymentMethod ?? throw new InvalidOperationException("PaymentMethodResponse response was null");
    }

    public async Task<PaymentResponse> PaymentAsync(string sessionId)
    {
        var paymentMethod = await GetPaymentMethod(sessionId);

        var paymentRequest = new CreatePaymentRequest
        {
            SessionId = sessionId,
            PaymentMethodId = paymentMethod.Id,
        };

        var responseMessage = await Client.PostAsJsonAsync(paymentMethod.OnInitiatePayment.Value, paymentRequest);
        try
        {
            responseMessage.EnsureSuccessStatusCode();
        }
        catch (Exception ex)
        {
            var errorResponseContent = await responseMessage.Content.ReadAsStringAsync();
            _logger.LogError(ex, "Alta Payment Request Error Content", errorResponseContent);
            throw;
        }

        var responseContent = await responseMessage.Content.ReadFromJsonAsync<PaymentResponse>();
        return responseContent ?? throw new InvalidOperationException("PaymentResponse response was null");
    }
}
