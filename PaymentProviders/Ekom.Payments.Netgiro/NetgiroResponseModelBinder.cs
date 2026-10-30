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
                var jsonResponse = await request.ReadFromJsonAsync<Response>(
                    cancellationToken: request.HttpContext.RequestAborted);
                if (jsonResponse != null)
                {
                    bindingContext.Result = ModelBindingResult.Success(jsonResponse);
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

        var referenceNumberValue = GetValue(nameof(Response.ReferenceNumber));
        var response = new Response
        {
            Signature = GetValue(nameof(Response.Signature)) ?? string.Empty,
            ConfirmationCode = GetValue(nameof(Response.ConfirmationCode)) ?? string.Empty,
            InvoiceNumber = GetValue(nameof(Response.InvoiceNumber)) ?? string.Empty,
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

        bindingContext.Result = ModelBindingResult.Success(response);
    }
}
