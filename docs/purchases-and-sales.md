# Purchase receipts and manual sales (phase 3)

For the completed returns, corrections, daily alerts, exports, and lifecycle contract,
see [inventory operations](inventory-operations.md). References to later phases below
describe this document’s original delivery boundary.


Businesses record purchases/sales completed outside this API. Receipts add goods;
sales remove goods. This phase does not process money, create invoices, maintain
customer debt, or assume that a recorded sale has been paid. Customer/payment
tracking follows in phase 4; corrections/returns follow in phase 5.

## Posting rules

- Owner, Admin, and Staff can post receipts and sales. Viewers cannot.
- Each transaction contains 1-100 ordered lines. Each line identifies a tenant
  product, one of that product's units, a positive quantity, and actual unit price.
- UnitPrice is per ENTERED unit: two crates at 10000 per crate total 20000,
  independently of how many base bottles each crate contains.
- Prices are nonnegative and retain up to six decimal places. Free goods are
  allowed; negative prices and excess precision are rejected.
- Stock quantity conversion/indivisibility rules remain unchanged. Every line
  snapshots product name, SKU, unit name, conversion factor, quantity, and price.
- Duplicate product lines are allowed, for example different selling prices or
  units. They share a running balance; splitting lines cannot bypass stock checks.
- Negative stock is rejected. Receipts that exceed the supported balance fail.
- EffectiveDate is required, cannot be future-dated in the business timezone, and
  cannot predate an existing opening balance for an affected product. Entry UTC
  time and actor are recorded separately.
- Backdating does not recalculate earlier balances: stock follows posting order,
  while EffectiveDate describes when the external transaction happened. This is
  not a historical-stock valuation or FIFO costing engine.
- A receipt can be the first movement of a product starting at zero; a separate
  opening is optional. Once other movements exist, opening stock is no longer allowed.
- Products must be active when posting. Existing records and valid idempotent
  retries remain readable after product archival or settings changes.

A SQLite writer transaction serializes posting before authorization and stock
reads. Header, lines, stock balances, movements, and audit event commit together.
Failure on a later line or database write rolls back everything. Per-product
SequenceNumber preserves exact movement ordering even when timestamps tie. Sale
movements have negative input/base quantities; document line quantities stay positive.

## Money and business configuration

CreateBusinessDto now accepts `currencyDecimalPlaces` (0-4, default 2). It is
immutable through settings, like currency itself, and copied onto each document.
Existing businesses receive precision 2 during migration. Choose the appropriate
precision for your currency at onboarding; the setting is explicit and is not an
automatic ISO currency registry lookup. Review existing businesses before monetary
posting if their currency needs a different precision; no casual in-app rewrite of
historical money is supported.

Unit prices are exact scaled integers with six decimal places. For each line,
multiply entered quantity by unit price, round once to declared currency precision
using midpoint-away-from-zero, and sum rounded line totals. Header/line totals use
integer minor units. For example, at precision 2 a line amount of 0.005 becomes
0.01. This is the initial product rounding policy, not jurisdiction-specific tax
or accounting advice. Transaction amount is capped at 9,000,000,000 currency units.

There are no taxes, discounts, currency conversion, costing/profit calculations,
payment amounts, supplier debts, or credit balances in this phase. Actual prices
are entered on each transaction; product default pricing remains future convenience.

## Endpoints and visibility

Prefix routes with `/businesses/{businessId}`. All require a valid bearer session
and current membership. Lists use the existing page/pageSize bounds.

| Method and route | Behaviour |
| --- | --- |
| POST /purchases | Record a receipt with optional supplier/reference; 200 |
| GET /purchases | Paged receipt summaries |
| GET /purchases/{transactionId} | Header and ordered line snapshots |
| POST /sales | Record manual sale; SupplierId must be absent; 200 |
| GET /sales | Paged sale summaries |
| GET /sales/{transactionId} | Header and ordered line snapshots |
| POST /suppliers | Owner/Admin creates tenant supplier name and optional contact reference |
| GET /suppliers | Paginated suppliers for users who can record transactions |

Owner/Admin can read all business documents. Staff can read only documents they
entered, plus supplier lookup needed for entry. Viewers retain inventory access
but cannot read financial documents/suppliers. Future reporting permissions can
extend this deliberately. Guessed IDs, foreign units/suppliers/products, and global
admin claims cannot bypass the business boundary. Responses use no-store.

Example purchase (replace GUIDs):

```json
{
  "effectiveDate": "2026-10-26",
  "lines": [
    {
      "productId": "product-guid",
      "unitId": "crate-unit-guid",
      "quantity": 2,
      "unitPrice": 10000
    }
  ],
  "idempotencyKey": "purchase-delivery-001",
  "supplierId": null,
  "reference": "External delivery reference"
}
```

The sale body has the same shape, with no supplier. Use actual selling prices;
the API calculates amounts and stock effects rather than accepting client totals.

Idempotency keys are unique across purchases/sales within the business. The same
key with the same normalized payload returns the original document without moving
stock again. Kind, effective date, supplier, reference, and ordered line quantities/
prices participate in the fingerprint. Changing any of them gives 409. Another
staff member cannot use a guessed key to retrieve the original document; admins
can inspect/retry business documents. Opening-stock keys are a separate operation
namespace, and generated line movement keys are internal.

Posting uses a separate `inventory-write` budget, default 120 requests per minute
per business and trusted connection IP. Configure RateLimits:InventoryWritePermits
and InventoryWriteWindowSeconds; invalid/nonpositive budgets fail startup. This
avoids reusing the much smaller authentication budget for routine sales. It remains
per-process; existing shared-gateway requirements still apply to multiple instances.
Supplier creation retains recent-authentication and auth-baseline protection.

## Schema and verification

`AddPurchasesAndSales` adds Suppliers, InventoryTransactions, InventoryTransactionLines,
Business.CurrencyDecimalPlaces, and StockMovement.SequenceNumber. A data backfill
assigns posting sequence to existing movements before adding the unique index.
It preserves existing IDs, quantities, and legacy catalog/search tables. Normal
startup applies it; development tests do not modify the live database.

Completed documents/lines cannot be edited/deleted through tracked EF writes.
There are deliberately no update/delete endpoints. A later controlled reversal
must preserve original financial and stock history. Composite tenant foreign keys,
default-deny filters, and write guards cover the new tables as well.

Backup verification now requires all 20 application tables. Restore tests include
a recorded sale, line-price snapshots, signed movements, and final stock balance.
Posting tests cover variable prices, repeated products, pack conversions, rounding
including precision zero, cross-tenant relationships, permissions/revocation,
idempotent retries, concurrent overselling, later-line/database rollback, migration
backfill on existing stock, invalid values/dates, and inventory rate limits.
