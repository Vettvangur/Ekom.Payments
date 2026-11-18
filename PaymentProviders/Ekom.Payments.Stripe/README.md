# Ekom.Payments.Stripe

[Stripe Checkout](https://docs.stripe.com/payments/checkout) for [Ekom](https://github.com/Vettvangur/Ekom). The customer pays on Stripe's hosted Checkout page; Stripe reports the outcome by webhook and the provider records it on the Ekom order.

Requires Ekom 1.2 or later.

## Setup

1. Install `Ekom.Payments.Stripe` next to Ekom.
2. Add a payment provider node named `stripe` (or of a document type with the alias `stripe`) under the Ekom payment providers node.
3. In Stripe, add a webhook endpoint for `https://<your site>/ekom/payments/striperesponse` with the **Snapshot** payload format and these events:
   - `checkout.session.completed`
   - `checkout.session.async_payment_succeeded`
   - `checkout.session.async_payment_failed`
   - `checkout.session.expired`
   - `charge.captured`
   - `charge.refunded`
   - `charge.expired`
4. Give the provider its settings.

## Settings

Each setting is read from the property of that alias on the provider node, then from `Ekom:Payments:stripe` in configuration. Set them in code with `paymentSettings.SetStripeSettings(...)` to override both.

| Setting | Required | Description |
| --- | --- | --- |
| `secretKey` | Yes | Stripe secret key (`sk_live_...`, `sk_test_...`), or a restricted key that may write Checkout Sessions. It is not stored with the payment. |
| `webhookSecret` | Yes | Signing secret (`whsec_...`) of the webhook endpoint. No payment is started without it. |
| `useDelayedCapture` | No | Only reserve the amount. The payment is recorded as authorized, and as captured when you capture it at Stripe. |
| `checkoutExpiresInMinutes` | No | How long the Checkout page stays open, 30 to 1440. Stripe's default is 24 hours. |
| `locale` | No | Checkout language. By default the payment's language when Stripe has it, otherwise the customer's browser language. Stripe has no Icelandic Checkout page. |
| `paymentMethodTypes` | No | Comma separated Stripe payment method types, e.g. `card,klarna`. By default the methods enabled in the Stripe Dashboard. |

```json
{
  "Ekom": {
    "Payments": {
      "stripe": {
        "secretKey": "sk_test_...",
        "webhookSecret": "whsec_..."
      }
    }
  }
}
```

The success, cancel and error URLs and the currency are the ordinary Ekom payment settings. After paying, the customer is sent to the success URL with `reference=<payment id>` added.

## What is recorded

| Stripe event | Recorded on the order |
| --- | --- |
| `checkout.session.completed`, paid | Captured |
| `checkout.session.completed`, unpaid, with delayed capture | Authorized |
| `checkout.session.completed`, unpaid, otherwise | Nothing: the payment method confirms later |
| `checkout.session.async_payment_succeeded` | Captured |
| `checkout.session.async_payment_failed` | Failed |
| `checkout.session.expired` | Cancelled |
| `charge.captured` | Captured, with the captured amount |
| `charge.refunded` | Refunded, with the amount of that refund. Voided when the charge was never captured |
| `charge.expired` | Voided |

A payment is captured once, however often Stripe delivers its events. An event whose signature does not verify, or that was signed more than five minutes ago, records nothing. Events about payments that were not started by Ekom are acknowledged and ignored.

## Notes

- Amounts are sent in the minor unit Stripe expects of the currency. ISK is rounded to whole krónur.
- The order's lines are listed on the Checkout page when they add up to the amount of the payment. Otherwise, for example with a discount line, the customer sees one line for the whole order.
- A declined card is not recorded: the customer can try again on the Checkout page until it expires.
- The card brand and last four digits are only known from charge events, so they are recorded with delayed captures and refunds but not with the first capture.
- Each webhook endpoint has its own signing secret. Sites that share a Stripe account (for example staging and local development on the same test keys) each need their own endpoint, and receive each other's events; those are answered with 404.
- To test locally, forward events with the Stripe CLI: `stripe listen --forward-to https://localhost:<port>/ekom/payments/striperesponse` and use the signing secret it prints.
