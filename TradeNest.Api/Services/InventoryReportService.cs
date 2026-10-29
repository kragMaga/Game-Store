using System.Globalization;
using System.IO.Compression;
using System.Xml;
using TradeNest.Api.Data;
using TradeNest.Api.Dtos;
using TradeNest.Api.Entities;
using Microsoft.EntityFrameworkCore;

namespace TradeNest.Api.Services;

// Reports use live membership and explicit tenant predicates. Exports are bounded
// snapshots, not arbitrary SQL or an unbounded download of the shared database.
public sealed partial class InventoryReportService(TradeNestContext db, BusinessAccess access, CustomerLedgerService ledger)
{
    public const int ExportLimit = 10000;
    private async Task<Business> Scope(Guid businessId, int actor, bool financial)
    {
        db.InventoryBusinessId = null;
        var business = await db.Businesses.AsNoTracking().SingleOrDefaultAsync(b => b.Id == businessId)
            ?? throw new BusinessRequestException(404, "Business not found");
        if (business.ClosedAtUtc.HasValue)
        {
            // Closed businesses retain a read/export route for their active owner.
            // No ordinary posting or staff access is restored by this exception.
            if (business.OwnerUserId != actor || !await db.Users.AnyAsync(u => u.Id == actor && u.AccountClosedAtUtc == null) || !await db.BusinessMemberships.AnyAsync(m => m.BusinessId == businessId && m.UserId == actor && m.IsActive && m.Role == BusinessRole.Owner))
                throw new BusinessRequestException(404, "Business not found");
        }
        else await access.RequireAsync(businessId, actor, financial);
        db.InventoryBusinessId = businessId; return business;
    }
    private static void Range(DateOnly? startDate, DateOnly? to)
    {
        if (startDate.HasValue && to.HasValue && startDate > to) throw new BusinessRequestException(400, "Start date must precede end date");
    }
    private IQueryable<InventoryTransaction> Documents(Guid businessId, string kind, DateOnly? startDate, DateOnly? to) =>
        db.InventoryTransactions.AsNoTracking().Where(t => t.BusinessId == businessId && t.Kind == kind &&
            (!startDate.HasValue || t.EffectiveDate >= startDate.Value) && (!to.HasValue || t.EffectiveDate <= to.Value));

    public async Task<object> SummaryAsync(Guid businessId, int actor, DateOnly? startDate, DateOnly? to)
    {
        Range(startDate, to); var business = await Scope(businessId, actor, true);
        var sales = Documents(businessId, "Sale", startDate, to); var purchases = Documents(businessId, "Purchase", startDate, to);
        var corrections = db.InventoryCorrections.Where(c => c.BusinessId == businessId && (!startDate.HasValue || c.EffectiveDate >= startDate) && (!to.HasValue || c.EffectiveDate <= to));
        var saleReturns = await corrections.Where(c => c.Kind == "SaleReturn" || c.Kind == "SaleReversal").SumAsync(c => (long?)c.AmountMinor) ?? 0;
        var purchaseReturns = await corrections.Where(c => c.Kind == "PurchaseReturn" || c.Kind == "PurchaseReversal").SumAsync(c => (long?)c.AmountMinor) ?? 0;
        var salesTotal = await sales.SumAsync(t => (long?)t.TotalMinor) ?? 0;
        var purchasesTotal = await purchases.SumAsync(t => (long?)t.TotalMinor) ?? 0;
        var refund = await (from a in db.CustomerChargeAdjustments join c in corrections
            on new { a.BusinessId, Id = a.CorrectionId } equals new { c.BusinessId, c.Id }
            where a.BusinessId == businessId select (long?)a.RefundMinor).SumAsync() ?? 0;
        var receiptQuery = db.CustomerPayments.Where(p => p.BusinessId == businessId && (!startDate.HasValue || p.EffectiveDate >= startDate) && (!to.HasValue || p.EffectiveDate <= to));
        var received = await receiptQuery.SumAsync(p => (long?)p.AmountMinor) ?? 0;
        var reversed = await (from r in db.CustomerPaymentReversals join p in db.CustomerPayments
            on new { r.BusinessId, Id = r.PaymentId } equals new { p.BusinessId, p.Id }
            where r.BusinessId == businessId && (!startDate.HasValue || r.EffectiveDate >= startDate) && (!to.HasValue || r.EffectiveDate <= to)
            select (long?)p.AmountMinor).SumAsync() ?? 0;
        decimal Money(long value) => InventoryMoney.FromMinor(value, business.CurrencyDecimalPlaces);
        return new { business.Currency, business.CurrencyDecimalPlaces, From = startDate, To = to,
            SalesCount = await sales.CountAsync(), PurchasesCount = await purchases.CountAsync(),
            Sales = Money(salesTotal), SalesReturns = Money(saleReturns), NetSales = Money(salesTotal - saleReturns),
            Purchases = Money(purchasesTotal), PurchaseReturns = Money(purchaseReturns), NetPurchases = Money(purchasesTotal - purchaseReturns),
            Receipts = Money(received), ReversedReceipts = Money(reversed), Refunds = Money(refund), NetReceipts = Money(received - reversed - refund),
            // Historical sales lacking charges are disclosed, never assumed paid.
            UntrackedSalesCount = await sales.CountAsync(t => !db.CustomerCharges.Any(c => c.BusinessId == businessId && c.SaleId == t.Id)),
            ActiveProducts = await db.InventoryProducts.CountAsync(p => p.BusinessId == businessId && p.IsActive),
            LowStockProducts = await db.InventoryProducts.CountAsync(p => p.BusinessId == businessId && p.IsActive && p.QuantityOnHandMicros <= p.ReorderLevelMicros) };
    }

