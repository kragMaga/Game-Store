# Imports, reconciliation, ageing, notifications, and dashboard

This implements selected features 1, 2, 4, 6, 7, 9, and 10. It adds API contracts
for a future frontend; the project still does not host a dashboard page. All routes
below start `/businesses/{businessId}` and require a current authenticated member.
Owner/Admin is required except stock reconciliation, which permits all active
members. Sensitive writes also require recent authentication and the auth limit.
Financial reports and exports remain private/no-store and tenant scoped.

## Excel onboarding imports

Four kinds: `products`, `customers`, `opening-stock`, `opening-debts`.

1. `GET /imports/{kind}/template.xlsx` downloads one empty worksheet with supported
   column headers. Fill it without renaming/removing headers.
2. `POST /imports/{kind}/preview` with raw XLSX bytes (not multipart or JSON).
   Response: kind, rowCount, the first twenty preview rows, errors with spreadsheet
   row numbers, and previewToken only when every row validates. Nothing is committed during preview.
3. Review errors, correct/re-upload, then `POST /imports/commit` with
   `{ "previewToken": "protected-value-from-preview" }`.

Preview tokens bind reviewed rows to the tenant/admin for 30 minutes. Commit checks
live authority and revalidates current data, so a concurrent sale/customer charge
can invalidate an opening preview. Every row, movement/charge, batch, and audit event
commits together or rolls back. Retrying a committed token returns the same batch,
including after expiry while its protection key remains available. Tokens cannot
be moved between tenants or reviewers. Do not log preview tokens or workbook bodies.

Limits: 2 MB compressed, 8 MB total declared uncompressed content, one worksheet,
1,000 data rows, 30 columns, 500 characters per cell, and 1 MB serialized preview
contents. Split larger workbooks. At most 100 row errors are returned per preview.
ZIP structure/XML parsing are bounded; external references, XML entities, formulas,
macros, and spreadsheet error cells are rejected. Formula-like literal text remains
text. Numbers use a decimal point without thousands separators. Dates use
`yyyy-MM-dd`; ordinary Excel numeric date cells are also accepted. Times and
fractional date serials are rejected. Imported quantities/prices use existing
precision and whole-unit rules; debt amounts match business currency precision.

Product columns: Sku, Name, StockUnit, AllowsFractionalStock (true/false),
ReorderLevel, DefaultSellingPrice, OpeningQuantity, EffectiveDate, Reason.
Create new SKUs only; optional opening quantity requires a date and reason.
Default selling price is optional and per BASE unit.

Customer columns: Code, Name, Email, Phone, OpeningDebt, EffectiveDate, DueDate,
Reason. Create new codes only. Optional opening debt requires effective date and
reason; due date is optional and cannot precede the effective date.

Existing-product opening columns: Sku, Quantity, UnitName, EffectiveDate, Reason.
Existing-customer opening columns: Code, Amount, EffectiveDate, DueDate, Reason.
Opening stock requires no prior movements; opening debt requires no prior charges.
Codes/SKUs and named units must resolve within the business. Repeated codes/SKUs in
one workbook are rejected. Imports never update existing profiles, revalue completed
transactions, silently merge duplicate records, or send customer messages.

## Stock reconciliation

`GET /reports/stock-reconciliation?from=YYYY-MM-DD&to=YYYY-MM-DD&page=1&pageSize=20`
returns a paged report table with:

- Opening stock before the start date.
- Initial opening entries inside the period.
- Purchases, sales deductions, sale restocks, purchase returns, and adjustments.
- Closing = opening plus all signed period movements.

All quantities are in each product's base unit. Damaged/non-restockable returns
have zero stock effect. Archived products remain visible. The range is inclusive
and cannot end in the future business-local date. The matching Excel endpoint is
`GET /reports/exports/stock-reconciliation.xlsx?from=...&to=...` (10,000 products max).

This is an effective-date movement reconciliation. Backdated entries can change a
past report, and can produce a negative intermediate effective-date balance despite
nonnegative posting-order stock. It is not FIFO costing or an immutable historical
snapshot. Movement posting sequences retain the original running-balance evidence.

## Debt ageing

`GET /reports/debt-ageing?page=1&pageSize=20` returns business-local asOf, currency,
whole-report totals, and paged customer buckets: Unscheduled, Current, Days1To30,
Days31To60, Days61To90, Over90, Total. The Excel version is
`GET /reports/exports/debt-ageing.xlsx`, limited to 10,000 customers.

