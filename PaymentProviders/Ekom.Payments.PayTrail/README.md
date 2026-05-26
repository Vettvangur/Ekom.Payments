# Ekom.Payments.PayTrail

PayTrail payment provider for Ekom Payments. This provider implements the PayTrail hosted payment flow for normal payments.

PayTrail documentation: <https://docs.paytrail.com/#/>

## Supported features

- Creates payments through PayTrail `POST /payments`.
- Redirects customers to the PayTrail hosted payment page.
- Validates PayTrail signed return and callback parameters.
- Marks Ekom payment orders as paid when PayTrail returns `checkout-status=ok`.
- Stores the PayTrail transaction ID in the Ekom payment order `CustomData` field.
- Stores payment callback data in `EkomPayments`.

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
| `ApiBaseUrl` | Yes | `https://services.paytrail.com` | PayTrail API base URL. |
| `Algorithm` | Yes | `sha256` | HMAC algorithm. PayTrail supports `sha256` and `sha512`. |
| `PlatformName` | No | `ekom-vettvangur` | Sent as PayTrail `platform-name` header. |

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
        "platformName": "ekom-vettvangur"
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
});
```

## Payment flow

1. `Payment.RequestAsync` validates the incoming `PaymentSettings`.
2. Provider settings are populated from Umbraco/config/custom settings.
3. A pending Ekom payment order is inserted.
4. A PayTrail create-payment payload is built from the Ekom order data.
5. The provider signs the request with PayTrail HMAC headers.
6. PayTrail returns a `transactionId` and hosted payment `href`.
7. The `transactionId` is saved to the Ekom payment order `CustomData` field.
8. The customer is redirected to the PayTrail hosted payment page.
9. PayTrail redirects the customer back to `/ekom/payments/paytrailresponse`.
10. PayTrail also calls the same response endpoint with `callback=true` for server callbacks.
11. The response controller validates the PayTrail signature and amount.
12. If `checkout-status=ok`, the Ekom order is marked paid and success events are raised.

## URLs

The default PayTrail response endpoint is:

```text
/ekom/payments/paytrailresponse
```

The provider sends this endpoint to PayTrail as both success and cancel redirect URL. The response controller then redirects the customer to the configured Ekom `SuccessUrl` or `CancelUrl` after validation.

Server callbacks are sent to the same endpoint with `callback=true`. Callback requests return `200 OK` after processing instead of redirecting.

## Amounts and currencies

PayTrail expects amounts in the currency minor unit.

The provider converts Ekom decimal amounts as follows:

- Most currencies: multiplied by `100`.
- Zero-decimal currencies (`ISK`, `JPY`, `KRW`): no multiplier.

Line items are sent with `vatPercentage = 0` because Ekom `OrderItem` does not currently expose VAT percentage data.

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
- Calculating an HMAC with the configured `SecretKey`.
- Comparing the calculated HMAC to the `signature` parameter.
- Checking that `checkout-amount` matches the stored Ekom order amount.

Only a valid `checkout-status=ok` response marks the order as paid.

## Events

Provider-specific events are exposed through `Ekom.Payments.PayTrail.Events`:

```csharp
Events.Success += (sender, args) =>
{
    // Payment verified successfully.
};

Events.Error += (sender, args) =>
{
    // Payment failed or verification failed.
};
```

These events also forward to the global Ekom payment events.

## Build

From the repository root:

```bash
dotnet build Ekom.Payments.sln
```

## Notes

- Keep PayTrail `SecretKey` out of source control.
- PayTrail may call callbacks more than once; the response controller is idempotent for already-paid orders.
- The PayTrail signature payload uses line feed (`\n`) separators. Carriage returns are not supported by PayTrail.
