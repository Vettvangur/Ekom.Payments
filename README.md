<p align="center">
  <img src="Ekom_Logo.png" alt="Ekom" width="120" />
</p>

<h1 align="center">Ekom.Payments</h1>

<p align="center">
  Payment gateways for <a href="https://github.com/Vettvangur/Ekom">Ekom</a>, the open source e-commerce package for Umbraco.
</p>

<p align="center">
  <a href="https://www.nuget.org/packages/Ekom.Payments.Core/"><img src="https://img.shields.io/nuget/v/Ekom.Payments.Core?label=Ekom.Payments.Core&color=ed0f0f" alt="NuGet" /></a>
  <a href="./LICENSE"><img src="https://img.shields.io/badge/license-MIT-green" alt="MIT license" /></a>
</p>

Each gateway is one NuGet package. A package sends the customer to the gateway, verifies the gateway's answer, and records the result on the Ekom order. Ekom then works out the payment status and completes the checkout.

**Documentation:** [vettvangur.gitbook.io/ekom/payments](https://vettvangur.gitbook.io/ekom/payments)

## Gateways

| Gateway | How the customer pays | Package | Provider name | Guide |
| --- | --- | --- | --- | --- |
| Stripe | Card and other methods, on Stripe's hosted Checkout page | [`Ekom.Payments.Stripe`](https://www.nuget.org/packages/Ekom.Payments.Stripe/) | `stripe` | [Stripe](https://vettvangur.gitbook.io/ekom/payments/stripe) |
| PayPal | PayPal account, approved on PayPal's site | [`Ekom.Payments.PayPal`](https://www.nuget.org/packages/Ekom.Payments.PayPal/) | `payPal` | [PayPal](https://vettvangur.gitbook.io/ekom/payments/paypal) |
| Teya (formerly Borgun) | Card, on Teya's hosted payment page | [`Ekom.Payments.Teya`](https://www.nuget.org/packages/Ekom.Payments.Teya/) | `borgun` | [Teya](https://vettvangur.gitbook.io/ekom/payments/borgun) |
| Rapyd (formerly Valitor) | Card or card loan, on Rapyd's hosted payment page | [`Ekom.Payments.Rapyd`](https://www.nuget.org/packages/Ekom.Payments.Rapyd/) | `valitor` | [Rapyd](https://vettvangur.gitbook.io/ekom/payments/valitor) |
| Straumur | Card, on Straumur's hosted checkout page | [`Ekom.Payments.Straumur`](https://www.nuget.org/packages/Ekom.Payments.Straumur/) | `straumur` | [Straumur](https://vettvangur.gitbook.io/ekom/payments/straumur) |
| Netgiro | Pay later, on Netgiro's payment page | [`Ekom.Payments.Netgiro`](https://www.nuget.org/packages/Ekom.Payments.Netgiro/) | `netgiro` | [Netgiro](https://vettvangur.gitbook.io/ekom/payments/netgiro) |
| Siminn Pay | In the Siminn Pay app on the customer's phone | [`Ekom.Payments.SiminnPay`](https://www.nuget.org/packages/Ekom.Payments.SiminnPay/) | `siminnPay` | [Siminn Pay](https://vettvangur.gitbook.io/ekom/payments/siminn-pay) |
| Teya Consumer Loans | Consumer loan, applied for on Teya's loan portal | [`Ekom.Payments.TeyaConsumerloans`](https://www.nuget.org/packages/Ekom.Payments.TeyaConsumerloans/) | `teyaConsumerloans` | [Teya Consumer Loans](https://vettvangur.gitbook.io/ekom/payments/teya-consumer-loans) |
| AltaPay | Card, on a payment page hosted by AltaPay | [`Ekom.Payments.AltaPay`](https://www.nuget.org/packages/Ekom.Payments.AltaPay/) | `altaPay` | [AltaPay](https://vettvangur.gitbook.io/ekom/payments/altapay) |
| PayTrail | A method chosen on PayTrail's hosted payment page | [`Ekom.Payments.PayTrail`](https://www.nuget.org/packages/Ekom.Payments.PayTrail/) | `payTrail` | [PayTrail](https://vettvangur.gitbook.io/ekom/payments/paytrail) |

Each guide lists what to install, every setting, the address to give the gateway, what the customer sees, and what Ekom records for each outcome.

Offline payments, such as bank transfer or pay on pickup, need no package from this repository.

### Teya and Rapyd were Borgun and Valitor

`Ekom.Payments.Teya` replaces `Ekom.Payments.Borgun`, and `Ekom.Payments.Rapyd` replaces `Ekom.Payments.Valitor`. To move a site over:

1. Replace the package reference.
2. Rename the types in your code: the `Ekom.Payments.Borgun` namespace is `Ekom.Payments.Teya` and `BorgunSettings` is `TeyaSettings`; the `Ekom.Payments.Valitor` namespace is `Ekom.Payments.Rapyd`, and `ValitorSettings`, `ValitorResponseHelper` and `ValitorAHKException` are `RapydSettings`, `RapydResponseHelper` and `RapydAHKException`.

Nothing else changes. The provider names stay `borgun` and `valitor`, and the callback addresses stay `/ekom/payments/borgunresponse` and `/ekom/payments/valitorresponse`, so the payment provider nodes, the gateway setup and payments already started keep working.

## Requirements

- Ekom 1.2 or later. The packages cannot be used without Ekom.
- .NET 8 or later. The packages target `net8.0` and reference `Ekom.Core`, not an Umbraco package, so the same package works on every Umbraco version Ekom supports.

`Ekom.Payments.Core` is a small library the gateway packages share. It is installed with them; you do not reference it yourself.

## Get started

1. Install the package for your gateway in the site project:

   ```bash
   dotnet add package Ekom.Payments.Stripe
   ```

2. In Umbraco, create a payment provider node and set its `basePaymentProvider` property to the provider name from the table above. Ekom ignores upper and lower case in the name.

3. Give the gateway its settings, on the provider node or in configuration:

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

4. If the gateway calls your site with a webhook, register the address at the gateway. The guide for each gateway gives the address.

There is no setup code. Ekom finds the provider and registers its services when the site starts.

The full walkthrough is in [Payment and shipping providers](https://vettvangur.gitbook.io/ekom/guides/providers).

## Where settings come from

For each setting, Ekom takes the first value it finds:

1. A value set in code, for example with `paymentSettings.SetStripeSettings(...)` in a `CheckoutEvents.PayAsync` handler.
2. The property with that name on the payment provider node.
3. The configuration key `Ekom:Payments:<provider name>:<setting>`.

Because the last step reads standard .NET configuration, a secret can come from user secrets or an environment variable, for example `Ekom__Payments__stripe__secretKey`. Keep secrets out of source control.

## How a payment is recorded

1. Checkout hands the payment to the provider. The provider stores a payment on the Ekom order and sends the customer to the gateway.
2. The gateway answers, by redirect, by callback or by webhook.
3. The provider verifies the answer. An answer that cannot be verified is logged and changes nothing.
4. The provider records what happened: authorized, captured, failed, cancelled, refunded or voided.
5. Ekom recalculates the payment status. A capture or an authorization completes the checkout.

Staff see every attempt and every transaction on the order in Ekom Manager. [Payments](https://vettvangur.gitbook.io/ekom/guides/payments) explains the model, and how to read a payment or react to one in your own code.

## Documentation

| Page | Read it when you want to |
| --- | --- |
| [Payment gateways](https://vettvangur.gitbook.io/ekom/payments) | Set up one of the gateways. |
| [Payment and shipping providers](https://vettvangur.gitbook.io/ekom/guides/providers) | Create provider nodes and understand their settings. |
| [Payments](https://vettvangur.gitbook.io/ekom/guides/payments) | Understand what Ekom stores for a payment and how the status is worked out. |
| [Checkout](https://vettvangur.gitbook.io/ekom/guides/checkout) | See what happens before and after the gateway is called. |
| [Write your own gateway](https://vettvangur.gitbook.io/ekom/payments/write-your-own) | Connect a gateway that has no package. |
| [Upgrade payments](https://vettvangur.gitbook.io/ekom/upgrading/payments) | Move a site from Ekom.Payments 1.0. |

## Write a gateway

A gateway is three classes: its settings, a class that implements `IPaymentProvider`, and a controller that receives the gateway's answer. The contracts are in Ekom itself, so a gateway for one site can live in the site project without a reference to this repository. [Write your own gateway](https://vettvangur.gitbook.io/ekom/payments/write-your-own) builds a complete one step by step.

To add a gateway to this repository, start from [`Ekom.Payments.AsynchronousExample`](PaymentProviders/Ekom.Payments.AsynchronousExample), the reference implementation, and follow these rules:

- Expose the provider name as a constant named `_ppNodeName` on the `IPaymentProvider` class. Ekom finds the provider by it.
- Store the payment with `IOrderPayments.CreateAsync` before the customer leaves, and send its `PaymentId` to the gateway.
- Verify the gateway's answer before you record anything. An answer you cannot verify is logged and records nothing.
- Record one capture per payment, with `LegacyPaymentBridge.CaptureKey` as the idempotency key.
- Keep signatures, tokens and national ID numbers out of `RawData`.
- Start log lines with `PaymentLog.Template`, so every line leads with the order.
- Add a test file for the gateway in `Tests/Ekom.Payments.Tests`.

A new gateway also needs an entry in `.github/publish-projects.json`, `release-please-config.json` and `.release-please-manifest.json`. See [RELEASING.md](RELEASING.md).

## Repository layout

| Path | What it holds |
| --- | --- |
| `Core/` | `Ekom.Payments.Core`: helpers the gateways share, and the bridge that completes payments started with Ekom.Payments 1.0. |
| `PaymentProviders/Ekom.Payments.*` | One project per gateway. |
| `PaymentProviders/Ekom.Payments.AsynchronousExample` | The reference implementation. It is not published. |
| `Tests/Ekom.Payments.Tests` | The tests, one file per gateway. |

## Build and test

```bash
dotnet restore Ekom.Payments.sln
```

```bash
dotnet build Ekom.Payments.sln --no-restore
```

```bash
dotnet test Tests/Ekom.Payments.Tests/Ekom.Payments.Tests.csproj -c Release
```

The projects reference the `Ekom.Core` NuGet package, in the version set by `EkomVersion` in `Directory.Build.props`. That is the lowest Ekom version the packages work with.

### Build against a local Ekom checkout

To work on Ekom and a gateway together, create `Directory.Build.local.props` in the repository root. The file is not committed.

```xml
<Project>
    <PropertyGroup>
        <UseProjectReferences>true</UseProjectReferences>
        <EkomRoot>/path/to/Ekom/</EkomRoot>
    </PropertyGroup>
</Project>
```

## Releases

Every package has its own version and its own changelog. [Release-please](https://github.com/googleapis/release-please) opens one release pull request on `main` that lists each package with unreleased changes, and publishing a release sends that package to NuGet.

Pull request titles follow [Conventional Commits](https://www.conventionalcommits.org/), for example `fix(netgiro): handle callback retries`. [RELEASING.md](RELEASING.md) has the details.

## Contributing

Bug reports and pull requests are welcome. Open an issue in this repository for a gateway problem, and in [Vettvangur/Ekom](https://github.com/Vettvangur/Ekom/issues) for anything about checkout, orders or the payment model.

## License

[MIT](LICENSE)