    public async Task<ReportTable> TableAsync(Guid businessId, int actor, string kind, DateOnly? startDate, DateOnly? to, int page = 1, int size = 100, bool export = false)
    {
        Range(startDate, to);
        if (kind is not ("inventory" or "movements" or "sales" or "purchases" or "customers" or "corrections")) throw new BusinessRequestException(404, "Report not found");
        if (page is < 1 or > 1000 || size is < 1 or > 100 || (long)(page - 1) * size > 10000) throw new BusinessRequestException(400, "Invalid pagination");
        var business = await Scope(businessId, actor, kind is not ("inventory" or "movements"));
        var take = export ? ExportLimit + 1 : size; var skip = export ? 0 : (page - 1) * size;
        var rows = new List<object?[]>(); string[] headers; int count;
        if (kind == "inventory")
        {
            headers = ["SKU", "Product", "Base unit", "On hand", "Reorder level", "Active", "Low stock", "Default selling price (base unit)"];
            var query = db.InventoryProducts.AsNoTracking().Where(p => p.BusinessId == businessId);
            count = await query.CountAsync();
            foreach (var p in await query.OrderBy(p => p.NormalizedSku).ThenBy(p => p.Id).Skip(skip).Take(take).ToListAsync())
                rows.Add([p.Sku, p.Name, p.StockUnit, StockQuantity.FromMicros(p.QuantityOnHandMicros), StockQuantity.FromMicros(p.ReorderLevelMicros), p.IsActive, p.IsActive && p.QuantityOnHandMicros <= p.ReorderLevelMicros, p.DefaultSellingPriceMicros.HasValue ? StockQuantity.FromMicros(p.DefaultSellingPriceMicros.Value) : null]);
        }
        else if (kind == "movements")
        {
            headers = ["Product ID", "Sequence", "Kind", "Effective date", "Recorded UTC", "Unit", "Quantity", "Base delta", "Balance after", "Reason", "Actor"];
            var query = db.StockMovements.AsNoTracking().Where(m => m.BusinessId == businessId && (!startDate.HasValue || m.EffectiveDate >= startDate) && (!to.HasValue || m.EffectiveDate <= to));
            count = await query.CountAsync();
            foreach (var m in await query.OrderBy(m => m.ProductId).ThenBy(m => m.SequenceNumber).Skip(skip).Take(take).ToListAsync())
                rows.Add([m.ProductId, m.SequenceNumber, m.Kind, m.EffectiveDate, m.RecordedAtUtc, m.UnitName, StockQuantity.FromMicros(m.UnitQuantityMicros), StockQuantity.FromMicros(m.BaseQuantityMicros), StockQuantity.FromMicros(m.BalanceAfterMicros), m.Reason, m.ActorUserId]);
        }
        else if (kind is "sales" or "purchases")
        {
            headers = ["Document ID", "Effective date", "Recorded UTC", "Reference", "Customer", "Currency", "Total", "Net paid", "Outstanding", "Payment tracked", "Actor", "Document number"];
            var query = Documents(businessId, kind == "sales" ? "Sale" : "Purchase", startDate, to); count = await query.CountAsync();
            var documents = await query.OrderBy(t => t.EffectiveDate).ThenBy(t => t.Id).Skip(skip).Take(take).ToListAsync();
            // Bound the selected documents before computing current financial status.
            var finances = await ledger.DescribeSalesAsync(businessId, documents.Select(d => d.Id));
            foreach (var d in documents)
            {
                finances.TryGetValue(d.Id, out var status);
                rows.Add([d.Id, d.EffectiveDate, d.RecordedAtUtc, d.Reference, d.CustomerName, d.Currency,
                    InventoryMoney.FromMinor(d.TotalMinor, d.CurrencyDecimalPlaces), status?.AmountPaid, status?.Outstanding, status?.PaymentsTracked ?? false, d.ActorUserId, d.DocumentNumber]);
            }
        }
        else if (kind == "corrections")
        {
            headers = ["Correction ID", "Document ID", "Kind", "Effective date", "Recorded UTC", "Reason", "Currency", "Amount", "Refund", "Actor", "Document number"];
            var query = db.InventoryCorrections.AsNoTracking().Where(c => c.BusinessId == businessId && (!startDate.HasValue || c.EffectiveDate >= startDate) && (!to.HasValue || c.EffectiveDate <= to));
            count = await query.CountAsync();
            var selected = await query.OrderBy(c => c.EffectiveDate).ThenBy(c => c.Id).Skip(skip).Take(take).ToListAsync();
            var ids = selected.Select(c => c.Id).ToArray();
            var refunds = await db.CustomerChargeAdjustments.Where(a => a.BusinessId == businessId && ids.Contains(a.CorrectionId)).ToDictionaryAsync(a => a.CorrectionId, a => a.RefundMinor);
            foreach (var c in selected) rows.Add([c.Id, c.TransactionId, c.Kind, c.EffectiveDate, c.RecordedAtUtc, c.Reason, c.Currency,
                InventoryMoney.FromMinor(c.AmountMinor, c.CurrencyDecimalPlaces), InventoryMoney.FromMinor(refunds.GetValueOrDefault(c.Id), c.CurrencyDecimalPlaces), c.ActorUserId, c.DocumentNumber]);
        }
        else
        {
            if (startDate.HasValue || to.HasValue) throw new BusinessRequestException(400, "Customer balances are current, not historical as-of-date balances");
            headers = ["Code", "Customer", "Active", "Currency", "Opening debt", "Sales", "Net receipts", "Outstanding"];
            var query = db.Customers.AsNoTracking().Where(c => c.BusinessId == businessId); count = await query.CountAsync();
            var customers = await query.OrderBy(c => c.NormalizedCode).ThenBy(c => c.Id).Skip(skip).Take(take).ToListAsync();
            var ids = customers.Select(c => c.Id).ToArray();
            var charges = await db.CustomerCharges.AsNoTracking().Where(c => c.BusinessId == businessId && c.CustomerId.HasValue && ids.Contains(c.CustomerId.Value)).ToListAsync();
            var chargeIds = charges.Select(c => c.Id).ToArray();
            var adjustments = await db.CustomerChargeAdjustments.Where(a => a.BusinessId == businessId && chargeIds.Contains(a.ChargeId)).ToListAsync();
            var paid = await db.CustomerPaymentAllocations.Where(a => a.BusinessId == businessId && chargeIds.Contains(a.ChargeId) &&
                !db.CustomerPaymentReversals.Any(r => r.BusinessId == businessId && r.PaymentId == a.PaymentId)).GroupBy(a => a.ChargeId)
                .Select(g => new { Id = g.Key, Amount = g.Sum(a => a.AmountMinor) }).ToDictionaryAsync(a => a.Id, a => a.Amount);
            foreach (var c in customers)
            {
                var own = charges.Where(ch => ch.CustomerId == c.Id).ToList(); var ownIds = own.Select(ch => ch.Id).ToHashSet();
                var changes = adjustments.Where(a => ownIds.Contains(a.ChargeId)).ToList();
                long Amount(string type) => own.Where(ch => ch.Kind == type).Sum(ch => ch.AmountMinor + changes.Where(a => a.ChargeId == ch.Id).Sum(a => a.DeltaMinor));
                var received = own.Sum(ch => paid.GetValueOrDefault(ch.Id)) - changes.Sum(a => a.RefundMinor);
                decimal Money(long value) => InventoryMoney.FromMinor(value, business.CurrencyDecimalPlaces);
                rows.Add([c.Code, c.Name, c.IsActive, business.Currency, Money(Amount("OpeningDebt")), Money(Amount("Sale")), Money(received), Money(Amount("OpeningDebt") + Amount("Sale") - received)]);
            }
        }
        if (export && rows.Count > ExportLimit) throw new BusinessRequestException(413, "Export exceeds 10000 rows; narrow the date range or use paged reports");
        return new(kind, business.Currency, headers, rows, count, page, size);
    }
}

