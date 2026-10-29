# User-facing backend workflows

This adds readable document numbers, searchable transactions, entry previews,
customer statement/receipt downloads, and onboarding status for a future frontend.
The frontend still owns walkthrough screens, confirmation dialogs, and help/support
interfaces. No frontend or support-ticket system is introduced here.

All routes below start `/businesses/{businessId}` and require authentication/live
membership in an open business. Existing private/no-store response rules apply.

## Document numbers

New and existing documents receive business-specific references:

| Document | Example |
| --- | --- |
| Sale | SALE-000001 |
| Purchase | PUR-000001 |
| Payment (at sale or later) | PAY-000001 |
| Stock/financial correction or return | COR-000001 |
| Mistaken-payment reversal | REV-000001 |

Numbers are returned on their respective summaries/details and transaction/
correction exports. Original GUIDs remain the API identity; external reference
fields remain unchanged. Counters are independent by business and family. Another
business can also have SALE-000001; a number alone proves no access. There is no
yearly reset. Width is a minimum of six digits, expanding as needed.

Allocation happens under the existing SQLite writer lock and in the same
transaction as stock/ledger posting. Concurrent postings cannot receive the same
number. Failed writes and previews roll the counter back; idempotent retries return
the original number. Completed documents retain their numbers when corrected.
Corrections/reversals receive their own references rather than renumbering originals.

`AddDocumentNumbersAndUserWorkflows` backfills existing documents ordered by
RecordedAtUtc and GUID within each business/family and seeds the counters. It
preserves IDs, external references, stock, prices and ledger values. This is an
internal record reference scheme, not a claim of jurisdiction-specific invoice
numbering/tax compliance. A schema downgrade removes numbers/counters; restoring a
compatible backup is preferable to casually downgrading financial history.
Current backup verification requires 30 inventory/authentication/migration tables,
including document counters; new backups contain no retired catalog archives.

## Search and filters

`GET /sales` and `GET /purchases` retain page/pageSize and now accept:

- `from`, `to`: inclusive effective dates, formatted yyyy-MM-dd.
- `productId`, `customerId`, `supplierId`: matching tenant record IDs.
- `staffUserId`: the user who recorded the transaction.
- `search`: literal substring of document number, external reference, or recorded
  customer-name snapshot. Maximum 100 characters; SQL case folding applies.

Filters combine with AND and are applied before paging/counting. Invalid reversed
date ranges return 400. Foreign/missing product/customer/supplier filters return no
matching rows; they never reveal records from another business. Owner/Admin can
search all tenant documents; Staff continue to see only their own. Selecting another
staff ID does not grant cross-staff visibility. Viewers cannot read financial entries.

`GET /customers/{customerId}/payments` accepts from, to, staffUserId, search, page,
pageSize. Search matches document number/external reference. Existing payment
permission is required: Owner/Admin or Staff with the live later-payment grant.
Reversed receipts remain listed with isReversed=true. Page/offset caps remain intact.

## Entry previews and likely duplicates

Use `POST /sales/preview` or `/purchases/preview` with the same body as the existing
posting route. Use `POST /customers/{customerId}/payments/preview` with the normal
allocated payment body. Previews use the normal inventory-write rate limit.

The service executes the exact posting validation in a transaction, reads its
calculated result, and rolls back. No stock, ledger, audit, document or counter
changes persist. The response includes calculated totals/lines or payment allocations,
`alreadyRecorded`, and up to five `possibleDuplicates` with permitted document IDs,
numbers, dates and external references.

Transient IDs are Guid.Empty and documentNumber is null so a preview cannot be
mistaken for a committed receipt. Retrying an already committed idempotency key
reports alreadyRecorded=true and returns its real document. The original operation's
visibility still applies; other staff cannot use a guessed key to inspect it.

Sale/purchase warnings match the same canonical payload or the same nonblank
external reference, with reference case folding. Payments warn for the same customer,
date, amount and method or a matching reference, excluding reversed receipts.
Warnings are advisory: a legitimate repeated sale/payment may have similar values.
They are not a guarantee that every duplicate is detected. Staff warnings expose only
sales/purchases they may already read; finance-permitted receipts follow their
existing customer-ledger visibility.

A frontend should display the preview and warnings, ask the user to review, and
then call the existing POST endpoint with the SAME idempotency key/payload. A preview
is not a reservation or authorisation token. POST rechecks current stock, debt and
permissions; another user's posting can make a previously valid preview fail.
Existing API clients can still post directly. Confirmation dialogs are a UI concern;
these endpoints supply the data needed to implement them.

## Customer statements and payment receipts

- `GET /customers/{customerId}/statement.xlsx`: Excel statement download.
- `GET /customers/{customerId}/statement.html`: printable HTML statement.
- `GET /customer-payments/{paymentId}/receipt.html`: printable payment receipt.

Statements show recorded opening debt/sales, receipts, mistaken-payment reversals,
charge corrections/returns, and actual refund records. Each event has date, type,
readable reference, description, currency, debit, credit and running balance.
Debits increase debt; credits reduce it. The ending balance reconciles with the
current ledger. Dates are effective dates, with recorded-time/type/ID tie breakers.
Backdated entries may change past running balances. Only recorded ledger events
are shown; unknown legacy payment history is not invented.

A short transaction collects a consistent snapshot before rendering. Downloads are
limited to 10,000 events; oversized histories return 413 with guidance to use the
paged ledger endpoints. Archived customer balances remain available. Statements
require Owner/Admin or a Staff member with live customer-payment permission.

Receipts identify the payment number, date, method, amount/currency, external
reference, customer, and allocations to sale/opening debt. Reversed receipts are
clearly marked REVERSED rather than presented as effective payments. A receipt
records the original payment; later refunds/corrections are shown on the customer
statement. It does not process payments or serve as a tax invoice.

Owner/Admin and finance-permitted Staff keep their existing receipt visibility.
Ordinary Staff can also download receipts for payments taken on their own sales;
this grants no access to other receipts or customer statements. A separate staff
member cannot obtain the download by guessing the payment ID.

HTML downloads have attachment headers, private/no-store, a restrictive content
security policy and nosniff. Every dynamic value is HTML encoded; documents contain
no scripts/external assets. Open them in a browser to print or use the browser's
Save as PDF facility. These endpoints do not generate PDF bytes. Excel uses the
existing bounded XLSX writer with literal text cells, not formulas.

## Onboarding status

`GET /onboarding-status` is Owner/Admin only. It returns factual counts/flags for
products, opening stock, customers, opening debts, additional active members,
unexpired pending invitations, recorded purchases/sales, and email settings.

Optional steps are labelled optional. There is deliberately no completion percentage
requiring invented opening stock, customer debt, or unnecessary staff. A business
can start legitimately with no stock/debt/employees. The frontend can show these
facts in a walkthrough; it owns instructional text, progress presentation and help
screens. Configuration presence does not certify real SMTP delivery or deployment
readiness. Onboarding status makes no external service calls.
