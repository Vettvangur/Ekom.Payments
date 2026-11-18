using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Ekom.Payments.PayPal;

/// <summary>
/// The calls the provider makes to PayPal's REST API.
/// See https://developer.paypal.com/docs/api/orders/v2/
/// </summary>
sealed partial class PayPalClient
{
    const string LiveUrl = "https://api-m.paypal.com";
    const string SandboxUrl = "https://api-m.sandbox.paypal.com";

    // PayPal asks callers to reuse an access token until it expires.
    static readonly ConcurrentDictionary<string, (string Token, DateTimeOffset Expires)> _tokens = new();

    readonly IHttpClientFactory _httpClientFactory;
    readonly PayPalSettings _settings;

    public PayPalClient(IHttpClientFactory httpClientFactory, PayPalSettings settings)
    {
        ArgumentException.ThrowIfNullOrEmpty(settings.ClientId);
        ArgumentException.ThrowIfNullOrEmpty(settings.ClientSecret);

        _httpClientFactory = httpClientFactory;
        _settings = settings;
    }

    /// <summary>PayPal's order ids are short and alphanumeric. Anything else is not sent to PayPal.</summary>
    public static bool IsOrderId(string? orderId)
        => orderId != null && OrderIdPattern().IsMatch(orderId);

    /// <summary>Creates the order the customer approves at PayPal.</summary>
    public Task<PayPalResponse> CreateOrderAsync(string orderJson, string requestId, CancellationToken ct = default)
        => SendAsync(HttpMethod.Post, "/v2/checkout/orders", orderJson, requestId, ct);

    /// <summary>Reads an order with its payments.</summary>
    public Task<PayPalResponse> GetOrderAsync(string orderId, CancellationToken ct = default)
        => SendAsync(HttpMethod.Get, "/v2/checkout/orders/" + Uri.EscapeDataString(orderId), null, null, ct);

    /// <summary>
    /// Takes the payment of an order the customer approved: captures it, or only authorizes it.
    /// PayPal answers a repeated request with the same request id with the first outcome.
    /// </summary>
    public Task<PayPalResponse> CompleteOrderAsync(string orderId, bool authorizeOnly, string requestId, CancellationToken ct = default)
        => SendAsync(
            HttpMethod.Post,
            "/v2/checkout/orders/" + Uri.EscapeDataString(orderId) + (authorizeOnly ? "/authorize" : "/capture"),
            "{}",
            requestId,
            ct);

    /// <summary>
    /// Asks PayPal whether it sent a webhook. The event is posted back exactly as it was received.
    /// See https://developer.paypal.com/api/rest/webhooks/rest/
    /// </summary>
    public async Task<bool> VerifyWebhookAsync(
        string webhookId,
        string? transmissionId,
        string? transmissionTime,
        string? transmissionSignature,
        string? certificateUrl,
        string? algorithm,
        string webhookEvent,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(transmissionId)
            || string.IsNullOrEmpty(transmissionTime)
            || string.IsNullOrEmpty(transmissionSignature)
            || string.IsNullOrEmpty(certificateUrl)
            || string.IsNullOrEmpty(algorithm))
        {
            return false;
        }

        var json = "{"
            + "\"auth_algo\":" + JsonSerializer.Serialize(algorithm)
            + ",\"cert_url\":" + JsonSerializer.Serialize(certificateUrl)
            + ",\"transmission_id\":" + JsonSerializer.Serialize(transmissionId)
            + ",\"transmission_sig\":" + JsonSerializer.Serialize(transmissionSignature)
            + ",\"transmission_time\":" + JsonSerializer.Serialize(transmissionTime)
            + ",\"webhook_id\":" + JsonSerializer.Serialize(webhookId)
            + ",\"webhook_event\":" + webhookEvent
            + "}";

        var response = await SendAsync(HttpMethod.Post, "/v1/notifications/verify-webhook-signature", json, null, ct)
            .ConfigureAwait(false);

