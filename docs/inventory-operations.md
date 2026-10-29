# Corrections and operations

For newer Excel imports, reconciliation, debt ageing, notification delivery history,
internal business reminders, suggested prices and dashboard APIs, see
[business insights and imports](business-insights-and-imports.md).

This completes the agreed API scope. All business routes require authentication
and live membership. Financial reports and corrections require Owner/Admin;
inventory and movement reports allow active members including Viewers. Sensitive
correction and lifecycle writes also require recent authentication (10 minutes).
Ordinary staff can continue posting sales/purchases and, when granted, later
customer payments. They cannot return goods, issue refunds, adjust stock, or count.

## Returns, reversals, and stock counts

Use `POST /businesses/{businessId}/transactions/{transactionId}/returns`:

```json
{
  "effectiveDate": "2026-10-26",
  "reason": "Customer returned two damaged items",
  "idempotencyKey": "return-external-reference-001",
  "lines": [{ "transactionLineId": "original-line-guid", "quantity": 2, "restockable": false }],
  "refundAmount": 500,
  "refundMethod": "Cash"
}
```

Quantities use the original line's unit and conversion. For a full reversal of
what remains after earlier partial returns, omit lines and send
`reverseRemaining: true`. Remaining units are treated as restockable; use explicit
lines for damaged returns. No transaction or original movement is edited.
Cumulative quantity cannot exceed the original line; cumulative rounded credits
cannot exceed the original line amount. The final partial return consumes any
rounding remainder. Purchase returns reduce stock and fail if insufficient stock
remains. Supplier cash/debt/refunds are outside this inventory scope.

Sale returns reduce the original customer charge. Credit reduces outstanding debt
first. If it exceeds outstanding debt, `refundAmount` must equal the excess exactly
and a Cash/BankTransfer/Card/Other method is required. This records a refund made
outside the system; no payment processor is called. Fully paid walk-in returns
therefore require a full corresponding refund. Refunds reduce net receipts. No
unallocated customer credit is created. Legacy sales with unknown payment history
are blocked from returns until explicitly reconciled; historical payments are not
invented. Receipts on refunded charges cannot subsequently be reversed.

`POST /products/{productId}/stock-adjustments` accepts signed `quantity` in the
base stock unit, `expectedQuantityOnHand`, effectiveDate, reason, idempotencyKey.
Use a negative quantity for damage/loss. `POST /products/{productId}/stock-counts`
uses the same shape with nonnegative `quantity` representing the physical count.
Both compare expected stock under the writer lock and reject stale counts with
409. They enforce whole-unit rules and nonnegative/bounded resulting stock.
Counts with no difference still record evidence. Effective dates cannot be future
business-local dates or predate opening stock.

`POST /customer-charges/{chargeId}/correct-opening-debt` accepts amount (new total),
effectiveDate, reason, idempotencyKey, optional refundAmount/refundMethod. It
appends a delta to an opening debt; it never edits its initial value. The same
refund rule applies if the corrected total falls below money already received.
Financial corrections cannot predate related receipt/correction history.

`GET /corrections` pages headers; `GET /corrections/{id}` returns lines, original
references, stock deltas, balances, charge delta, and refund method. All paths in
this section use the `/businesses/{businessId}` prefix. Idempotent retries with the
same normalized payload return the same result; changed payloads yield 409.
Header, movements, updated stock, charge changes/refunds, and audit commit together.

## Daily low-stock notification

`LowStock:Enabled` defaults true. A background worker scans enabled open businesses
once per minute in bounded pages. The business timezone, daily local time, email,
and toggle come from settings. Active products whose on-hand quantity is at or
below their reorder level qualify; archived products do not. A business/local-date
primary key prevents duplicate enqueueing, including after restart or concurrent
workers. A day with no qualifying goods records a scan and sends nothing. Stock
that becomes low after that scan appears the next day. A failed enqueue rolls back
the marker, allowing retry. Only the current local day is processed, with no stale
backlog of missed days. DST uses local date/time; an absent scheduled clock time is
handled on the first later scan. Only one enqueue occurs during a repeated hour.

