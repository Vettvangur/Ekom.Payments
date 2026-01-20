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
        readonly string _apiKey;
        readonly string _baseServiceUrl;

        private async Task<HttpClient> CreateHttpClient()
        {
            var token = await AuthenticateAsync().ConfigureAwait(false);
            var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            return httpClient;
        }

        /// <summary>
        /// ctor
        /// </summary>
        /// <param name="apiKey"></param>
        /// <param name="baseServiceUrl"></param>
        public SiminnPayService(string apiKey, string baseServiceUrl, ILogger logger)
        {
            if (string.IsNullOrEmpty(apiKey))
            {
                throw new ArgumentException("string.IsNullOrEmpty", nameof(apiKey));
            }
            if (string.IsNullOrEmpty(baseServiceUrl))
            {
                throw new ArgumentException("string.IsNullOrEmpty", nameof(apiKey));
            }

            _logger = logger;

            _baseServiceUrl = baseServiceUrl;
            _apiKey = apiKey;
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="payOrder"></param>
        /// <param name="notifyUrl"></param>
        /// <param name="currency"></param>
        /// <param name="restrictToLoan"></param>
        /// <exception cref="SiminnPayApiUnauthorizedException">Authorization invalid</exception>
        /// <exception cref="SiminnPayApiUnknownPhoneException">Phone number not registered with service</exception>
        /// <exception cref="SiminnPayApiResponseException">Unrecognized siminn pay api error</exception>
        public async Task<CreatePaymentOrderResponse> CreatePaymentOrder(
            SiminnPayOrder payOrder,
            string notifyUrl,
            string currency = "ISK",
            bool restrictToLoan = false)
        {
            var order = new CreatePaymentOrderRequest
            {
                Amount = payOrder.Amount,
                Description = payOrder.Description,
                ReferenceId = payOrder.ReferenceId,
                TimeToLive = 60,
                Currency = currency,
                Recipients = [ new() {
                        Phone = payOrder.PhoneNumber,
                        SendNotification = true
                    }
                ],
                CallbackUrl = notifyUrl,
                RestrictToLoan = restrictToLoan,
            };

            var orderContent = JsonConvert.SerializeObject(order);
            _logger.LogDebug("CreatePaymentOrder request {OrderContent}", orderContent);


            using var httpClient = await CreateHttpClient().ConfigureAwait(false);
            using var httpContent = new StringContent(orderContent, Encoding.UTF8, "application/json");
            var resp = await httpClient.PostAsync(new Uri($"{_baseServiceUrl}/paymentOrder/v1/order/"), httpContent).ConfigureAwait(false);

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
        public async Task<GetPaymentOrderStatusResponse> GetStatus(Guid orderKey)
        {
            using (var httpClient = await CreateHttpClient().ConfigureAwait(false))
            {
                var resp = await httpClient.GetAsync(new Uri($"{_baseServiceUrl}/paymentOrder/v1/order/{orderKey}/status")).ConfigureAwait(false);

                await HandleOrderActionsErrorResponseAsync(resp).ConfigureAwait(false);

                resp.EnsureSuccessStatusCode();

                var content = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

                _logger.LogDebug("GetStatus response {Content}", content);

                return JsonConvert.DeserializeObject<GetPaymentOrderStatusResponse>(content);
            }
        }

        /// <summary>
        /// Updates the amount.
        /// </summary>
        /// <param name="siminnPayOrder">The siminn pay order.</param>
        /// <param name="membershipInfo">The membership information.</param>
        /// <exception cref="SiminnPayApiUnauthorizedException">Authorization invalid</exception>
        /// <exception cref="SiminnPayApiNotFoundException">Order not found</exception>
        /// <exception cref="SiminnPayApiResponseException">Unrecognized siminn pay api error</exception>
        public async Task UpdateAmount(SiminnPayOrder siminnPayOrder, IEnumerable<MembershipDiscount>? membershipInfo = null)
        {
            var updateRequest = new UpdateOrderAmountRequest
            {
                OriginalAmount = siminnPayOrder.OriginalAmount,
                NewAmount = siminnPayOrder.Amount,
                MembershipDiscounts = membershipInfo ?? []
            };

            var orderContent = JsonConvert.SerializeObject(updateRequest);
            _logger.LogDebug("UpdateAmount request {OrderContent}", orderContent);


            using var httpClient = await CreateHttpClient().ConfigureAwait(false);
            using var httpContent = new StringContent(orderContent, Encoding.UTF8, "application/json");
            var resp = await httpClient.PutAsync(
                new Uri($"{_baseServiceUrl}/paymentOrder/v1/order/{siminnPayOrder.OrderKey}/discount"),
                httpContent
            ).ConfigureAwait(false);

            await HandleOrderActionsErrorResponseAsync(resp).ConfigureAwait(false);

            resp.EnsureSuccessStatusCode();
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
            using var httpClient = await CreateHttpClient().ConfigureAwait(false);
            var resp = await httpClient.DeleteAsync(new Uri($"{_baseServiceUrl}/paymentOrder/v1/order/{orderKey}")).ConfigureAwait(false);

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
        public async Task<SiminnPayLoanResponse> CalculateLoan(int amount)
        {
            using var httpClient = await CreateHttpClient().ConfigureAwait(false);
            var resp = await httpClient.GetAsync(new Uri($"{_baseServiceUrl}/loan/v1/calculate/alloptions/{amount}")).ConfigureAwait(false);

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

        private async Task<string> AuthenticateAsync()
        {
            using (var httpClient = new HttpClient())
            {
                httpClient.BaseAddress = new Uri(_baseServiceUrl);

                using var content = new StringContent(
                    JsonConvert.SerializeObject(new
                    {
                        apiKey = _apiKey,
                    }),
                    Encoding.UTF8,
                    "application/json"
                );

                var response = await httpClient.PostAsync(
                    new Uri("/authentication/v1/accesstoken", UriKind.Relative),
                    content)
                    .ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                var messageStr = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                var tokenValues = JsonConvert.DeserializeObject<Dictionary<string, string>>(messageStr);
                var token = tokenValues["token"];

                _logger.LogDebug("Got token");

                return token;
            }
        }
    }
}
