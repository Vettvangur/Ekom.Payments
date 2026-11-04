# Teya Consumer Loans polling fix

Official API v3 documentation:

- https://docs.borgun.is/consumerloans/ecommerceloanv3/
- https://docs.borgun.is/consumerloans/clapiv3/

`TokenValidMinutes` is the time allowed to open the loan URL.
`ProgressValidMinutes` is the time allowed to complete the loan after opening it.
Polling therefore covers the **sum** of those windows, rather than stopping when
the token-start window ends. With both settings at five minutes, monitoring lasts
up to ten minutes and stops earlier when Teya returns a final status.

Temporary status-request failures (network/client timeout, HTTP 408, 429 and 5xx)
retry at the normal polling interval within that same window. Validation and
success callbacks are not automatically retried. Their existing payment-saving,
error-event and email-alert behavior remains unchanged.

`Events.ActivityAsync` allows the site to forward start, status changes, payment
confirmation, errors and timeout messages to Ekom's order Activity log. Messages
include the payment attempt ID, not tokens or customer details. Use
`args.PaymentSettings.OrderCustomData["ekomOrderUniqueId"]` to find the backoffice
order; the payment attempt ID is different. Unsubscribe on application termination.

The original in-memory queue and per-order polling remain in place. New attempts
store a `teyaConsumerloansPolling=pending` marker in their existing
`PaymentSettings.OrderCustomData`, plus the absolute success URL in the existing
`SuccessUrl` field. No table or column is added.

After application startup, the worker makes one database pass for marked, unpaid
attempts. It resumes each attempt for the remainder of its original token/progress
window, calculated from the saved payment creation time. If the window has already
elapsed, it makes one status request instead of opening a fresh polling window:

- `SUCCESS`: run the existing validation/payment-completion flow.
- Failed, canceled or expired: log the final status and finish recovery.
- Still processing: log a manual-review warning and finish recovery.
- Request failure: log the error and leave the marker pending for another startup.

Final results persist `teyaConsumerloansPolling=finished` in the existing settings.
A normal local timeout or shutdown leaves the marker pending. Historical unmarked
attempts and paid attempts are not recovered; success callbacks are not replayed.
There is no recurring recovery scanner or change to stock-reservation checks.

This implementation assumes repeated/concurrent Teya validation is safe, as agreed
for this change; that is **not confirmed by the published Teya documentation**.
There are deliberately no database locks, leases or heartbeats. Overlapping app
instances or slots sharing a database can check/validate the same unpaid attempt;
Ekom's existing paid-order and checkout reconciliation behavior remains in place.

A local timeout does not prove that a loan failed: check Teya before starting
another payment. A loan still processing at the overdue one-time check requires
manual review; this implementation does not keep polling it indefinitely.

Tests use fake time, simulated HTTP responses and a local SQLite database. Run:

```text
dotnet test Tests/Ekom.Payments.Tests/Ekom.Payments.Tests.csproj
```
