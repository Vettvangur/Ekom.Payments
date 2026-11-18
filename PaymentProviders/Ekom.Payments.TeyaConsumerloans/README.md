# Teya Consumer Loans polling fix

Official API v3 documentation:

- https://docs.borgun.is/consumerloans/ecommerceloanv3/
- https://docs.borgun.is/consumerloans/clapiv3/

## Settings

`apiBaseUrl`, `username`, `password`, `loanPortalUrl`, `merchantId`,
`loanTypeId`, `numberOfPayments`, `flexibleNumberOfPayments`,
`tokenValidMinutes` and `progressValidMinutes` are required. They are checked
before a loan token is created at Teya or a payment is stored, so a missing
setting fails checkout without leaving anything behind.

The customer is sent to `loanPortalUrl` with the loan token as its last path
segment, e.g. `https://portal.example/consumerloans/{token}`. A trailing slash
on `loanPortalUrl` is optional and a query string on it is kept.

## Monitoring

`TokenValidMinutes` is the time allowed to open the loan URL.
`ProgressValidMinutes` is the time allowed to complete the loan after opening it.
Polling therefore covers the **sum** of those windows plus 30 seconds, rather
than stopping when the token-start window ends. With both settings at five
minutes, monitoring lasts up to ten and a half minutes and stops earlier when
Teya returns a final status.

Temporary status-request failures (network/client timeout, HTTP 408, 429 and 5xx)
retry at the normal polling interval within that same window. So does
completing an approved loan: a validation that fails for one of those reasons is
tried again, and a validated loan whose payment could not be recorded is
recorded again without validating it a second time. Other validation failures
are not retried. Completion failures send one email alert per loan.

When the loan is approved (`SUCCESS`) it is validated with Teya and a capture is
recorded on the payment, which completes the Ekom order. `CANCELED` is recorded
as a cancelled payment; `FAILED`, `TOKENEXPIRED` and `PROGRESSEXPIRED` are
recorded as a failed payment.

`Events.ActivityAsync` allows the site to forward start, status changes, payment
confirmation, errors and timeout messages to Ekom's order Activity log. Messages
include the payment attempt ID, not tokens or customer details. Use
`args.PaymentSettings.OrderCustomData["ekomOrderUniqueId"]` to find the backoffice
order; the payment attempt ID is different. Unsubscribe on application termination.

## Recovery after a restart

The in-memory queue and per-payment polling remain in place. Each payment is
stored on the Ekom order with the loan settings and the loan token, and with the
absolute success URL in its saved `PaymentSettings.SuccessUrl`. No table or
column is added.

After application startup, the worker makes one pass over the Teya payments
requested in the last seven days that have had nothing recorded since their
request (`IOrderPayments.GetOpenPaymentsAsync`). It resumes each one for the
remainder of its original token/progress window, calculated from the payment's
creation time. If the window has already elapsed, it makes one status request
instead of opening a fresh polling window:

- `SUCCESS`: validate the loan and record the capture.
- Canceled, failed or expired: record the cancelled or failed payment.
- Still processing: record a note on the payment asking staff to check the loan
  with Teya.
- Request failure: log the error and record nothing, so the payment is checked
  again at the next startup.

Anything recorded on the payment ends its recovery. A local timeout or shutdown
records nothing, so the payment is checked again at the next startup. Success
callbacks are not replayed. There is no recurring recovery scanner or change to
stock-reservation checks.

Loans started before payments were recorded on the Ekom order were marked with
`teyaConsumerloansPolling=pending` in their legacy payment row. Unpaid rows with
that marker are copied to Ekom at startup and then recovered like any other open
payment.

This implementation assumes repeated/concurrent Teya validation is safe, as agreed
for this change; that is **not confirmed by the published Teya documentation**.
There are deliberately no database locks, leases or heartbeats. Overlapping app
instances or slots sharing a database can check/validate the same unpaid attempt;
the capture is recorded once per payment.

A local timeout does not prove that a loan failed: check Teya before starting
another payment. A loan still processing at the overdue one-time check requires
manual review; this implementation does not keep polling it indefinitely.

Tests use fake time, simulated HTTP responses and a local SQLite database. Run:

```text
dotnet test Tests/Ekom.Payments.Tests/Ekom.Payments.Tests.csproj
```
