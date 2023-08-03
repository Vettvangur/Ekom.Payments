using Ekom.Payments.PayPal;
using Moq;
using NUnit.Framework;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Ekom.Payments.Helpers;
using Newtonsoft.Json;

namespace Ekom.Payments.Tests;

[TestFixture]
public class PayPalResponseControllerTests
{
    private Mock<ILogger<PayPalResponseController>> _mockLogger;
    private Mock<PaymentsConfiguration> _mockSettings;
    private Mock<IOrderService> _mockOrderService;
    private Mock<IDatabaseFactory> _mockDbFac;
    private Mock<IMailService> _mockMailSvc;
    private PayPalResponseController _controller;

    [SetUp]
    public void Setup()
    {
        _mockLogger = new Mock<ILogger<PayPalResponseController>>();
        _mockSettings = new Mock<PaymentsConfiguration>();
        _mockOrderService = new Mock<IOrderService>();
        _mockDbFac = new Mock<IDatabaseFactory>();
        _mockMailSvc = new Mock<IMailService>();
        _controller = new PayPalResponseController(
            _mockLogger.Object,
            _mockSettings.Object,
            _mockOrderService.Object,
            _mockDbFac.Object,
            _mockMailSvc.Object);
    }

    [Test]
    public async Task Post_InvalidPayPalResponse_ReturnsBadRequest()
    {
        // Arrange
        var response = new Response { Custom = "invalid_guid" };

        // Act
        var result = await _controller.Post(response);

        // Assert
        Assert.IsInstanceOf<BadRequestResult>(result);
    }

    [Test]
    public async Task Post_ValidResponseButOrderNotFound_ReturnsNotFound()
    {
        // Arrange
        var response = new Response { Custom = Guid.NewGuid().ToString() };

        _mockOrderService.Setup(os => os.GetAsync(It.IsAny<Guid>())).ReturnsAsync((OrderStatus)null!);

        // Act
        var result = await _controller.Post(response);

        // Assert
        Assert.IsInstanceOf<NotFoundResult>(result);

    }

    [Test]
    public void bleh()
    {
        var orderId = "57G116920G717843S";
        var formValues = new Dictionary<string, string?>
        {
            { "upload", "1" },
            { "cmd", "_cart" },
            { "business", "sb-jo6g814679631@business.example.com" },

            { "return", "https://example.com/return" },
            { "shopping_url", "https://visir.is" },
            { "notify_url", "https://webhook.site/9958f548-4282-43c0-a093-d6b7e45cc861" },

            { "currency_code", "USD" },
            { "lc", "IS" },

            { "invoice", orderId },
            { "custom", orderId },

            // for-loop elements
            { $"item_name_1", "Hoodie" },
            { $"quantity_1", "1" },
            { $"amount_1", "50" },
            { $"discount_amount_1", "0" }
        };

        var t = FormHelper.CreateRequest(formValues, "https://www.sandbox.paypal.com/us/cgi-bin/webscr");
    }
}
