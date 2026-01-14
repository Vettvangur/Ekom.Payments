using System.Text;

namespace Ekom.Payments.Helpers;

static class FormHelper
{
    public static string CreateRequest(
        Dictionary<string, string?> parameters,
        string url, 
        string method = "POST",
        string? cspNonce = null)
    {
        var html = new StringBuilder($"<form action=\"{System.Net.WebUtility.HtmlEncode(url)}\" method=\"{method}\" id=\"payform\"> ");

        foreach (var param in parameters)
            html.Append($"<input type=\"hidden\" name=\"{System.Net.WebUtility.HtmlEncode(param.Key)}\" value=\"{System.Net.WebUtility.HtmlEncode(param.Value ?? string.Empty)}\"> ");

        html.Append(@"<noscript> Please click the submit button below.<br/> <button type=""submit"">Submit</button> </noscript>");
        html.Append("</form>");

        var nonceAttribute = !string.IsNullOrWhiteSpace(cspNonce)
            ? $" nonce=\"{cspNonce}\""
            : string.Empty;

        html.Append($"<script{nonceAttribute}>(function(){{document.getElementById('payform').submit();}})()</script>");
        
        return html.ToString();
    }

    public static string Redirect(string url, string? cspNonce = null)
    {
        var safeUrl = System.Net.WebUtility.HtmlEncode(url);

        var html = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(cspNonce))
        {
            html.Append($"<script nonce=\"{cspNonce}\">(function(){{ window.location.href = \"{safeUrl}\"; }}())</script>");
        }
        else
        {
            html.Append($"<script>(function(){{ window.location.href = \"{safeUrl}\"; }}())</script>");
        }

        return html.ToString();
    }

    public static string AddQueryParam(this string url, string key, string value)
    {
        var builder = new UriBuilder(url);
        var query = System.Web.HttpUtility.ParseQueryString(builder.Query);
        query[key] = value;
        builder.Query = query.ToString();
        return builder.ToString();
    }
}
