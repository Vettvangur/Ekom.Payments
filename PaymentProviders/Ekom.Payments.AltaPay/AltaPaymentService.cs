using Ekom.Payments.AltaPay.Model;
using System.Net.Http.Headers;
using System.Text;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging;

namespace Ekom.Payments.AltaPay;

public class AltaPaymentService(IHttpClientFactory httpClientFactory, ILogger<Payment> _logger, AltaSettings altaSettings)
{
    private HttpClient Client { get; init; } = httpClientFactory.CreateClient();

    public async Task AuthenticateAsync()
    {
        var httpClient = httpClientFactory.CreateClient();
        var credentials = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{altaSettings.ApiUserName}:{altaSettings.ApiPassword}"));
        var authorization = new AuthenticationHeaderValue("Basic", credentials);
        httpClient.DefaultRequestHeaders.Authorization = authorization;

        var responseMessage = await httpClient.PostAsync(altaSettings.AuthenticationUrl, null);
        var responseContent = await responseMessage.Content.ReadFromJsonAsync<AuthenticationResponse>();

        Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", responseContent?.Token);
    }

    public async Task<Session> CreateSession(SessionRequest request)
    {
        var responseMessage = await Client.PostAsync(altaSettings.SessionUrl, null);
        var responseContent = await responseMessage.Content.ReadFromJsonAsync<Session>();
        return responseContent ?? throw new InvalidOperationException("Session response was null");
    }

    public async Task<Session> UpdateSession(Session session)
    {
        var responseMessage = await Client.PutAsJsonAsync($"{altaSettings.SessionUrl}/{session.SessionId}", session);
        var responseContent = await responseMessage.Content.ReadFromJsonAsync<Session>();
        return responseContent ?? throw new InvalidOperationException("Session response was null");
    }

    private async Task<PaymentMethod> GetPaymentMethod(string sessionId)
    {
        var responseMessage = await Client.GetAsync($"{altaSettings.SessionUrl}/{sessionId}/payment-methods");
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
