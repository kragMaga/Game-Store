# Customers, opening debts, and payments (phase 4)

For the completed returns, corrections, daily alerts, exports, and lifecycle contract,
see [inventory operations](inventory-operations.md). References to later phases below
describe this document’s original delivery boundary.


This batch adds customer profiles, onboarding debts, named credit sales, payment
allocations, statements, outstanding/overdue reports, and administrative payment
reversals. Payments document money received outside the application; no gateway,
bank settlement, refunds, general ledger, or tax accounting is introduced.

## Customer identities and permissions

A Customer is a business record, not an Identity login. Codes are stable,
case-insensitively unique within the business, and may repeat in other businesses.
Names, contact details, and active status are admin-editable. Sale records preserve
the customer name captured at posting. Inactive customers cannot receive new
sales, but their debts remain visible and payable. No hard-delete endpoint exists.

| Operation | Permission |
| --- | --- |
| Look up customer profiles | Owner/Admin/Staff |
| Create/update/archive customers, enter opening debt | Owner/Admin, recent proof |
| Record sale and payment received during that sale | Owner/Admin/Staff |
| Record later payments; read customer statements/payment details | Owner/Admin, or Staff with CanRecordCustomerPayments |
| Business-wide debtor report | Owner/Admin |
| Reverse a mistaken named-customer payment | Owner/Admin, recent proof |

Payment permission is checked from current membership inside the writer transaction.
Revoking it takes effect without requiring logout. It never grants reversal/refund
authority. Viewers have no customer-finance access. Existing tenant/global-role
boundaries and private/no-store response rules apply to every new endpoint.

## Opening balances

Record existing customer debt once, before any other charges for that customer.
Use an explicit effective date, reason, optional due date, amount, and idempotency
key. A zero opening debt is allowed and still counts as the customer's opening
entry. Outstanding balances are never entered directly on Customer itself.

Opening entries and completed financial records are append-only. Incorrect opening
debt correction will need a separate charge-adjustment workflow; there is no edit or
delete that could silently change history. Retry the original entry with the same
key/payload to retrieve it; a new key cannot add a second opening debt.

## Sale payment contract

The existing sale body now accepts:

```json
{
  "effectiveDate": "2026-10-26",
  "lines": [
    { "productId": "product-guid", "unitId": "unit-guid", "quantity": 2, "unitPrice": 500 }
  ],
  "idempotencyKey": "credit-sale-001",
  "customerId": "customer-guid",
  "amountPaid": 400,
  "paymentMethod": "Cash",
  "dueDate": "2026-11-02"
}
```

This creates a 1000 charge, records 400 received, and leaves 600 owed. The payment,
charge, sale snapshots, stock deductions, and audit event commit together. A customer
is mandatory for any unpaid amount. For a walk-in sale omit customerId and supply
the full amountPaid; overpayment is rejected. A zero-price walk-in sale needs no
receipt. Omitted/null amountPaid means zero received, never an assumption of payment.
Positive receipts require Cash, BankTransfer, Card, or Other. Purchase receipts
reject customer/payment/due-date fields; supplier payments are outside this phase.

Due dates are optional and cannot precede the effective date. Transactions cannot
be future-dated in the business timezone or predate an existing customer opening
debt. Sale input prices keep their existing six-decimal/line-rounding policy, but
opening debts/payment amounts must ALREADY match the business currency precision:
for a two-decimal business, 100.001 is rejected rather than rounded.

Sale summaries now include customerId/name snapshot, paymentsTracked, amountPaid,
and outstanding. Paid/outstanding are calculated from currently effective allocations,
including later receipts and reversals. Historical headers/prices are not rewritten.
Existing phase-3 sales stay paymentsTracked=false with null paid/outstanding. Their
financial status is unknown, not automatically paid or overdue. Do not repost old
sales to add payment data: that would duplicate inventory effects. Legacy payment
reconciliation is a separate migration decision.

## Later receipts and allocation

Retrieve a permitted customer statement to obtain charge IDs. Allocate each receipt
explicitly to one or more opening/sale charges belonging to that same customer:

