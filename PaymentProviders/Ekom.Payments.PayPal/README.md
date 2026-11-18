# Ekom.Payments.PayPal

[PayPal Checkout](https://developer.paypal.com/docs/checkout/) for [Ekom](https://github.com/Vettvangur/Ekom). The customer approves the payment at PayPal; the provider takes the payment when they return to the shop and records it on the Ekom order.

Requires Ekom 1.2 or later.

## Setup

1. Install `Ekom.Payments.PayPal` next to Ekom.
2. Add a payment provider node named `payPal` (or of a document type with the alias `payPal`) under the Ekom payment providers node.
3. Create a REST app in the [PayPal Developer Dashboard](https://developer.paypal.com/dashboard/applications) and copy its client id and secret.
4. Add a webhook to the app for `https://<your site>/ekom/payments/paypalresponse/webhook` with these events, and copy its webhook id:
   - `CHECKOUT.ORDER.APPROVED`
   - `PAYMENT.CAPTURE.COMPLETED`
   - `PAYMENT.CAPTURE.DECLINED`
   - `PAYMENT.CAPTURE.DENIED`
   - `PAYMENT.CAPTURE.PENDING`
   - `PAYMENT.CAPTURE.REFUNDED`
   - `PAYMENT.CAPTURE.REVERSED`
   - `PAYMENT.AUTHORIZATION.CREATED`
   - `PAYMENT.AUTHORIZATION.VOIDED`
5. Give the provider its settings.

The webhook is optional but recommended. Without it a customer who approves the payment and never returns to the shop is not charged, and refunds made at PayPal are not recorded.

## Settings

Each setting is read from the property of that alias on the provider node, then from `Ekom:Payments:payPal` in configuration. Set them in code with `paymentSettings.SetPayPalSettings(...)` to override both.

| Setting | Required | Description |
| --- | --- | --- |
| `clientId` | Yes | Client id of the PayPal REST app. |
| `clientSecret` | Yes | Secret of the PayPal REST app. It is not stored with the payment, so it has to be on the node or in configuration: it is read again when the customer returns and for webhooks. |
| `webhookId` | No | Id of the PayPal webhook. Webhooks are refused without it. |
| `sandbox` | No | Use PayPal's sandbox. Sandbox apps have their own client id, secret and webhook id. |
| `useDelayedCapture` | No | Only reserve the amount. The payment is recorded as authorized, and as captured when you capture it at PayPal. |
| `brandName` | No | Name of the shop on the PayPal pages, instead of the name of the PayPal account. |
| `locale` | No | Language of the PayPal pages as a BCP 47 tag, e.g. `en-GB`. By default PayPal chooses. |

```json
{
  "Ekom": {
    "Payments": {
      "payPal": {
        "clientId": "...",
        "clientSecret": "...",
        "webhookId": "...",
        "sandbox": true
      }
    }
  }
}
```

The success, cancel and error URLs and the currency are the ordinary Ekom payment settings. PayPal sends the customer to `/ekom/payments/paypalresponse`, which takes the payment and sends them on to the success URL with `reference=<payment id>` added, or to the error URL when PayPal refused the payment. A customer who cancels at PayPal goes to the cancel URL.

## Currencies

PayPal only takes [its own list of currencies](https://developer.paypal.com/api/rest/reference/currency-codes/). **ISK is not one of them**: a shop that sells in ISK has to offer PayPal on a store that charges in another currency, such as EUR or USD.

## What is recorded

| What happens | Recorded on the order |
| --- | --- |
| The customer returns and the payment is captured | Captured |
| The customer returns, with delayed capture | Authorized |
| PayPal refuses to take the approved payment | Failed |
| PayPal holds the payment for review | Nothing, until its webhook tells the outcome |
| `CHECKOUT.ORDER.APPROVED` | The payment is taken as if the customer had returned |
| `PAYMENT.CAPTURE.COMPLETED` | Captured, with the captured amount |
| `PAYMENT.CAPTURE.DECLINED`, `PAYMENT.CAPTURE.DENIED` | Failed |
| `PAYMENT.CAPTURE.REFUNDED`, `PAYMENT.CAPTURE.REVERSED` | Refunded, with the amount of that refund |
| `PAYMENT.AUTHORIZATION.CREATED` | Authorized |
| `PAYMENT.AUTHORIZATION.VOIDED` | Voided |

A payment is captured once, however often the customer opens the return address or PayPal delivers its events. Only the PayPal order that was created for a payment is captured for it. For every webhook PayPal is asked whether it sent it; one PayPal does not vouch for records nothing. Events about payments that were not started by Ekom are acknowledged and ignored.

## Notes

- A customer who leaves PayPal without approving is not recorded as cancelled: PayPal does not report it.
- The customer sees the order's name and total at PayPal, not its lines.
- To test, set `sandbox` and use a sandbox app and a sandbox buyer account. PayPal's webhook simulator sends events that cannot be verified, so use real sandbox payments to test the webhook.
