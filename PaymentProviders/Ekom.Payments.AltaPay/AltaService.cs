using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Xml.Linq;

namespace Ekom.Payments.AltaPay;

public class AltaService
{
    private readonly ILogger<AltaService> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly Microsoft.Extensions.Configuration.IConfiguration _configuration;
    public AltaService(ILogger<AltaService> logger, IHttpClientFactory httpClientFactory, Microsoft.Extensions.Configuration.IConfiguration configuration)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
    }
    public async Task CaptureReservationAsync(string transactionId, decimal? amount = null, string? reconciliationIdentifier = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(transactionId))
            throw new ArgumentException("Transaction ID is required.", nameof(transactionId));

        var client = _httpClientFactory.CreateClient("AltaPay");

        // Build form body
        var form = new List<KeyValuePair<string, string>>
        {
            new("transaction_id", transactionId)
        };

        if (amount.HasValue && amount.Value > 0)
        {
            form.Add(new("amount", amount.Value.ToString("0.00################", CultureInfo.InvariantCulture)));
        }

        if (!string.IsNullOrWhiteSpace(reconciliationIdentifier))
        {
            form.Add(new("reconciliation_identifier", reconciliationIdentifier));
        }

        using var req = new HttpRequestMessage(HttpMethod.Post, "captureReservation")
        {
            Content = new FormUrlEncodedContent(form)
        };

        using var resp = await client.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogError("AltaPay captureReservation failed: {Status} {Reason}. Body: {Body}",
                (int)resp.StatusCode, resp.ReasonPhrase, body);
            throw new HttpRequestException($"AltaPay captureReservation failed: {(int)resp.StatusCode} {resp.ReasonPhrase}");
        }

        // Parse XML response
        try
        {
            var xml = XDocument.Parse(body);
            var result = xml.Descendants("Result").FirstOrDefault()?.Value;

            if (!string.Equals(result, "Success", StringComparison.OrdinalIgnoreCase))
            {
                var merchantMsg = xml.Descendants("MerchantErrorMessage").FirstOrDefault()?.Value;
                var cardMsg = xml.Descendants("CardHolderErrorMessage").FirstOrDefault()?.Value;
                var reason = xml.Descendants("Reason").FirstOrDefault()?.Value;

                var details = merchantMsg ?? cardMsg ?? reason ?? "Unknown AltaPay error";

                _logger.LogError("AltaPay captureReservation returned non-success. Result={Result}, Details={Details}, XML={Xml}",
                    result, details, body);

                throw new InvalidOperationException($"AltaPay capture failed: {details}");
            }

            var capturedAmount = xml.Descendants("CapturedAmount").FirstOrDefault()?.Value;
            var currency = xml.Descendants("Currency").FirstOrDefault()?.Value;

            _logger.LogInformation("AltaPay capture succeeded. Tx={TransactionId}, Captured={CapturedAmount} {Currency}",
                transactionId, capturedAmount ?? "(n/a)", currency ?? "");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse AltaPay captureReservation XML. Body: {Body}", body);
            throw;
        }
    }

}

