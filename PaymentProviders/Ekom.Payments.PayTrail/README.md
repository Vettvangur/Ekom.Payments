# Ekom.Payments.PayTrail

PayTrail payment provider for Ekom Payments. This provider implements the PayTrail hosted payment flow for normal payments.

PayTrail documentation: <https://docs.paytrail.com/#/>

## Supported features

- Creates payments through PayTrail `POST /payments`.
- Redirects customers to the PayTrail hosted payment page.
- Validates PayTrail signed return and callback parameters.
- Records the payment on the Ekom order through Ekom's order payments: a capture when PayTrail returns `checkout-status=ok`, a failed payment when it returns `checkout-status=fail`.
- Stores the PayTrail transaction ID, payment method, reference and status with the recorded transaction. The signature is not stored.

Not currently implemented:

- Refunds
- Token payments
- Payment method button rendering
- Payment reports

## Provider alias

The payment provider node alias/name used by this provider is:

```text
payTrail
```

Use this name for the Umbraco payment provider node, or pass PayTrail settings directly through `PaymentSettings.CustomSettings`.

## Settings

Configure these values on the PayTrail payment provider node or set them in code with `SetPayTrailSettings`.

| Setting | Required | Default | Description |
| --- | --- | --- | --- |
| `AccountId` | Yes | | PayTrail merchant account ID. |
| `SecretKey` | Yes | | PayTrail merchant secret key used for HMAC signing. |
| `ApiBaseUrl` | No | `https://services.paytrail.com` | PayTrail API base URL. |
| `Algorithm` | No | `sha256` | HMAC algorithm used to sign requests: `sha256` or `sha512`. Any other value fails the payment request before a payment is created. |
| `PlatformName` | No | `ekom-vettvangur` | Sent as PayTrail `platform-name` header. |
| `DebugLog` | No | `false` | Logs full PayTrail create-payment request and response payloads, plus callback payloads with the signature redacted, at Information level. Payloads can contain customer data and must only be enabled for troubleshooting. |

A default is used only when the setting is empty after the provider node, configuration and code have been applied, so any of them can still set it.

PayTrail test credentials from their documentation:

```text
AccountId: 375917
SecretKey: SAIPPUAKAUPPIAS
```

## appsettings.json example

PayTrail settings can also be configured under `Ekom` -> `Payments` -> `payTrail`:

```json
{
  "Ekom": {
    "Payments": {
      "payTrail": {
        "accountId": "375917",
        "secretKey": "SAIPPUAKAUPPIAS",
        "apiBaseUrl": "https://services.paytrail.com",
        "algorithm": "sha256",
        "platformName": "ekom-vettvangur",
        "debugLog": false
      }
    }
  }
}
```

## Code configuration example

```csharp
using Ekom.Payments.PayTrail;

paymentSettings.SetPayTrailSettings(new PayTrailSettings
{
    AccountId = "375917",
    SecretKey = "SAIPPUAKAUPPIAS",
    ApiBaseUrl = new Uri("https://services.paytrail.com"),
    Algorithm = "sha256",
    PlatformName = "ekom-vettvangur",
    DebugLog = false,
});
```

## Payment flow

1. `Payment.RequestAsync` validates the incoming `PaymentSettings`.
2. Provider settings are populated from Umbraco/config/custom settings, then the defaults fill what is still empty. `AccountId`, `SecretKey` and `Algorithm` are checked.
3. A payment is created on the Ekom order. Its payment id is sent to PayTrail as the `stamp`.
4. A PayTrail create-payment payload is built from the Ekom order data.
5. The provider signs the request with PayTrail HMAC headers.
6. PayTrail returns a `transactionId` and hosted payment `href`.
7. The customer is redirected to the PayTrail hosted payment page.
8. PayTrail redirects the customer back to `/ekom/payments/paytrailresponse`.
9. PayTrail also calls the same response endpoint with `callback=true` for server callbacks.
10. The response controller finds the payment by the `checkout-stamp` and validates the PayTrail signature.
11. If `checkout-status=ok` and `checkout-amount` matches the order, a capture is recorded on the Ekom order, once per payment however often PayTrail answers. Ekom then completes the checkout. Subscribe to `Ekom.Events.OrderEvents.PaymentRecordedAsync` to follow recorded payments.
12. If `checkout-status=fail`, a failed payment is recorded. `pending` and `delayed` record nothing.

## URLs

The default PayTrail response endpoint is:

```text
/ekom/payments/paytrailresponse
```

The provider sends this endpoint to PayTrail as both success and cancel redirect URL. After validation the response controller redirects the customer:

| PayTrail answer | Customer is sent to |
| --- | --- |
| `ok` | `SuccessUrl` |
| `pending` or `delayed` | `SuccessUrl`. The payment is not final yet; PayTrail's server callback records it when it is. The checkout is not released. |
| `fail` or any other status | `CancelUrl` |
| Signature, `checkout-algorithm` or `checkout-amount` cannot be verified, or the amount does not match | `ErrorUrl`. Nothing is recorded. |

Server callbacks are sent to the same endpoint with `callback=true`. Callback requests return `200 OK` after processing instead of redirecting, `401 Unauthorized` when the answer cannot be verified and `400 Bad Request` when the amount does not match.

## Amounts and currencies

PayTrail expects amounts in the currency minor unit.

The provider converts Ekom decimal amounts as follows:

- Most currencies: multiplied by `100`.
- Zero-decimal currencies (`ISK`, `JPY`, `KRW`): no multiplier.

For each line item, `vatPercentage` is derived from the included VAT amount as `VAT / (GrandTotal - VAT) * 100` and rounded to one decimal place. `OrderItem.VAT` must therefore contain the VAT amount included in that line's `GrandTotal`. A non-positive VAT amount or net amount results in `vatPercentage = 0`.

## Language mapping

PayTrail supports `FI`, `SV`, and `EN`.

The provider maps the Ekom culture to PayTrail language codes:

- Finnish cultures -> `FI`
- Swedish cultures -> `SV`
- English cultures -> `EN`
- Any other language -> `EN`

## Response validation

The response controller validates every PayTrail return/callback by:

- Filtering query parameters to `checkout-*` keys.
- Sorting keys alphabetically.
- Calculating an HMAC with the configured `SecretKey` and the algorithm in `checkout-algorithm`. Only `sha256` and `sha512` are accepted.
- Comparing the calculated HMAC to the `signature` parameter in constant time.
- Rejecting a `checkout-amount` that is not a whole number.
- For `checkout-status=ok`, checking that `checkout-amount` matches the Ekom order amount.

Only a valid `checkout-status=ok` response records a capture.

## Events

`Ekom.Payments.PayTrail.Events.Success` and `Events.Error` are obsolete and no longer raised. Payments are recorded on the Ekom order: subscribe to `Ekom.Events.OrderEvents.PaymentRecordedAsync` instead.

## Build

From the repository root:

```bash
dotnet build Ekom.Payments.sln
```

## Notes

- Keep PayTrail `SecretKey` out of source control.
- PayTrail may call callbacks more than once; a payment's capture is recorded once however often it arrives.
- The PayTrail signature payload uses line feed (`\n`) separators. Carriage returns are not supported by PayTrail.
