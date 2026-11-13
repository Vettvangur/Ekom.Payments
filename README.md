<h1 align="center">
Ekom 
 
[![Nuget](https://img.shields.io/nuget/vpre/Ekom.U10?color=ed0f0f)](https://www.nuget.org/packages/Ekom.U10/)
[![License](https://img.shields.io/badge/license-MIT-green)](./LICENSE)

</h1>

<h2 align="center">
Open Source Ecommerce package for Umbraco
</h2>

Supports Umbraco version 10+

## Installation
Install the appropriate Umbraco versioned package to your solution (f.x. Ekom.U10)
Install the Ekom.Web package into your sites main project (contains wwwroot)

**NuGet:** [https://www.nuget.org/packages/Ekom.U10](https://www.nuget.org/packages/Ekom.U10)

`dotnet add package Ekom.U10`

`PM> Install-Package Ekom.U10`

##### Ekom.Web

`dotnet add package Ekom.Web`

`PM> Install-Package Ekom.Web`

## Payment providers for Ekom

This repository holds the online payment providers for [Ekom](https://github.com/Vettvangur/Ekom). Ekom owns the payment contracts and records every payment on its order; a provider here sends the customer to its gateway, verifies the gateway's answer and records what it means for the order.

Install one package per gateway next to Ekom. Nothing else is needed: Ekom finds the provider and registers its services.

| Gateway | Package |
| --- | --- |
| Borgun / Teya cards | `Ekom.Payments.Borgun` |
| Valitor | `Ekom.Payments.Valitor` |
| Netgíró | `Ekom.Payments.Netgiro` |
| Straumur | `Ekom.Payments.Straumur` |
| AltaPay | `Ekom.Payments.AltaPay` |
| PayTrail | `Ekom.Payments.PayTrail` |
| Síminn Pay | `Ekom.Payments.SiminnPay` |
| Teya consumer loans | `Ekom.Payments.TeyaConsumerloans` |

Offline payments (bank transfer, pay on pickup) need no package from this repository.

`Ekom.Payments.Core` is a small library shared by the providers and comes with them. The `Ekom.Payments.U10`, `U17`, `U18`, `U19` and `Ekom.Payments.AspNetCore` packages no longer exist.

These packages require Ekom 1.1 or later and do not run without Ekom. Upgrading from Ekom.Payments 1.0 is described in Ekom's `docs/upgrading/payments.md`, and the payment model in `docs/guides/payments.md`.

### Writing a provider

`PaymentProviders/Ekom.Payments.AsynchronousExample` is the reference implementation:

1. `Payment.RequestAsync` stores the payment on the order with `IOrderPayments.CreateAsync` and sends its `PaymentId` to the gateway.
2. The response controller finds the payment, checks that it belongs to the provider, verifies the gateway's answer and records it with `IOrderPayments.RecordAsync`.
3. A response that cannot be verified is logged and records nothing.
4. Logs lead with the order: `PaymentLog.BeginScope(logger, payment)`.

### Building against a local Ekom checkout

Create `Directory.Build.local.props` (not committed) in the repository root:

```xml
<Project>
    <PropertyGroup>
        <UseProjectReferences>true</UseProjectReferences>
        <EkomRoot>/path/to/Ekom/</EkomRoot>
    </PropertyGroup>
</Project>
```

Without it the projects reference the `Ekom.Core` NuGet package in the version set by `EkomVersion` in `Directory.Build.props`.

## Features

- B2B and B2C support
- Headless support
- Multiple currency and language support
- Powerful order management
- Multiple payment providers out of the box and pluggable providers
- Flexible Shipping provider configuration with connection to external services with Íslandspóst, TVG Zimsen or Dropp
- Flexible discounts
- Complex variant support
- Supports integration with external systems like Business Central, Dynamic AX, DK, Salesforce and many more.
- Powerful inventory management
- Google Analytics Ecommerce support
- Built with ASP NET Core with focus on performance and security
- Fully extendable
- And much more...

## Documentation

[Link to documentation](https://vettvangur.gitbook.io/ekom/)
