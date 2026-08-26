using Ekom.Payments.SiminnPay.apimodels;
using Ekom.Payments.SiminnPay.Exceptions;
using Ekom.Payments.SiminnPay.Model;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace Ekom.Payments.SiminnPay
{
    /// <summary>
    /// Handles communication with siminn pay api
    /// </summary>
    public class SiminnPayService
    {
        readonly ILogger _logger;
        readonly SiminnPaySettings _settings;
        readonly HttpClient _client;

        /// <summary>
        /// ctor
        /// </summary>
        /// <param name="settings"></param>
        /// <param name="logger"></param>
        public SiminnPayService(SiminnPaySettings settings, ILogger logger)
        {
            ArgumentException.ThrowIfNullOrEmpty(settings.ApiKey);
            ArgumentException.ThrowIfNullOrEmpty(settings.ApiUrl);

            _logger = logger;
            _settings = settings;
            _client = new HttpClient()
            {
                BaseAddress = new Uri(settings.ApiUrl)
            };
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="payOrder"></param>
        /// <param name="notifyUrl"></param>
        /// <exception cref="SiminnPayApiUnauthorizedException">Authorization invalid</exception>
        /// <exception cref="SiminnPayApiUnknownPhoneException">Phone number not registered with service</exception>
        /// <exception cref="SiminnPayApiResponseException">Unrecognized siminn pay api error</exception>
        public async Task<CreatePaymentOrderResponse?> CreatePaymentOrder(CreateSiminnPayOrder payOrder, Uri? notifyUrl)
        {
            var order = new CreatePaymentOrderRequest
            {
                Amount = payOrder.Amount,
                Description = payOrder.Description,
                ReferenceId = payOrder.ReferenceId,
                TimeToLive = _settings.TimeToLive > 0 ? _settings.TimeToLive : 60,
                Currency = _settings.Currency,
                PaymentType = _settings.PaymentType,
                Recipients = [ new() {
                        Phone = payOrder.PhoneNumber,
                        SendNotification = true
                    }
                ],
                CallbackUrl = notifyUrl?.ToString() ?? string.Empty,
                RestrictToLoan = _settings.RestrictToLoan,
            };

            _logger.LogDebug("CreatePaymentOrder request {@OrderContent}", order);

            await AuthenticateAsync().ConfigureAwait(false);
            using var httpContent = new StringContent(JsonConvert.SerializeObject(order), Encoding.UTF8, "application/json");
            var resp = await _client.PostAsync(new Uri("paymentorder/order", UriKind.Relative), httpContent).ConfigureAwait(false);

            await HandleCreateOrderErrorResponseAsync(resp).ConfigureAwait(false);

            resp.EnsureSuccessStatusCode();

            var content = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

            _logger.LogDebug("CreatePaymentOrder response {Content}", content);

            return JsonConvert.DeserializeObject<CreatePaymentOrderResponse>(content);
        }

        /// <summary>
        /// Get siminn pay order status
        /// </summary>
        /// <param name="orderKey"></param>
        /// <exception cref="SiminnPayApiUnauthorizedException">Authorization invalid</exception>
        /// <exception cref="SiminnPayApiNotFoundException">Order not found</exception>
        /// <exception cref="SiminnPayApiResponseException">Unrecognized siminn pay api error</exception>
        public async Task<GetPaymentOrderStatusResponse?> GetStatus(Guid orderKey)
        {
            await AuthenticateAsync().ConfigureAwait(false);
            var resp = await _client.GetAsync(new Uri($"paymentorder/order/{orderKey}/status", UriKind.Relative)).ConfigureAwait(false);

            await HandleOrderActionsErrorResponseAsync(resp).ConfigureAwait(false);

            resp.EnsureSuccessStatusCode();

            var content = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

            _logger.LogDebug("GetStatus response {Content}", content);

            return JsonConvert.DeserializeObject<GetPaymentOrderStatusResponse>(content);
        }

        /// <summary>
        /// Deletes the order.
        /// </summary>
        /// <param name="orderKey">The order key.</param>
        /// <exception cref="SiminnPayApiUnauthorizedException">Authorization invalid</exception>
        /// <exception cref="SiminnPayApiNotFoundException">Order not found</exception>
        /// <exception cref="SiminnPayApiResponseException">Unrecognized siminn pay api error</exception>
        public async Task DeleteOrder(Guid orderKey)
        {
            await AuthenticateAsync().ConfigureAwait(false);
            var resp = await _client.DeleteAsync(new Uri($"paymentorder/order/{orderKey}", UriKind.Relative)).ConfigureAwait(false);

            await HandleOrderActionsErrorResponseAsync(resp).ConfigureAwait(false);

            resp.EnsureSuccessStatusCode();
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="amount"></param>
        /// <exception cref="SiminnPayApiUnprocessableEntityException">No possible loans could be calculated</exception>
        /// <exception cref="SiminnPayApiUnauthorizedException">Authorization invalid</exception>
        /// <exception cref="SiminnPayApiResponseException">Unrecognized siminn pay api error</exception>
        public async Task<SiminnPayLoanResponse?> CalculateLoan(int amount)
        {
            await AuthenticateAsync().ConfigureAwait(false);
            var resp = await _client.PostAsync(new Uri($"loan/calculate/alloptions/{amount}", UriKind.Relative), null).ConfigureAwait(false);

            await HandleLoanCalculationErrorResponseAsync(resp).ConfigureAwait(false);

            resp.EnsureSuccessStatusCode();

            var content = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

            _logger.LogDebug("CalculateLoan response {Content}", content);

            return JsonConvert.DeserializeObject<SiminnPayLoanResponse>(content);
        }

        private async Task HandleCreateOrderErrorResponseAsync(HttpResponseMessage response)
        {
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                var errorResponse = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                _logger.LogError("status = {StatusCode} {ErrorResponse}", response.StatusCode, errorResponse);

                throw new SiminnPayApiUnknownPhoneException();
            }

            await HandleErrorResponseAsync(response).ConfigureAwait(false);
        }
        private async Task HandleOrderActionsErrorResponseAsync(HttpResponseMessage response)
        {
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                var errorResponse = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                _logger.LogError("status = {StatusCode} {ErrorResponse}", response.StatusCode, errorResponse);

                throw new SiminnPayApiNotFoundException();
            }

            await HandleErrorResponseAsync(response).ConfigureAwait(false);
        }
        private async Task HandleLoanCalculationErrorResponseAsync(HttpResponseMessage response)
        {
            if (response.StatusCode == (HttpStatusCode)422)
            {
                var errorResponse = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                _logger.LogError("status = {StatusCode} {ErrorResponse}", response.StatusCode, errorResponse);

                throw new SiminnPayApiUnprocessableEntityException();
            }

            await HandleErrorResponseAsync(response).ConfigureAwait(false);
        }

        private async Task HandleErrorResponseAsync(HttpResponseMessage response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var errorResponse = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                _logger.LogError("status = {StatusCode} {ErrorResponse}", response.StatusCode, errorResponse);

                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    throw new SiminnPayApiUnauthorizedException();
                }
                else
                {
                    throw new SiminnPayApiResponseException();
                }
            }
        }

        private async Task AuthenticateAsync()
        {
            using var content = new StringContent(
                JsonConvert.SerializeObject(new
                {
                    apiKey = _settings.ApiKey,
                }),
                Encoding.UTF8,
                "application/json");

            var response = await _client.PostAsync(new Uri("authentication/accesstoken", UriKind.Relative), content).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var messageStr = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            var tokenValues = JsonConvert.DeserializeObject<Dictionary<string, string>>(messageStr);
            if (tokenValues == null || !tokenValues.TryGetValue("token", out var token) || string.IsNullOrWhiteSpace(token))
            {
                _logger.LogError("Authentication response did not contain a token");
                throw new SiminnPayApiResponseException();
            }

            _logger.LogDebug("Got token");

            _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
    }
}
