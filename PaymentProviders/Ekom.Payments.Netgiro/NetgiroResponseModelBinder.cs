using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Http;

namespace Ekom.Payments.Netgiro;

public class NetgiroResponseModelBinder : IModelBinder
{
    public async Task BindModelAsync(ModelBindingContext bindingContext)
    {
        var request = bindingContext.HttpContext.Request;
        var query = request.Query;
        IFormCollection? form = request.HasFormContentType
            ? await request.ReadFormAsync()
            : null;

        string? GetValue(string key)
        {
            if (query.TryGetValue(key, out var queryValue))
            {
                return queryValue.FirstOrDefault();
            }

            return form != null && form.TryGetValue(key, out var formValue)
                ? formValue.FirstOrDefault()
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
