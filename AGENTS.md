# Agent Notes

## Commands
- Restore/build from the repo root: `dotnet restore Ekom.Payments.sln` then `dotnet build Ekom.Payments.sln --no-restore`.
- The solution includes `Umbraco/Ekom.Payments.U17`, which targets `net10.0`; a .NET 10 SDK is required for full-solution builds.
- To verify one package without building everything: `dotnet build <path-to-csproj> --no-restore` after restore.
- There is no active CI test step; Azure Pipelines restores, packs selected projects, and pushes NuGet packages on `main` only.

## Project layout
- `Core/` defines the shared payment abstractions (`IPaymentProvider`, `PaymentSettings`, `OrderService`, DB models/helpers).
- `AspNetCore/` wires DI, table creation, provider discovery, and controller mapping through `EkomPaymentsStartupFilter`.
- `Umbraco/Ekom.Payments.U10` is the Umbraco 13 adapter on `net8.0`; `Umbraco/Ekom.Payments.U17` is the Umbraco 17 adapter on `net10.0`.
- `PaymentProviders/Ekom.Payments.*` are provider packages that reference `Core/`; most target `net8.0`, but `Ekom.Payments.ValitorPay` targets `net7.0`.
- `PaymentProviders/ValitorPay/` is a separate legacy `Vettvangur.ValitorPay` client/test tree; it is not included in `Ekom.Payments.sln`.

## Repo-specific gotchas
- Payment providers are registered by reflection: an `IPaymentProvider` implementation must expose an internal static `_ppNodeName` constant; lookup lowercases this value.
- Runtime config is bound from `Ekom:Payments`; provider docs/settings often use the provider alias, e.g. PayTrail uses `payTrail` and callback endpoint `/ekom/payments/paytrailresponse`.
- `AspNetCore/EnsureTablesExist.cs` creates or alters `EkomPaymentOrders` and `EkomPayments` at startup for SQL Server and SQLite only; treat schema changes as runtime-impacting.
- Package restore lock files exist per project via `RestorePackagesWithLockFile`; commit package lock changes only when dependency changes are intentional.
- The root `.editorconfig` specifies CRLF, but OpenCode sessions must write LF-only files unless explicitly told otherwise.
- `PaymentProviders/Directory.Build.props` sets `ExcludeNetFx=true`, so projects under `PaymentProviders/` that honor it skip .NET Framework targets.
- `PaymentProviders/ValitorPay/ValitorPay.Tests/Config.cs` contains empty credential constants and does not compile as-is; do not treat that legacy test project as a normal validation target without fixing/providing credentials.