Each message lists up to 500 items and reports any additional count. Production
uses the existing encrypted durable outbox in the same transaction. SMTP retries,
expiry, protected key persistence, and at-least-once delivery semantics remain
those of the outbox. A dispatch means queued/scanned, not proof of delivery.
Retired dispatch markers retain 90 days; expired invitations retain 30 days before
bounded cleanup. Financial and authority audit history is not pruned by that job.

## Reports and Excel

Prefix with `/businesses/{businessId}/reports`:

- `GET /summary?from=YYYY-MM-DD&to=YYYY-MM-DD`: sales/purchases, returns, net totals,
  recorded receipts, reversed receipts, refunds, net receipts, and low-stock counts.
- `GET /tables/{report}?page=1&pageSize=100`: inventory, movements, sales, purchases,
  customers, or corrections. Standard page/offset bounds apply.
- `GET /exports/{report}.xlsx`: the same report as Excel, capped at 10,000 rows.
  Larger exports return 413; narrow date ranges or use the paged API.

Date filters are inclusive effective dates, independently applied to documents,
returns, receipts, reversals, and refunds. A month's returns can relate to older
sales; period net totals can therefore be negative. Product counts and customer
balances are current. Customer balances reject date filters rather than pretending
to be an as-of report. Original sale totals stay unchanged in sales tables; current
net paid/outstanding reflect corrections. Use corrections/summary alongside them.
Untracked historical sales are explicitly counted and have no assumed paid balance.
No tax, profit, cost-of-goods, stock valuation, or supplier payable is calculated.

Reports require live tenant access and explicit tenant predicates; financial
reports require Owner/Admin. Files and JSON receive private/no-store headers.
Exports use the inventory-write budget. User strings are XLSX inline text, never
formulas; XML is escaped, invalid control characters are removed, and cell strings
are bounded to Excel's length limit. Numeric cells use invariant decimal values.
Excel's numeric precision limits still apply when inspecting extreme quantities.

## Onboarding and closure

Within `/businesses/{businessId}`:

- `GET /invitations`: paged administrative metadata, never token hashes.
- `DELETE /invitations/{id}`: cancel an unused invite. Only owners manage admin invites.
- `GET /audit-events`: paged administrative authority/operation history.
- `POST /transfer-ownership` with `{ "newOwnerUserId": 123 }`: current owner only,
  active confirmed member only, one owner preserved atomically. Previous owner is Admin.
- `POST /close` or `/reopen` with `{ "confirmBusinessName": "Exact current name" }`:
  owner only. Closure cancels pending invites, stops alerts, and blocks ordinary
  tenant access. History remains. Reopening leaves notifications off until opted in.

The active owner can still use report/export routes while the business is closed;
other members cannot. Business lists include closedAtUtc. Closing is reversible
until account deactivation; transfer ownership before closing an account if somebody
must retain access to a business. `POST /auth/close-account` requires recent proof;
open owned businesses must first be transferred/closed. It marks the account closed,
rotates its security stamp, revokes sessions, locks login, and deactivates memberships.
Password reset and email confirmation cannot reactivate it. This preserves identities
referenced by financial records; it is not automatic personal-data erasure.

The old catalog/global-user routes and compatibility flags are removed. See
[legacy retirement](legacy-retirement.md) for the removal migrations.

## Migration and deployment

`CompleteInventoryOperations` adds four tables and nullable business/account closure
markers; earlier migrations remain intact. Normal startup applies pending migrations.
No live developer database was updated during implementation: tests use isolated
SQLite databases. Backup verification now expects 30 required inventory/authentication/migration
tables. New backups contain no retired catalog archives; existing backup files are unchanged.

Before real deployment, supply SMTP credentials and persistent protected Data
Protection keys, configure trusted proxies, schedule private backups and rehearse
restoration, and check daily-job/outbox health on the actual host. Multi-instance
rate limiting still requires a shared gateway/distributed policy as documented in
the authentication deployment notes. Running the tests does not exercise a real
SMTP provider or hosting infrastructure. No frontend, ERP, payroll, gateways,
multi-location stock, taxes, supplier balances, or costing/profit engine is included.
