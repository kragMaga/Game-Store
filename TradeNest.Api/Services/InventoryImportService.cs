using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using TradeNest.Api.Data;
using TradeNest.Api.Entities;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace TradeNest.Api.Services;

public sealed record CommitInventoryImportDto([Required, StringLength(2 * 1024 * 1024)] string PreviewToken);
public sealed record ImportRowError(int Row, string Message);
public sealed record ImportPreviewRowDto(int Row, IReadOnlyDictionary<string, string> Values);
public sealed record ImportPreviewDto(string Kind, int RowCount, IReadOnlyList<ImportRowError> Errors, string? PreviewToken,
    IReadOnlyList<ImportPreviewRowDto>? PreviewRows = null);

// Preview and commit run the SAME validation/posting code under a writer lock.
// Preview rolls back; commit revalidates live state and is all-or-nothing. Tokens
// expire after 30 minutes and bind the rows to one tenant and one reviewing admin.
public sealed class InventoryImportService(TradeNestContext db, BusinessAccess access, IDataProtectionProvider protection, TimeProvider clock)
{
    private readonly IDataProtector protector = protection.CreateProtector(LegacyCompatibility.ImportPreviewPurpose);
    public sealed record Preview(Guid Id, Guid BusinessId, int ActorId, string Kind, DateTime ExpiresAtUtc, List<Dictionary<string, string>> Rows);
    public static string[] Columns(string kind) => kind switch {
        "products" => ["Sku", "Name", "StockUnit", "AllowsFractionalStock", "ReorderLevel", "DefaultSellingPrice", "OpeningQuantity", "EffectiveDate", "Reason"],
        "customers" => ["Code", "Name", "Email", "Phone", "OpeningDebt", "EffectiveDate", "DueDate", "Reason"],
        "opening-stock" => ["Sku", "Quantity", "UnitName", "EffectiveDate", "Reason"],
        "opening-debts" => ["Code", "Amount", "EffectiveDate", "DueDate", "Reason"],
        _ => throw new BusinessRequestException(404, "Import kind not found") };
    private async Task<Business> Scope(Guid businessId, int actor)
    { db.InventoryBusinessId = null; await access.RequireAsync(businessId, actor, true); db.InventoryBusinessId = businessId; return await db.Businesses.AsNoTracking().SingleAsync(b => b.Id == businessId); }
    public async Task<byte[]> TemplateAsync(Guid businessId, int actor, string kind)
    {
        await Scope(businessId, actor);
        return InventoryWorkbook.Create(new(kind, "", Columns(kind), [], 0, 1, 100));
    }
    public async Task<ImportPreviewDto> PreviewAsync(Guid businessId, int actor, string kind, byte[] file)
    {
        var columns = Columns(kind); var rows = InventorySpreadsheet.Read(file).ToList();
        if (rows[0].Keys.Except(columns, StringComparer.OrdinalIgnoreCase).Any() || columns.Except(rows[0].Keys, StringComparer.OrdinalIgnoreCase).Any())
            throw new BusinessRequestException(400, "Use the downloaded template's column headers exactly");
        var preview = new Preview(Guid.NewGuid(), businessId, actor, kind, clock.GetUtcNow().UtcDateTime.AddMinutes(30), rows);
        await using var tx = await SecurityTransaction.BeginAsync(db); var business = await Scope(businessId, actor);
        var errors = await Apply(preview, business);
        // No commit here. Even valid preview inserts are rolled back on disposal.
        var serialized = JsonSerializer.Serialize(preview);
        if (System.Text.Encoding.UTF8.GetByteCount(serialized) > 1024 * 1024) throw new BusinessRequestException(413, "Import contents exceed the preview limit; split the workbook");
        return new(kind, rows.Count, errors, errors.Count == 0 ? protector.Protect(serialized) : null,
            rows.Take(20).Select((row, i) => new ImportPreviewRowDto(i + 2, row)).ToList());
    }
    public async Task<object> CommitAsync(Guid businessId, int actor, string token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 2 * 1024 * 1024) throw new BusinessRequestException(400, "Invalid preview token");
        Preview preview;
        try { preview = JsonSerializer.Deserialize<Preview>(protector.Unprotect(token)) ?? throw new JsonException(); }
        catch (Exception e) when (e is CryptographicException or JsonException) { throw new BusinessRequestException(400, "Invalid preview token"); }
        if (preview.BusinessId != businessId || preview.ActorId != actor) throw new BusinessRequestException(400, "Preview belongs to another business or reviewer");
        await using var tx = await SecurityTransaction.BeginAsync(db); var business = await Scope(businessId, actor);
        var committed = await db.InventoryImportBatches.AsNoTracking().SingleOrDefaultAsync(b => b.BusinessId == businessId && b.Id == preview.Id);
        if (committed is not null) return new { BatchId = committed.Id, committed.RowCount, AlreadyCommitted = true };
        if (preview.ExpiresAtUtc <= clock.GetUtcNow().UtcDateTime) throw new BusinessRequestException(409, "Preview expired; upload and review again");
        var errors = await Apply(preview, business);
        if (errors.Count > 0) throw new BusinessRequestException(409, $"Import no longer validates. Row {errors[0].Row}: {errors[0].Message}. Preview again.");
        db.InventoryImportBatches.Add(new() { BusinessId = businessId, Id = preview.Id, Kind = preview.Kind, ActorUserId = actor, RowCount = preview.Rows.Count, CommittedAtUtc = clock.GetUtcNow().UtcDateTime });
        db.BusinessAuditEvents.Add(new() { BusinessId = businessId, ActorUserId = actor, Action = "InventoryImportCommitted", CreatedAtUtc = clock.GetUtcNow().UtcDateTime });
        await db.SaveChangesAsync(); await tx.CommitAsync(); return new { BatchId = preview.Id, RowCount = preview.Rows.Count, AlreadyCommitted = false };
    }
    private async Task<List<ImportRowError>> Apply(Preview preview, Business business)
    {
        var errors = new List<ImportRowError>(); var seen = new HashSet<string>();
        for (var index = 0; index < preview.Rows.Count; index++)
        {
            var row = new Dictionary<string, string>(preview.Rows[index], StringComparer.OrdinalIgnoreCase);
            string Cell(string key) => row.GetValueOrDefault(key, "");
            try
            {
                var code = Text(Cell(preview.Kind is "products" or "opening-stock" ? "Sku" : "Code"), 64, 1);
                if (!Regex.IsMatch(code, "^[A-Za-z0-9._-]{1,64}$")) throw new BusinessRequestException(400, "Code/SKU must use letters, numbers, periods, underscores or hyphens");
                var normalized = code.ToUpperInvariant();
                if (!seen.Add(normalized)) throw new BusinessRequestException(400, "Repeated code/SKU in this workbook");
                if (preview.Kind == "products")
                {
                    var name = Text(Cell("Name"), 160); var unitName = Text(Cell("StockUnit"), 40, 1);
                    if (!bool.TryParse(Cell("AllowsFractionalStock"), out var fractional)) throw new BusinessRequestException(400, "AllowsFractionalStock must be true or false");
                    var reorder = StockQuantity.ToMicros(InventorySpreadsheet.Number(Cell("ReorderLevel"), optional: true)); Whole(reorder, fractional);
                    var price = Cell("DefaultSellingPrice").Length == 0 ? (long?)null : InventoryMoney.PriceMicros(InventorySpreadsheet.Number(Cell("DefaultSellingPrice")));
                    if (await db.InventoryProducts.AnyAsync(p => p.BusinessId == business.Id && p.NormalizedSku == normalized)) throw new BusinessRequestException(409, "SKU already exists");
                    var master = await InventoryMasterData.ResolveUnitAsync(db, business.Id, null, unitName);
                    unitName = master.Name; // Use the selected master label consistently across entry paths.
                    var product = new InventoryProduct { BusinessId = business.Id, Sku = code, NormalizedSku = normalized, Name = name, StockUnit = unitName, StockUnitId = master.Id,
                        AllowsFractionalStock = fractional, ReorderLevelMicros = reorder, DefaultSellingPriceMicros = price, CreatedAtUtc = clock.GetUtcNow().UtcDateTime };
                    var unit = new ProductUnit { BusinessId = business.Id, ProductId = product.Id, Name = unitName, NormalizedName = unitName.ToUpperInvariant(), UnitOfMeasureId = master.Id, IsBaseUnit = true,
                        AllowsFractionalQuantity = fractional, ConversionFactorMicros = StockQuantity.Scale };
                    if (Cell("OpeningQuantity").Length > 0) await OpeningStock(product, unit, Cell("OpeningQuantity"), Cell("EffectiveDate"), Cell("Reason"), business, preview, index);
                    db.InventoryProducts.Add(product); db.ProductUnits.Add(unit);
                }
                else if (preview.Kind == "customers")
                {
                    var name = Text(Cell("Name"), 160); var email = Cell("Email"); var phone = Cell("Phone");
                    if (email.Length > 254 || email.Length > 0 && !new EmailAddressAttribute().IsValid(email) || phone.Length > 50) throw new BusinessRequestException(400, "Invalid customer email or phone");
                    if (await db.Customers.AnyAsync(c => c.BusinessId == business.Id && c.NormalizedCode == normalized)) throw new BusinessRequestException(409, "Customer code already exists");
                    var customer = new Customer { BusinessId = business.Id, Code = code, NormalizedCode = normalized, Name = name, Email = email.Length == 0 ? null : email,
                        Phone = phone.Length == 0 ? null : phone, CreatedAtUtc = clock.GetUtcNow().UtcDateTime };
                    if (Cell("OpeningDebt").Length > 0) OpeningDebt(customer.Id, Cell("OpeningDebt"), Cell("EffectiveDate"), Cell("DueDate"), Cell("Reason"), business, preview, index);
                    db.Customers.Add(customer);
                }
                else if (preview.Kind == "opening-stock")
                {
                    var product = await db.InventoryProducts.SingleOrDefaultAsync(p => p.BusinessId == business.Id && p.NormalizedSku == normalized)
                        ?? throw new BusinessRequestException(404, "SKU not found in this business");
                    if (!product.IsActive || product.OpeningStockRecorded || product.QuantityOnHandMicros != 0 || await db.StockMovements.AnyAsync(m => m.BusinessId == business.Id && m.ProductId == product.Id))
                        throw new BusinessRequestException(409, "Opening stock requires an active product with no prior movements");
                    var normalizedUnit = Cell("UnitName").ToUpperInvariant();
                    var unit = await db.ProductUnits.AsNoTracking().SingleOrDefaultAsync(u => u.BusinessId == business.Id && u.ProductId == product.Id && u.NormalizedName == normalizedUnit)
                        ?? throw new BusinessRequestException(404, "Product unit not found");
                    await OpeningStock(product, unit, Cell("Quantity"), Cell("EffectiveDate"), Cell("Reason"), business, preview, index);
                }
                else
                {
                    var customer = await db.Customers.AsNoTracking().SingleOrDefaultAsync(c => c.BusinessId == business.Id && c.NormalizedCode == normalized && c.IsActive)
                        ?? throw new BusinessRequestException(404, "Active customer not found");
                    if (await db.CustomerCharges.AnyAsync(c => c.BusinessId == business.Id && c.CustomerId == customer.Id)) throw new BusinessRequestException(409, "Opening debt must precede other customer charges");
                    OpeningDebt(customer.Id, Cell("Amount"), Cell("EffectiveDate"), Cell("DueDate"), Cell("Reason"), business, preview, index);
                }
            }
            catch (BusinessRequestException e) { errors.Add(new(index + 2, e.Message)); if (errors.Count == 100) break; }
        }
        if (errors.Count == 0) await db.SaveChangesAsync();
        return errors;
    }
    private async Task OpeningStock(InventoryProduct product, ProductUnit unit, string quantityText, string dateText, string reasonText, Business business, Preview preview, int index)
    {
        var quantity = StockQuantity.ToMicros(InventorySpreadsheet.Number(quantityText)); Whole(quantity, unit.AllowsFractionalQuantity);
        var amount = StockQuantity.ConvertToBase(quantity, unit.ConversionFactorMicros); Whole(amount, product.AllowsFractionalStock);
        var date = Date(dateText, business); var reason = Text(reasonText, 300, 3);
        product.OpeningStockRecorded = true; product.QuantityOnHandMicros = amount;
        var movement = new StockMovement { BusinessId = business.Id, ProductId = product.Id, UnitId = unit.Id, SequenceNumber = 1, Kind = "OpeningStock", UnitName = unit.Name,
            UnitQuantityMicros = quantity, ConversionFactorMicros = unit.ConversionFactorMicros, BaseQuantityMicros = amount, BalanceAfterMicros = amount, Reason = reason,
            EffectiveDate = date, RecordedAtUtc = clock.GetUtcNow().UtcDateTime, ActorUserId = preview.ActorId, IdempotencyKey = Key(preview, index), RequestFingerprint = preview.Id.ToString("N") };
        await InventoryMasterData.SnapshotAsync(db, product, movement);
        db.StockMovements.Add(movement);
    }
    private void OpeningDebt(Guid customerId, string amountText, string dateText, string dueText, string reasonText, Business b, Preview preview, int index)
    {
        var amount = InventoryMoney.ExactMinor(InventorySpreadsheet.Number(amountText), b.CurrencyDecimalPlaces, allowZero: true); var date = Date(dateText, b);
        DateOnly? due = dueText.Length == 0 ? null : InventorySpreadsheet.Date(dueText); if (due < date) throw new BusinessRequestException(400, "Due date precedes opening date");
        var reason = Text(reasonText, 300, 3);
        db.CustomerCharges.Add(new() { BusinessId = b.Id, CustomerId = customerId, Kind = "OpeningDebt", AmountMinor = amount, Currency = b.Currency, CurrencyDecimalPlaces = b.CurrencyDecimalPlaces,
            EffectiveDate = date, DueDate = due, Reason = reason, ActorUserId = preview.ActorId, RecordedAtUtc = clock.GetUtcNow().UtcDateTime,
            IdempotencyKey = Key(preview, index), RequestFingerprint = preview.Id.ToString("N") });
    }
    private DateOnly Date(string text, Business b)
    { var date = InventorySpreadsheet.Date(text); if (date > BusinessInsightService.Today(b, clock)) throw new BusinessRequestException(400, "Opening date cannot be in the future"); return date; }
    private static string Key(Preview p, int row) => "imp-" + p.Id.ToString("N") + "-" + row;
    private static string Text(string text, int max, int min = 2)
    { if (text.Length < min || text.Length > max) throw new BusinessRequestException(400, "Text length is invalid"); return text; }
    private static void Whole(long quantity, bool fractional)
    { if (!fractional && quantity % StockQuantity.Scale != 0) throw new BusinessRequestException(400, "Product/unit requires whole quantities"); }
}
