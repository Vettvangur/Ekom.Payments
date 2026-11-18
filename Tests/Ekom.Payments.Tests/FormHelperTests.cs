using Ekom.Payments.Helpers;
using System.Text.RegularExpressions;
using Xunit;

namespace Ekom.Payments.Tests;

public sealed class FormHelperTests
{
    [Fact]
    public void RedirectKeepsEveryQueryParameterOfTheAddress()
    {
        const string url = "https://shop.example/ekom/checkout/payment-return?orderId=1&attemptId=2&outcome=error";

        var html = FormHelper.Redirect(url);

        // An "&amp;" inside a script is not decoded by the browser: the customer would land on "amp;attemptId".
        Assert.DoesNotContain("&amp;", html);

        // What the browser assigns to window.location.href, with the JavaScript escapes resolved.
        var literal = Regex.Match(html, "window\\.location\\.href = \"(?<url>[^\"]*)\"").Groups["url"].Value;
        Assert.Equal(url, Regex.Unescape(literal));
    }

    [Fact]
    public void RedirectCannotBreakOutOfTheScript()
    {
        var html = FormHelper.Redirect("https://shop.example/?a=\"</script><script>alert(1)</script>");

        Assert.Single(Regex.Matches(html, "</script>"));
        Assert.DoesNotContain("alert(1)</script><", html.Replace("\\u003C", "<"), StringComparison.Ordinal);
    }
}
