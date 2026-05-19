using Ekom.Payments.TeyaConsumerloans.Models;
using Microsoft.Extensions.Logging;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Ekom.Payments.TeyaConsumerloans;

internal class TeyaConsumerloansClient
{
    static readonly JsonSerializerOptions JsonSerializerOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    readonly IHttpClientFactory _httpClientFactory;
    readonly ILogger _logger;

    public TeyaConsumerloansClient(IHttpClientFactory httpClientFactory, ILogger logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<LoanTokenResponse> CreateWebTokenAsync(TeyaConsumerloansSettings settings, LoanApplicationRequest request)
    {
        var httpClient = _httpClientFactory.CreateClient();
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, new Uri(settings.ApiBaseUrl, "online/token/web"))
        {
            Content = JsonContent.Create(request, options: JsonSerializerOptions),
        };

        if (!string.IsNullOrWhiteSpace(settings.ApiKey))
        {
            httpRequest.Headers.TryAddWithoutValidation("X-API-Key", settings.ApiKey);
        }

        if (!string.IsNullOrWhiteSpace(settings.Username) || !string.IsNullOrWhiteSpace(settings.Password))
        {
            var rawCredentials = $"{settings.Username}:{settings.Password}";
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(rawCredentials)));
        }

        using var response = await httpClient.SendAsync(httpRequest).ConfigureAwait(false);
        var responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Teya Consumer Loans token request failed. Status: {StatusCode} Body: {ResponseBody}", response.StatusCode, responseBody);
            response.EnsureSuccessStatusCode();
        }

        var tokenResponse = JsonSerializer.Deserialize<LoanTokenResponse>(responseBody, JsonSerializerOptions);
        if (tokenResponse == null || string.IsNullOrWhiteSpace(tokenResponse.Token))
        {
            throw new InvalidOperationException("Teya Consumer Loans token response did not contain a token.");
        }

        return tokenResponse;
    }
}
