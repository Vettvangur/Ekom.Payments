using Ekom.Payments.PayTrail.Models;
using Microsoft.Extensions.Logging;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Ekom.Payments.PayTrail;

public class PayTrailService
{
    static readonly JsonSerializerOptions JsonSerializerOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    readonly IHttpClientFactory _httpClientFactory;
    readonly ILogger _logger;

    public PayTrailService(IHttpClientFactory httpClientFactory, ILogger logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<CreatePaymentResponse> CreatePaymentAsync(PayTrailSettings settings, CreatePaymentRequest request)
    {
        var body = JsonSerializer.Serialize(request, JsonSerializerOptions);
        var headers = PayTrailHmacHelper.CreateHeaders(settings, HttpMethod.Post.Method);
        headers["signature"] = PayTrailHmacHelper.CalculateHmac(settings.SecretKey, headers, body, settings.Algorithm);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, new Uri(settings.ApiBaseUrl, "/payments"))
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

        foreach (var header in headers)
        {
            httpRequest.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        var httpClient = _httpClientFactory.CreateClient();
        using var response = await httpClient.SendAsync(httpRequest).ConfigureAwait(false);
        var responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("PayTrail create payment failed. Status: {StatusCode} Body: {ResponseBody}", response.StatusCode, responseBody);
            response.EnsureSuccessStatusCode();
        }

        var createPaymentResponse = JsonSerializer.Deserialize<CreatePaymentResponse>(responseBody, JsonSerializerOptions);

        if (createPaymentResponse?.Href == null || string.IsNullOrEmpty(createPaymentResponse.TransactionId))
        {
            throw new InvalidOperationException("PayTrail create payment response did not contain transaction id and href.");
        }

        return createPaymentResponse;
    }
}