        return response.IsSuccess && response.String("verification_status") == "SUCCESS";
    }

    async Task<PayPalResponse> SendAsync(HttpMethod method, string path, string? json, string? requestId, CancellationToken ct)
    {
        var token = await GetAccessTokenAsync(ct).ConfigureAwait(false);

        using var request = new HttpRequestMessage(method, BaseUrl + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        // The whole order comes back, with the payments that were made for it.
        request.Headers.Add("Prefer", "return=representation");
        if (requestId != null)
        {
            request.Headers.Add("PayPal-Request-Id", requestId);
        }
        if (json != null)
        {
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        var httpClient = _httpClientFactory.CreateClient();

        using var response = await httpClient.SendAsync(request, ct).ConfigureAwait(false);
        var content = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            _tokens.TryRemove(TokenKey, out _);
        }

        return PayPalResponse.From(response.StatusCode, content);
    }

    async Task<string> GetAccessTokenAsync(CancellationToken ct)
    {
        if (_tokens.TryGetValue(TokenKey, out var cached) && cached.Expires > DateTimeOffset.UtcNow)
        {
            return cached.Token;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, BaseUrl + "/v1/oauth2/token")
        {
            Content = new FormUrlEncodedContent([new KeyValuePair<string, string>("grant_type", "client_credentials")]),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes(_settings.ClientId!.Trim() + ":" + _settings.ClientSecret!.Trim())));

        var httpClient = _httpClientFactory.CreateClient();

        using var response = await httpClient.SendAsync(request, ct).ConfigureAwait(false);
        var content = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var token = PayPalResponse.From(response.StatusCode, content);

        var accessToken = token.String("access_token");
        if (!token.IsSuccess || string.IsNullOrEmpty(accessToken))
        {
            throw new HttpRequestException(
                $"PayPal did not accept the client id and secret ({(int)response.StatusCode} {token.Error}).",
                null,
                response.StatusCode);
        }

        var lifetime = token.Body.TryGetProperty("expires_in", out var expiresIn) && expiresIn.TryGetInt32(out var seconds)
            ? seconds
            : 0;
        _tokens[TokenKey] = (accessToken, DateTimeOffset.UtcNow.AddSeconds(lifetime - 60));

        return accessToken;
    }

    string BaseUrl => _settings.Sandbox ? SandboxUrl : LiveUrl;

    string TokenKey => BaseUrl + "|" + _settings.ClientId!.Trim();

    [GeneratedRegex("^[A-Za-z0-9-]{1,64}$")]
    private static partial Regex OrderIdPattern();
}

/// <summary>An answer from PayPal's API.</summary>
sealed class PayPalResponse
{
    public HttpStatusCode Status { get; private init; }

    /// <summary>The JSON PayPal answered with. An empty object when it sent none.</summary>
    public JsonElement Body { get; private init; }

    public bool IsSuccess => (int)Status is >= 200 and < 300;

    /// <summary>
    /// What PayPal says went wrong, e.g. "UNPROCESSABLE_ENTITY INSTRUMENT_DECLINED: The instrument presented was declined".
    /// </summary>
    public string? Error
    {
        get
        {
            var name = String("name") ?? String("error");
            var detail = Body.TryGetProperty("details", out var details) && details.ValueKind == JsonValueKind.Array
                ? details.EnumerateArray().FirstOrDefault()
                : default;
            var issue = PayPalJson.String(detail, "issue");
            var description = PayPalJson.String(detail, "description") ?? String("message") ?? String("error_description");

            var error = string.Join(' ', new[] { name, issue }.Where(x => !string.IsNullOrEmpty(x)));

            return string.IsNullOrEmpty(description) ? error : $"{error}: {description}";
        }
    }

    /// <summary>The first issue PayPal names, e.g. "INSTRUMENT_DECLINED".</summary>
    public string? Issue
        => Body.TryGetProperty("details", out var details) && details.ValueKind == JsonValueKind.Array
            ? PayPalJson.String(details.EnumerateArray().FirstOrDefault(), "issue")
            : null;

    public string? String(string name) => PayPalJson.String(Body, name);

    public static PayPalResponse From(HttpStatusCode status, string? content)
    {
        JsonElement body;
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(content) ? "{}" : content);
            body = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            using var document = JsonDocument.Parse("{}");
            body = document.RootElement.Clone();
        }

        return new PayPalResponse { Status = status, Body = body };
    }
}

static class PayPalJson
{
    public static JsonElement Child(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var child) ? child : default;

    public static JsonElement First(JsonElement element, string name)
        => Child(element, name) is { ValueKind: JsonValueKind.Array } array ? array.EnumerateArray().FirstOrDefault() : default;

    public static string? String(JsonElement element, string name)
        => Child(element, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;
}
