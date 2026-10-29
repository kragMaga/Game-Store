# Inventory foundation (phase 2)

For the completed returns, corrections, daily alerts, exports, and lifecycle contract,
see [inventory operations](inventory-operations.md). References to later phases below
describe this document’s original delivery boundary.


This phase adds tenant products/categories, product-specific units, opening stock,
and paginated movement history. [Purchase receipts and manual sales](purchases-and-sales.md)
are now implemented in phase 3; customers, payments, and daily reorder-email
scheduling remain later phases. Existing Games/Genres stay
separate; no legacy stock or prices are silently assigned to a tenant.

## Permissions and tenant safety

Any active business member can read inventory. Only Owner/Admin can create
categories/products/units, edit product configuration, or record opening stock.
Configuration writes also require recent authentication and the auth rate-limit
baseline. Staff purchase/sale entry will use separate guarded routes in phase 3.

All inventory primary keys include BusinessId. Category and unit relationships use
composite foreign keys so another tenant's GUID cannot be attached to a product.
InventoryService checks current membership on every public operation, then selects
the scoped DbContext's InventoryBusinessId. Without that scope, global filters
return no inventory rows. Tracked writes must match the scope too. EF bulk SQL and
IgnoreQueryFilters bypass these guards and must not be introduced without explicit
tenant checks. Query filters are an additional boundary, not authorization alone.
Private responses inherit the business routes' no-store headers.

## Quantities and units

Public quantities are decimals. SQLite stores integer millionths (scale 1,000,000),
with at most six decimal places and a maximum base balance of 9,000,000,000 units.
Never convert these quantities through floating-point values. Requests that exceed
limits or produce a conversion requiring greater precision are rejected, not rounded.

Each product has a base stock unit and an automatically created base-unit option
with conversion factor one. Other unit factors belong to that product only:

| Product | Base unit | Additional unit | Factor |
| --- | --- | --- | --- |
| Bottled drinks | Bottle | Crate | 24 |
| Rice | Kilogram | Bag | 50 |
| Cable | Metre | Roll | 100 |

Product-level AllowsFractionalStock controls indivisibility in the base unit.
Each unit's AllowsFractionalQuantity independently controls whether a fractional
pack can be entered. A half crate of 24 bottles can mean 12 bottles if enabled;
half a bottle is rejected when bottles are indivisible. Both input and converted
base quantities are validated. Stock-unit meaning, fractional-stock rules, SKU,
and unit factors cannot be edited through configuration routes. To change a pack
size, add a differently named unit; history preserves the original unit and factor.
Movement and unit history are append-only through tracked EF persistence.

ReorderLevel uses base units. Active stock is low when QuantityOnHand <= ReorderLevel;
inactive products do not appear in lowStockOnly results. New products start at zero
and therefore may qualify before an opening balance is entered. Product edits never
accept quantity-on-hand. Archive via IsActive=false rather than deleting history.

## API contract

Prefix every route with `/businesses/{businessId}`. Lists default to page=1 and
pageSize=20; caps are page 1000, size 100, and offset 10000.

| Method and relative route | Result |
| --- | --- |
| GET /categories | Paged categories |
| POST /categories | Create category from `{ name }` |
| GET /products | Paged products; optional search and lowStockOnly |
| POST /products | Create product and its base unit; 201 |
| GET /products/{productId} | Product with base-unit stock, reorder level, and low-stock flag |
| PUT /products/{productId} | Edit name/category/reorder level/active flag; 204 |
| GET /products/{productId}/units | Paged base/alternative units |
| POST /products/{productId}/units | Add immutable named conversion |
| POST /products/{productId}/opening-stock | Record opening movement or return its idempotent result |
| GET /products/{productId}/movements | Paged history ordered by descending per-product posting sequence |

Product creation example:

```json
{
  "name": "Bottled Water",
  "sku": "WATER-500ML",
  "stockUnit": "Bottle",
  "allowsFractionalStock": false,
  "reorderLevel": 12,
  "categoryId": null
}
```

SKUs are case-insensitively unique per business and allow ASCII letters, digits,
periods, underscores, and hyphens (1-64 characters). Category names are also unique
per business; unit names are unique per product. Product search is literal substring
matching over name/SKU with a 100-character limit; it uses no shared catalog cache.
It does not yet use the legacy catalog's FTS indexes.

Add a crate:

```json
{ "name": "Crate", "conversionFactor": 24, "allowsFractionalQuantity": false }
```

Then use its returned unit ID when recording opening stock:

```json
{
  "unitId": "replace-with-returned-guid",
  "quantity": 2,
  "effectiveDate": "2026-10-26",
  "reason": "Existing stock counted when joining",
  "idempotencyKey": "opening-water-001"
}
```

This records two crates and adds 48 bottles. EffectiveDate is required and cannot
be later than today in the business's configured timezone. RecordedAtUtc and
ActorUserId independently record when/who entered the balance.

Opening stock is allowed once per product before other movements, including a
zero-quantity opening. The writer transaction commits product balance, opening
flag, movement, and audit event together. The idempotency key is unique across the
business, not only the product. Repeating the same key and normalized payload returns
the same movement; a changed payload returns 409. Another key cannot create a second
opening. The original result remains retrievable on retry even after product archival.
There is no opening edit/delete route; controlled corrections belong to phase 5.

No purchase-cost valuation or profit calculations are added in this phase. Monetary
rounding/currency precision and costing rules must be settled before those reports.

## Migration, backup, and verification

`AddInventoryFoundation` creates ProductCategories, InventoryProducts, ProductUnits,
and StockMovements. It leaves legacy catalog tables and FTS triggers intact. Normal
startup applies pending migrations; tests use their own databases. Maintenance
verification now requires all four inventory tables alongside tenant/account tables.

Focused checks cover pack/fractional conversion, precision and bounds, per-tenant
SKU reuse, composite relationships, default-deny queries/writes, staff restrictions,
live membership revocation, atomic rollback, immutable movement/unit records,
idempotent retry and concurrent opening requests, low-stock equality, and snapshot
restoration of both quantity and movement history.
