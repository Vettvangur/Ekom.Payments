using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Http;
using System.Text.Json;

namespace Ekom.Payments.Netgiro;

public class NetgiroResponseModelBinder : IModelBinder
{
    public async Task BindModelAsync(ModelBindingContext bindingContext)
    {
        var request = bindingContext.HttpContext.Request;
        if (request.HasJsonContentType())
        {
            try
            {
                using var json = await JsonDocument.ParseAsync(
                    request.Body,
                    cancellationToken: request.HttpContext.RequestAborted);

                if (json.RootElement.ValueKind == JsonValueKind.Object)
                {
                    var root = json.RootElement;
                    bindingContext.Result = ModelBindingResult.Success(
                        CreateResponse(bindingContext, key => GetJsonValue(root, key)));
                    return;
                }

                bindingContext.ModelState.AddModelError(bindingContext.ModelName, "A JSON response body is required.");
            }
            catch (JsonException)
            {
                bindingContext.ModelState.AddModelError(bindingContext.ModelName, "The response body contains invalid JSON.");
            }

            bindingContext.Result = ModelBindingResult.Failed();
            return;
        }

        var query = request.Query;
        IFormCollection? form = request.HasFormContentType
            ? await request.ReadFormAsync()
            : null;

        string? GetValue(string key)
        {
            if (form != null && form.TryGetValue(key, out var formValue))
            {
                return formValue.FirstOrDefault();
            }

            return query.TryGetValue(key, out var queryValue)
                ? queryValue.FirstOrDefault()
                : null;
        }

        bindingContext.Result = ModelBindingResult.Success(CreateResponse(bindingContext, GetValue));
    }

    static Response CreateResponse(ModelBindingContext bindingContext, Func<string, string?> getValue)
    {
        var referenceNumberValue = getValue(nameof(Response.ReferenceNumber));
        var response = new Response
        {
            Signature = getValue(nameof(Response.Signature)) ?? string.Empty,
            ConfirmationCode = getValue(nameof(Response.ConfirmationCode)) ?? string.Empty,
            InvoiceNumber = getValue(nameof(Response.InvoiceNumber)) ?? string.Empty,
            TransactionId = getValue(nameof(Response.TransactionId)),
            TotalAmount = getValue(nameof(Response.TotalAmount)),
            Status = getValue(nameof(Response.Status)),
            NetgiroSignature = getValue(nameof(Response.NetgiroSignature)),
        };

        if (Guid.TryParse(referenceNumberValue, out var referenceNumber))
        {
            response.ReferenceNumber = referenceNumber;
        }
        else if (!string.IsNullOrEmpty(referenceNumberValue))
        {
            bindingContext.ModelState.AddModelError(
                nameof(Response.ReferenceNumber),
                "The ReferenceNumber field must be a valid GUID.");
        }

        return response;
    }

    /// <summary>
    /// Property names are matched ignoring case, numeric values are read as they were written.
    /// </summary>
    static string? GetJsonValue(JsonElement root, string key)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (!property.Name.Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return property.Value.ValueKind switch
            {
                JsonValueKind.String => property.Value.GetString(),
                JsonValueKind.Number => property.Value.GetRawText(),
                _ => null,
            };
        }

        return null;
    }
}
