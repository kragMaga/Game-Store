using System.Globalization;
using System.Net;
using System.Text;
using TradeNest.Api.Data;
using TradeNest.Api.Entities;
using Microsoft.EntityFrameworkCore;
namespace TradeNest.Api.Services;

// Build statements from original ledger events, not just outstanding charges.
// This preserves receipts, reversals, credits and refunds in the running balance.
public sealed class CustomerDocumentService(TradeNestContext db, BusinessAccess access, CustomerLedgerService ledger)
{
    public sealed record StatementEvent(Guid Id, DateOnly Date, DateTime RecordedUtc, int Priority, string Type, string Reference, string Description, long Delta);
    private async Task<Business> Scope(Guid businessId, int actor)
    {
        db.InventoryBusinessId = null; var member = await access.RequireAsync(businessId, actor);
        if (!BusinessAccess.CanRecordPayments(member)) throw new BusinessRequestException(403, "Customer payment permission required");
        db.InventoryBusinessId = businessId; return await db.Businesses.AsNoTracking().SingleAsync(b => b.Id == businessId);
    }
    public async Task<(ReportTable Table, string BusinessName, string CustomerName)> StatementAsync(Guid businessId, Guid customerId, int actor)
    {
        // A short read transaction keeps the balance and event lists consistent
        // while collecting a bounded download. No database lock spans rendering.
        await using var tx = await db.Database.BeginTransactionAsync();
        var business = await Scope(businessId, actor);
        var customer = await db.Customers.AsNoTracking().SingleOrDefaultAsync(c => c.BusinessId == businessId && c.Id == customerId)
            ?? throw new BusinessRequestException(404, "Customer not found");
        var charges = await db.CustomerCharges.AsNoTracking().Where(c => c.BusinessId == businessId && c.CustomerId == customerId).Take(10001).ToListAsync();
        var payments = await db.CustomerPayments.AsNoTracking().Where(p => p.BusinessId == businessId && p.CustomerId == customerId).Take(10001).ToListAsync();
        var corrections = await (from a in db.CustomerChargeAdjustments.AsNoTracking() join c in db.CustomerCharges.AsNoTracking()
            on new { a.BusinessId, Id = a.ChargeId } equals new { c.BusinessId, c.Id }
            join correction in db.InventoryCorrections.AsNoTracking() on new { a.BusinessId, Id = a.CorrectionId } equals new { correction.BusinessId, correction.Id }
            where a.BusinessId == businessId && c.CustomerId == customerId select new { Adjustment = a, Correction = correction }).Take(10001).ToListAsync();
        var reversals = await (from r in db.CustomerPaymentReversals.AsNoTracking() join p in db.CustomerPayments.AsNoTracking()
            on new { r.BusinessId, Id = r.PaymentId } equals new { p.BusinessId, p.Id }
            where r.BusinessId == businessId && p.CustomerId == customerId select new { Reversal = r, Payment = p }).Take(10001).ToListAsync();
        if (charges.Count + payments.Count + corrections.Count + reversals.Count > 10000)
            throw new BusinessRequestException(413, "Statement exceeds 10000 events; use paged ledger endpoints");
        var saleIds = charges.Where(c => c.SaleId.HasValue).Select(c => c.SaleId!.Value).ToArray();
        var sales = await db.InventoryTransactions.AsNoTracking().Where(s => s.BusinessId == businessId && saleIds.Contains(s.Id))
            .ToDictionaryAsync(s => s.Id, s => s.DocumentNumber ?? s.Id.ToString());
        var events = new List<StatementEvent>();
        foreach (var c in charges) events.Add(new(c.Id, c.EffectiveDate, c.RecordedAtUtc, 0, c.Kind,
            c.SaleId.HasValue ? sales.GetValueOrDefault(c.SaleId.Value, c.SaleId.Value.ToString()) : "Opening debt", c.Reason, c.AmountMinor));
        foreach (var p in payments) events.Add(new(p.Id, p.EffectiveDate, p.RecordedAtUtc, 2, "Payment", p.DocumentNumber ?? p.Id.ToString(), p.Method + " " + p.Reference, -p.AmountMinor));
        foreach (var change in corrections)
        {
            var c = change.Correction; var a = change.Adjustment;
            events.Add(new(a.Id, c.EffectiveDate, c.RecordedAtUtc, 1, c.Kind, c.DocumentNumber ?? c.Id.ToString(), c.Reason + " | " +
                (c.TransactionId.HasValue ? "For " + sales.GetValueOrDefault(c.TransactionId.Value, c.TransactionId.Value.ToString()) : "Opening debt correction"), a.DeltaMinor));
            if (a.RefundMinor > 0) events.Add(new(a.Id, c.EffectiveDate, c.RecordedAtUtc, 3, "Refund", c.DocumentNumber ?? c.Id.ToString(), a.RefundMethod ?? "", a.RefundMinor));
        }
        foreach (var r in reversals) events.Add(new(r.Reversal.Id, r.Reversal.EffectiveDate, r.Reversal.RecordedAtUtc, 3, "Payment reversal",
            r.Reversal.DocumentNumber ?? r.Reversal.Id.ToString(), r.Reversal.Reason + " | Reverses " + (r.Payment.DocumentNumber ?? r.Payment.Id.ToString()), r.Payment.AmountMinor));
        if (events.Count > 10000) throw new BusinessRequestException(413, "Statement exceeds 10000 events");
        await tx.CommitAsync();
        long balance = 0; var rows = new List<object?[]>();
        foreach (var item in events.OrderBy(e => e.Date).ThenBy(e => e.RecordedUtc).ThenBy(e => e.Priority).ThenBy(e => e.Id))
        {
            balance = checked(balance + item.Delta);
            rows.Add([item.Date, item.Type, item.Reference, item.Description, business.Currency,
                InventoryMoney.FromMinor(Math.Max(item.Delta, 0), business.CurrencyDecimalPlaces),
                InventoryMoney.FromMinor(Math.Max(-item.Delta, 0), business.CurrencyDecimalPlaces), InventoryMoney.FromMinor(balance, business.CurrencyDecimalPlaces)]);
        }
        return (new("customer-statement", business.Currency, ["Date", "Type", "Document", "Description", "Currency", "Debit", "Credit", "Balance"], rows, rows.Count, 1, 100), business.Name, customer.Name);
    }
    public async Task<byte[]> ReceiptAsync(Guid businessId, Guid paymentId, int actor)
    {
        // Reuse the ledger's live finance permission and walk-in visibility guard.
        await using var tx = await db.Database.BeginTransactionAsync();
        var detail = await ledger.PaymentDetailAsync(businessId, paymentId, actor, allowOwnSaleReceipt: true);
        var b = await db.Businesses.AsNoTracking().SingleAsync(b => b.Id == businessId);
        var customer = detail.Payment.CustomerId.HasValue ? await db.Customers.AsNoTracking().Where(c => c.BusinessId == businessId && c.Id == detail.Payment.CustomerId).Select(c => c.Name).SingleAsync() : "Walk-in customer";
        var saleIds = detail.Allocations.Where(a => a.SaleId.HasValue).Select(a => a.SaleId!.Value).ToArray();
        var numbers = await db.InventoryTransactions.AsNoTracking().Where(s => s.BusinessId == businessId && saleIds.Contains(s.Id))
            .ToDictionaryAsync(s => s.Id, s => s.DocumentNumber ?? s.Id.ToString());
        var rows = detail.Allocations.Select(a => new object?[] { a.SaleId.HasValue ? numbers.GetValueOrDefault(a.SaleId.Value) : "Opening debt", a.ChargeId, b.Currency, a.Amount }).ToList();
        var table = new ReportTable("payment-receipt", b.Currency, ["Charge document", "Charge ID", "Currency", "Allocated amount"], rows, rows.Count, 1, 100);
        await tx.CommitAsync();
        var p = detail.Payment;
        var note = $"Receipt {p.DocumentNumber ?? p.Id.ToString()} | {p.EffectiveDate:yyyy-MM-dd} | {p.Method} | Amount {p.Amount.ToString(CultureInfo.InvariantCulture)} {b.Currency}. " +
            (p.IsReversed ? "REVERSED: this receipt was corrected; it is not an effective payment." : "Records an external payment. Later refunds/corrections are shown on the customer statement.") + " Reference: " + p.Reference;
        return RenderHtml(table, b.Name, customer, note);
    }
    public static byte[] RenderHtml(ReportTable table, string business, string customer, string note)
    {
        // HTML is a printable download without browser scripts or external assets.
        // Encode EVERY dynamic value; customer names/references must stay text.
        static string E(object? value) => WebUtility.HtmlEncode(value switch { DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), IFormattable f => f.ToString(null, CultureInfo.InvariantCulture), _ => value?.ToString() ?? "" });
        var html = new StringBuilder("<!doctype html><html><head><meta charset=\"utf-8\"><title>Business document</title><style>body{font:14px sans-serif;margin:32px}table{border-collapse:collapse;width:100%}th,td{border:1px solid #bbb;padding:8px;text-align:left}thead{display:table-header-group}tr{break-inside:avoid}@media print{body{margin:0}}</style></head><body>");
        html.Append("<h1>").Append(E(business)).Append("</h1><h2>").Append(E(customer)).Append("</h2><p>").Append(E(note)).Append("</p><table><thead><tr>");
        foreach (var column in table.Columns) html.Append("<th>").Append(E(column)).Append("</th>");
        html.Append("</tr></thead><tbody>");
        foreach (var row in table.Rows) { html.Append("<tr>"); foreach (var cell in row) html.Append("<td>").Append(E(cell)).Append("</td>"); html.Append("</tr>"); }
        html.Append("</tbody></table>");
        if (table.Report == "customer-statement")
        {
            // A standalone statement must show zero explicitly when there are no
            // events, rather than leaving the recipient to infer an empty balance.
            var closing = table.Rows.Count == 0 ? 0m : table.Rows[^1][7];
            html.Append("<p><strong>Current balance: ").Append(E(closing)).Append(' ').Append(E(table.Currency)).Append("</strong></p>");
        }
        html.Append("</body></html>"); return Encoding.UTF8.GetBytes(html.ToString());
    }
}