```json
{
  "amount": 600,
  "effectiveDate": "2026-10-26",
  "method": "BankTransfer",
  "allocations": [ { "chargeId": "charge-guid", "amount": 600 } ],
  "idempotencyKey": "bank-receipt-001",
  "reference": "External transfer reference"
}
```

All parts must be positive, use distinct charge IDs, sum exactly to amount, and not
exceed remaining debt. There is no unallocated credit or overpayment yet. Receipts
cannot predate their charges or be future-dated. Currency/precision must match.
Writer transactions serialize competing payments, preventing the same outstanding
amount being settled twice. Idempotent retries return the same receipt; changing
the customer, allocations, date, method, reference, or amount under that key gives 409.
Allocation order is immaterial. Payment correction does not change stock.

## Balances, overdue amounts, and corrections

Current outstanding = opening debt + recorded customer sales - effective allocated
payments. Net payments exclude reversed receipts. Overdue includes unpaid charges
whose due date is earlier than today in the business timezone; no due date means
outstanding but not classified as overdue. A charge due today is not overdue yet.
The report is a current balance, not a historical as-of-date or accounting statement.
Archived customers remain in the debtor report until their debt is settled.

Admins can reverse an incorrectly recorded named-customer payment in full using a
reason, effective date, and idempotency key. Original payment/allocations remain;
the reversal restores their debt contribution and marks the receipt reversed. A
second reversal under a different key is rejected. Reversal dates cannot predate
the payment. This records correction of a receipt, not an actual money refund.
Walk-in payment reversals are blocked because they would create debt without a
customer; those need the later sale-return/reversal workflow. Partial reversals,
refunds, charge adjustments, and customer credits remain later correction work.

## Routes

Prefix with `/businesses/{businessId}`. Lists use the established page/pageSize
bounds. Customer search is capped at 100 characters; codes use the SKU-style ASCII
letters/digits/period/underscore/hyphen rule (1-64 characters).

| Method and route | Purpose |
| --- | --- |
| POST /customers | Create customer profile; 201 |
| GET /customers | Paged lookup; search and includeInactive options |
| PUT /customers/{customerId} | Edit profile/active status; 204 |
| POST /customers/{customerId}/opening-debt | Record existing debt once |
| GET /customers/{customerId}/statement | Balance totals and paged charge history |
| POST /customers/{customerId}/payments | Allocate a later receipt |
| GET /customers/{customerId}/payments | Paged receipts, including reversal flags |
| GET /customer-payments/{paymentId} | Receipt, allocations, and reversal details |
| POST /customer-payments/{paymentId}/reverse | Admin-only receipt correction |
| GET /reports/customer-balances | Paged customers with positive debt, including inactive customers |

The normal inventory-write budget covers later receipts; customer configuration,
opening debt, and reversals use recent authentication and the auth baseline. Sales
retain their existing ordinary posting budget and do not require a separate late-
payment grant for money recorded with the sale itself.

## Schema, migration, and tests

`AddCustomerLedger` adds Customers, CustomerCharges, CustomerPayments,
CustomerPaymentAllocations, CustomerPaymentReversals, and nullable customer identity
snapshots on sale headers. Composite tenant foreign keys/default-deny filters/write
guards cover the ledger; completed charges/payments/allocations/reversals cannot be
edited or deleted through normal tracked persistence. Administrative writes append
audit events without logging contact/payment bodies.

The migration deliberately does not fabricate old payment history. An explicit
downgrade removes ledger data and customer snapshots; backup/restore a compatible
build rather than downgrading a production business with financial history casually.
Maintenance verification now requires 25 application tables. Restore checks include
opening debt, a partial sale receipt, a later receipt, its reversal, and the resulting
balance in addition to stock, prices, Identity, and email outbox data.

Tests cover debt derivation/overdue calculation, splitting payments across debts,
permissions and immediate revocation, tenant/customer isolation, archived-customer
settlement, walk-in/overpayment rules, immutable history, idempotent retries,
concurrent settlement, precision/date validation, rollback on sale/payment/reversal
failures, and migration preservation of financially untracked legacy sales.
