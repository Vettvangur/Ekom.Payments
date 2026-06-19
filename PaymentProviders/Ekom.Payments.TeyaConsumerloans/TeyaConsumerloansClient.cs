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

    public async Task<string> CreateWebTokenAsync(TeyaConsumerloansSettings settings, TokenRequest request)
    {
        using var httpRequest = CreateRequest(settings, HttpMethod.Post, "online/token/web");
        httpRequest.Content = JsonContent.Create(request, options: JsonSerializerOptions);

        using var response = await SendAsync(httpRequest).ConfigureAwait(false);
        var responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        var token = DeserializeStringResponse(responseBody);

        if (string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException("Teya Consumer Loans token response did not contain a token.");
        }

        return token;
    }

    public async Task<ContractInfoCompact> ValidateLoanAsync(TeyaConsumerloansSettings settings, ValidateRequest request)
    {
        using var httpRequest = CreateRequest(settings, HttpMethod.Put, "online/validate");
        httpRequest.Content = JsonContent.Create(request, options: JsonSerializerOptions);

        using var response = await SendAsync(httpRequest).ConfigureAwait(false);
        var contractInfo = await response.Content.ReadFromJsonAsync<ContractInfoCompact>(JsonSerializerOptions).ConfigureAwait(false);

        return contractInfo ?? throw new InvalidOperationException("Teya Consumer Loans validate response did not contain contract information.");
    }

    /// <summary>
    /// Polls <c>GET /online/status</c> and returns the raw status string.
    /// Known values: CREATED, INPROGRESS, PENDING, SUCCESS, FAILED, CANCELED, PROGRESSEXPIRED, TOKENEXPIRED.
    /// </summary>
    public async Task<string> GetStatusAsync(TeyaConsumerloansSettings settings, string token)
    {
        ArgumentException.ThrowIfNullOrEmpty(settings.MerchantId);

        var path = $"online/status?token={Uri.EscapeDataString(token)}&merchantNumber={Uri.EscapeDataString(settings.MerchantId)}";
        using var httpRequest = CreateRequest(settings, HttpMethod.Get, path);

        using var response = await SendAsync(httpRequest).ConfigureAwait(false);
        var responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        return DeserializeStringResponse(responseBody);
    }

    HttpRequestMessage CreateRequest(TeyaConsumerloansSettings settings, HttpMethod method, string relativePath)
    {
        ArgumentNullException.ThrowIfNull(settings.ApiBaseUrl);
        ArgumentException.ThrowIfNullOrEmpty(settings.Username);
        ArgumentException.ThrowIfNullOrEmpty(settings.Password);

        var httpRequest = new HttpRequestMessage(method, new Uri(settings.ApiBaseUrl, relativePath));
        var rawCredentials = $"{settings.Username}:{settings.Password}";
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(rawCredentials)));

        return httpRequest;
    }

    async Task<HttpResponseMessage> SendAsync(HttpRequestMessage httpRequest)
    {
        var httpClient = _httpClientFactory.CreateClient();
        var response = await httpClient.SendAsync(httpRequest).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            _logger.LogError(
                "Teya Consumer Loans request failed. Method: {Method} Url: {Url} Status: {StatusCode} Body: {ResponseBody}",
                httpRequest.Method,
                httpRequest.RequestUri,
                response.StatusCode,
                responseBody);
            response.EnsureSuccessStatusCode();
        }

        return response;
    }

    static string DeserializeStringResponse(string responseBody)
    {
        var trimmed = responseBody.Trim();

        if (trimmed.StartsWith("\"", StringComparison.Ordinal))
        {
            return JsonSerializer.Deserialize<string>(trimmed, JsonSerializerOptions) ?? string.Empty;
        }

        return trimmed;
    }
}