public sealed record ReportTable(string Report, string Currency, string[] Columns, IReadOnlyList<object?[]> Rows, int TotalCount, int Page, int PageSize);

// A small standards-based XLSX writer avoids a new runtime dependency. All user
// strings are explicit inline strings, never formulas (including =, +, -, and @).
public static class InventoryWorkbook
{
    private const string Spreadsheet = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    public static byte[] Create(ReportTable table)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            Write(zip, "[Content_Types].xml", "<?xml version=\"1.0\" encoding=\"utf-8\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/><Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/></Types>");
            Write(zip, "_rels/.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>");
            Write(zip, "xl/workbook.xml", "<workbook xmlns=\"" + Spreadsheet + "\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets><sheet name=\"Report\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>");
            Write(zip, "xl/_rels/workbook.xml.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/></Relationships>");
            using var stream = zip.CreateEntry("xl/worksheets/sheet1.xml").Open();
            using var xml = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new System.Text.UTF8Encoding(false) });
            xml.WriteStartElement("worksheet", Spreadsheet); xml.WriteStartElement("sheetData", Spreadsheet);
            Row(xml, table.Columns.Cast<object?>());
            foreach (var row in table.Rows) Row(xml, row);
            xml.WriteEndElement(); xml.WriteEndElement();
        }
        return output.ToArray();
    }
    private static void Row(XmlWriter xml, IEnumerable<object?> values)
    {
        xml.WriteStartElement("row", Spreadsheet);
        foreach (var value in values)
        {
            xml.WriteStartElement("c", Spreadsheet);
            if (value is decimal or long or int)
            { xml.WriteElementString("v", Spreadsheet, Convert.ToString(value, CultureInfo.InvariantCulture)); }
            else
            {
                xml.WriteAttributeString("t", "inlineStr"); xml.WriteStartElement("is", Spreadsheet);
                var text = value switch { DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), DateTime d => d.ToString("O", CultureInfo.InvariantCulture), _ => value?.ToString() ?? "" };
                // XML 1.0 disallows some control characters that may appear in
                // free text. Preserve Unicode while replacing only invalid units.
                var safe = new System.Text.StringBuilder();
                foreach (var rune in text.EnumerateRunes())
                {
                    if (safe.Length + rune.Utf16SequenceLength > 32767) break;
                    if (rune.Value is 9 or 10 or 13 or (>= 0x20 and <= 0xD7FF) or (>= 0xE000 and <= 0xFFFD) or (>= 0x10000 and <= 0x10FFFF)) safe.Append(rune.ToString());
                }
                text = safe.ToString();
                xml.WriteStartElement("t", Spreadsheet); xml.WriteAttributeString("xml", "space", "http://www.w3.org/XML/1998/namespace", "preserve"); xml.WriteString(text);
                xml.WriteEndElement(); xml.WriteEndElement();
            }
            xml.WriteEndElement();
        }
        xml.WriteEndElement();
    }
    private static void Write(ZipArchive zip, string name, string content)
    { using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new System.Text.UTF8Encoding(false)); writer.Write(content); }
}
