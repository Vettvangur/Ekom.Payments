# Agent Notes

## Commands
- Restore/build from the repo root: `dotnet restore Ekom.Payments.sln` then `dotnet build Ekom.Payments.sln --no-restore`.
- Every project references Ekom. With `UseProjectReferences=true` and `EkomRoot` set in an untracked `Directory.Build.local.props`, they build against a local Ekom checkout; otherwise against the `Ekom.Core` package in the `EkomVersion` set in `Directory.Build.props`. Until an Ekom release with the payment contracts exists, only the local-checkout build works.
- To verify one package without building everything: `dotnet build <path-to-csproj> --no-restore` after restore.
- Run the active test suite: `dotnet test Tests/Ekom.Payments.Tests/Ekom.Payments.Tests.csproj -c Release`.
- There is no active CI test step; GitHub Actions publishes only the group selected by its component tag in `.github/publish-projects.json` (`Ekom.Payments-v1.0.x` or `Ekom.Payments.<Provider>-v1.0.x`; legacy `0.2.x` tags are still accepted).
- Release-please runs on `main`. Core uses `Payments.Version.props`; providers have independent project versions and `CorePackageVersion` dependencies. Provider publishing builds with `UsePublishedCore=true` against NuGet Core. Each component version, its `.release-please-manifest.json` entry, and its release tag must agree. See `RELEASING.md` for setup and cutover.

## Project layout
- Ekom owns the payment contracts (`IPaymentProvider`, `PaymentSettings`, the provider registry, `IOrderPayments`). `Core/` is the library shared by the providers: crypto, CSP, form and mail helpers, `PaymentsCoreModule` (the `IPaymentModule` Ekom runs at startup), and the legacy payment model.
- The legacy model (`OrderStatus`, `PaymentData`, `OrderService`, `EnsureTablesExist`, `LegacyPaymentBridge`) only exists so payments started before a site upgraded still complete: `LegacyPaymentBridge.GetOrImportAsync` copies a legacy row to Ekom when its callback arrives. Do not build new behaviour on it.
- `PaymentProviders/Ekom.Payments.*` are provider packages that reference `Core/` and target `net8.0`. All providers in the solution record payments on the Ekom order through `IOrderPayments`; `Ekom.Payments.AsynchronousExample` is the reference.

- Provider tests use `InMemoryOrderPayments` and `ProviderTestSite` in `Tests/Ekom.Payments.Tests`; one test file per provider.

## Repo-specific gotchas
- Payment providers are found by Ekom by reflection: an `IPaymentProvider` implementation must expose a static `_ppNodeName` constant. That value is also the payment's provider type, which callbacks check with `PaymentRowGuard.BelongsTo`.
- Provider rules: verify a gateway response before recording anything; an unverifiable response is logged and records nothing; record one capture per payment with `LegacyPaymentBridge.CaptureKey`; keep signatures, tokens and national ids out of `RawData`; lead log lines with `PaymentLog.Template`.
- Runtime config is bound from `Ekom:Payments`; provider docs/settings often use the provider alias, e.g. PayTrail uses `payTrail` and callback endpoint `/ekom/payments/paytrailresponse`.
- `Core/EnsureTablesExist.cs` still creates the legacy `EkomPaymentOrders` and `EkomPayments` tables at startup for SQL Server and SQLite. New payments are stored in Ekom's `EkomOrderPayments` and `EkomOrderPaymentTransactions`.
- Package restore lock files exist per project via `RestorePackagesWithLockFile`; commit package lock changes only when dependency changes are intentional.
- The root `.editorconfig` specifies CRLF, but OpenCode sessions must write LF-only files unless explicitly told otherwise.
- `PaymentProviders/Directory.Build.props` sets `ExcludeNetFx=true`, so projects under `PaymentProviders/` that honor it skip .NET Framework targets.
