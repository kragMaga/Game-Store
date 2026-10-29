# Business master data and category reports

Categories, units of measure, suppliers, and customers are tenant-owned tables.
The same names can exist in different businesses. Category and unit names are
unique within their business after trimming and uppercase normalization.
All category/UOM changes require Owner/Admin authority and recent authentication;
active members may read the paged lists. No master deletion endpoint is exposed.

## Master lists

Routes below are relative to `/businesses/{businessId}`:

| Method | Route | Purpose |
|---|---|---|
| GET / POST | `/categories` | List / create categories |
| PUT | `/categories/{id}` | Rename, deactivate or reactivate a category |
| GET / POST | `/units-of-measure` | List / create reusable units |
| PUT | `/units-of-measure/{id}` | Rename, deactivate or reactivate a unit |

Create requests contain `name`. Update requests contain `name` and `isActive`.
Lists include inactive records and return `isActive`, allowing the frontend to hide
inactive choices while still displaying them on existing records. List pagination
uses `page` and `pageSize` (maximum 100), with bounded offsets.

Products reference `categoryId` and `stockUnitId`. A product can remain
uncategorized. Supply `stockUnitId` instead of a stock-unit name when creating a
product; legacy `stockUnit` names still resolve/create one shared business unit.
Product pack options similarly accept `unitOfMeasureId` instead of `name`.
The selected ID is authoritative when both are supplied. Spreadsheet imports use
the same business unit list. Foreign keys include BusinessId, so another business's
category or unit cannot be attached, even through direct database writes.

Conversions remain PRODUCT-SPECIFIC: a Carton may contain 12 bottles for one
product and 24 for another. A reusable unit does not have a universal conversion.
Product base units and pack conversions cannot be edited after creation. Master
renames change selection labels without rewriting posted labels or conversions.
Deactivation prevents new product/pack assignments. Existing products can still
record stock transactions and returns using their saved units. An unchanged inactive category may remain on a product.

## Category reports

`GET /reports/categories/{report}` supports `inventory`, `sales`, `purchases`,
`movements`, and `corrections`. It returns the existing ReportTable shape, including
columns, rows, total count and pagination. All category reports require Owner/Admin
authority, consistent with their financial content. Closed-business export access
follows the existing retained-owner policy.

Optional filters: `categoryId`, `from`, `to`, `page`, `pageSize`. Date filters are
inclusive and apply to effective dates. Inventory is a CURRENT stock report and
rejects date filters. Use movements for historical stock changes.
`GET /reports/categories/{report}/export.xlsx` exports the same grouped data with
category/date filters, a 10,000-group cap, and the existing export rate limit.

Reports group by category ID, saved category name, base unit ID/label and operation
kind. They never add incompatible units together. Renamed category snapshots may
appear as separate rows with the same ID; filtering that ID includes both labels.
Inventory uses current categories; transactions preserve categories at POSTING
TIME (not a reconstructed classification for a backdated effective date). Returns
and reversals inherit the original transaction's category, not its current product
category. Adjustments/counts snapshot the category when posted.

Sales/purchase amounts are gross line values; correction amounts are return credits.
Correction quantities represent actual STOCK DELTAS, so damaged sales returns can
have zero stock delta while still carrying a credit amount. Reports group kinds
separately; gross sales are not silently netted against returns. Counts represent
source rows (products for inventory, lines for financial reports, movements otherwise).
Low-stock counts include active products only. Money uses the business currency.
These reports do not claim inventory cost valuation or profit; those need an agreed
costing method. Suppliers/customers keep their existing business-specific APIs;
no speculative locations or other unused master tables were added.

## Upgrade behavior

`AddBusinessMasterDataAndCategoryHistory` creates UnitsOfMeasure, reuses existing
normalized product-unit names per business, and links existing product/base/pack
units without changing stock quantities or conversion factors. Existing categories
remain active. Backups now verify 30 required application tables, including units.

Old movements have no reliable historical category snapshot and are explicitly
reported as `Historical category not recorded`; they are never attributed to a
current category by guesswork. Newly uncategorized movements instead have
`CategoryRecorded=true` and display `Uncategorized`. Downgrading loses the new
master IDs and category snapshots, so use compatible protected backups for recovery.