Buckets use each charge's due date and current outstanding amount after receipts,
reversals, charge corrections, and refunds. Due today is current; yesterday is one
day overdue. No due date is unscheduled, not overdue. Fully settled charges are
excluded; archived customers with unpaid debt remain included. Totals cover all
customers, not just the selected page. This is current ageing, not an as-of-date
historical statement. Existing unknown-payment sales remain unreconciled.

## Suggested selling prices

Product create/update DTOs accept nullable `defaultSellingPrice`, stored with six
decimals per BASE stock unit and returned on product lookup and inventory exports.
Null clears the suggestion. A pack suggestion can be calculated as base price times
that pack's conversion factor. Price suggestions do not automatically fill the sale
request: clients still submit the actual per-entered-unit selling price. Different
sale prices remain valid, and changing the default never reprices past sales.

## Internal reminders and dashboard

`GET /dashboard` returns local date/currency, today's sales/returns/net sales,
current customer debt, an overdue-debt reminder count/amount, low-stock count and
first ten products, ten recent audit activities, and failed/expired notification
count. Owner/Admin only. Reminder values are live, even if email is disabled,
previous email attempts failed, or today's dispatch was already queued. Customer records are not used to select email recipients; the business configures
its own internal notification addresses.

`GET /settings/debt-reminders` reads settings. `PUT /settings/debt-reminders`:

```json
{
  "emailEnabled": true,
  "additionalRecipients": ["finance@example.com"]
}
```

Emails are opt-in (default off). Up to twenty additional valid addresses are
allowed and deduplicated case-insensitively. When enabled, reminders go to the explicitly specified addresses. Enabling email
requires at least one address. Dashboard reminders are visible only to Owner/Admin;
email addresses can belong to an owner/admin or an external accounts contact chosen
by the business. The system does not automatically add account/customer addresses.

The shared alert worker is controlled by `LowStock:Enabled` globally; this legacy
configuration name now governs both alert types. Business-specific toggles are
independent. Scheduling uses the business's existing local notification time/timezone. One
business/local-day marker deduplicates overdue-reminder enqueueing. No overdue
debt means no email. The email is an internal summary with customer count, overdue
amount/currency and dashboard guidance; it lists no customer contact details.
The SMTP worker rechecks tenant openness, current notification settings and configured
recipients before sending. Removed recipients are cancelled;
explicit additional-recipient settings remain authoritative. Closure stops both
alert types; reopening leaves both email toggles disabled until explicitly enabled.

## Notification history and retry

`GET /notifications?page=1&pageSize=20` shows low-stock and overdue notifications,
recipient, local date, queued UTC time, status, cumulative attempts, completion UTC,
safe failure type, and canRetry. History starts with this migration; old outbox
messages are not guessed to belong to a tenant. Auth confirmation/reset emails are
never included. Sent means SMTP transport accepted the send, not verified mailbox
receipt. SMTP delivery remains at least once if a worker crashes after transmission.

`POST /notifications/{notificationId}/retry` requires Owner/Admin and recent proof.
Only Failed/Expired business notifications less than seven days old can be retried.
Pending/Sent/Cancelled notifications cannot. Retry queues a new outbox message with
the original dated contents and preserves cumulative attempt history. It does not
send directly or let an admin select arbitrary outbox IDs/auth payloads. Automatic
attempts continue using the existing outbox backoff and ten-attempt limit per queue
entry. A stale lease holder cannot overwrite another worker's notification outcome.

Retry contents are encrypted separately and cleared after seven days. Operational
notification metadata/day markers retain ninety days through bounded cleanup.
Committed import batches, financial movements, and audit evidence remain retained.

## Migration and verification

`AddBusinessInsightsAndImports` adds BusinessNotifications, DebtReminderDispatches,
InventoryImportBatches; optional base selling prices; and reminder settings defaulting
to disabled/no extra recipients for existing businesses. It preserves inventory,
customer balances, and old auth email behavior. Backup verification now requires
30 required inventory/authentication/migration tables after
[legacy retirement](legacy-retirement.md), including document sequences and business units of measure. Tests
use isolated SQLite databases; the developer's live database is not migrated here.
