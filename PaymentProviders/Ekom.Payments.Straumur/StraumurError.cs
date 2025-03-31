using System.Text.Json.Serialization;

namespace Ekom.Payments.Straumur;

public partial class StraumurError
{
    [JsonPropertyName("errorMessage")]
    public string ErrorMessage { get; set; }

    [JsonPropertyName("responseIdentifier")]
    public Guid ResponseIdentifier { get; set; }

    [JsonPropertyName("errorCode")]
    public long ErrorCode { get; set; }


    public static readonly Dictionary<long, string> ErrorMessages = new()
    {
        { 1000, "The provided request body was an invalid JSON." },
        { 1001, "The provided request body needs to start with '{'." },
        { 1002, "The provided request body does not end properly. Missing '}'." },
        { 1003, "An array in the provided request body does not end properly. Missing ']'." },
        { 1004, "An object in the provided request body does not end properly. Missing '}'." },
        { 1005, "Could not parse field [{0}]. Expected a {1}." },
        { 1006, "Unknown field [{0}] in the JSON request body." },
        { 000, "Field [{0}] cannot be empty." },
        { 2001, "Field [{0}] is not a valid number. Value: [{1}]." },
        { 2002, "Field [{0}] must be greater than 0. Value: [{1}]." },
        { 2003, "Field [{0}] contains an unknown value. Value: [{1}]." },
        { 2004, "Field [{0}] cannot be resolved. Value: [{1}]." },
        { 2005, "Field [{0}] cannot be in the past. Value: [{1}]." },
        { 2006, "Field [{0}] cannot be more than {1} hours in the future. Value: [{2}]." },
        { 2007, "Sum of [{0}] (Value: {1}) must be equal to [{2}] (Value: {3})." },
        { 2008, "Field [{0}] contains an unsupported value. Value: [{1}]. Supported values: [{2}]." },
        { 2009, "Field [{0}] exceeds maximum length of [{1}]. Value: [{2}]." },
        { 2010, "Merchant cannot access the theme or the theme does not exist. Value: [{0}]." },
        { 2011, "Merchant does not have a contract in the currency the request was created. Value: [{0}]." },
        { 2012, "Field [{0}] does not meet the minimum length requirement of [{1}]. Value: [{2}]." },
        { 2013, "Fields provided are not intended to be used together. Fields: [{0}]." },
        { 2014, "Merchant cannot access entity or the entity does not exist. Value: [{0}]." },
        { 2015, "Authorization currency and capture currency do not match. Value: [{0}]." },
        { 2016, "Merchant cannot access terminal or terminal does not exist. Value: [{0}]." },
        { 2017, "Field [{0}] must be exactly [{1}] characters long. Value: [{2}]." },
        { 2018, "A request with the same reference was already processed." },
        { 2019, "Amount requested is too high. Value: [{0}]." },
        { 2020, "Request could not be processed. Please contact support." },
        { 2021, "One payment method must be provided." },
        { 2022, "Amount requested is lower than the minimum amount for the currency. Value: [{0}]. Min amount: [{1}]." },
        { 2023, "Amount requested is invalid for tokenization. Amount must be either 0 or the minimum amount for the currency. Value: [{0}]. Min amount: [{1}]." },
        { 2024, "Origin is not in the allowed origins for the terminal identifier. Value: [{0}]. Allowed origins: [{1}]." },
        { 2025, "Item amount cannot be greater than amount without discount. Amount: [{0}]. Without Discount: [{1}]." },
        { 2026, "Amount in minor units for ISK must end in 00. Value: [{0}]." }
    };


};
