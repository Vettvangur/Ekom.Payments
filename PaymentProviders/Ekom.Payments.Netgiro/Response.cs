using System;
using System.ComponentModel.DataAnnotations;

namespace Ekom.Payments.Netgiro;

/// <summary>
/// Response data from Netgiro Server
/// </summary>
public class Response
{
    private string _signature;
    [Required]
    public string Signature
    {
        get => _signature;
        set => _signature = System.Net.WebUtility.HtmlEncode(value);
    }

    [Required]
    public Guid ReferenceNumber { get; set; }

    private string _confirmationCode;
    [Required]
    public string ConfirmationCode
    {
        get => _confirmationCode;
        set => _confirmationCode = System.Net.WebUtility.HtmlEncode(value);
    }

    private string _invoiceNumber;
    [Required]
    public string InvoiceNumber
    {
        get => _invoiceNumber;
        set => _invoiceNumber = System.Net.WebUtility.HtmlEncode(value);
    }
}
