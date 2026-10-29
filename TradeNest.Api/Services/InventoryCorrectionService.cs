using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TradeNest.Api.Data;
using TradeNest.Api.Dtos;
using TradeNest.Api.Entities;
using Microsoft.EntityFrameworkCore;

namespace TradeNest.Api.Services;

// Corrections serialize with ordinary posting. Permission, remaining quantities,
// stock, credits, and recorded refunds are checked under the same writer lock.
public sealed class InventoryCorrectionService(TradeNestContext db, BusinessAccess access, TimeProvider clock)
{
    private async Task<Business> Scope(Guid businessId, int actor)
    {
        db.InventoryBusinessId = null;
        await access.RequireAsync(businessId, actor, administrator: true);
        db.InventoryBusinessId = businessId;
        return await db.Businesses.AsNoTracking().SingleAsync(b => b.Id == businessId);
    }

    public async Task<CorrectionDto> ReturnAsync(Guid businessId, Guid transactionId, int actor, ReturnInventoryDto request)
    {
        Validate(request.Reason, request.IdempotencyKey);
        if (request.ReverseRemaining && request.Lines?.Count > 0 || !request.ReverseRemaining &&
            (request.Lines is null || request.Lines.Count is < 1 or > 100 || request.Lines.Any(l => l is null) ||
            request.Lines.Select(l => l.TransactionLineId).Distinct().Count() != request.Lines.Count))
            throw new BusinessRequestException(400, "Provide distinct return lines or reverse the remaining document");
        var fingerprint = Hash(new { transactionId, request.EffectiveDate, reason = request.Reason.Trim(), request.ReverseRemaining,
            refund = Canonical(request.RefundAmount), request.RefundMethod,
            lines = request.Lines?.OrderBy(l => l.TransactionLineId).Select(l => new { l.TransactionLineId, quantity = Canonical(l.Quantity), l.Restockable }) });
        await using var tx = await SecurityTransaction.BeginAsync(db);
        var business = await Scope(businessId, actor);
        var retry = await Retry(businessId, request.IdempotencyKey, fingerprint);
        if (retry is not null) return await View(retry);
        var original = await db.InventoryTransactions.AsNoTracking().SingleOrDefaultAsync(t => t.BusinessId == businessId && t.Id == transactionId)
            ?? throw new BusinessRequestException(404, "Transaction not found");
        Date(request.EffectiveDate, business, original.EffectiveDate);
        var originals = await db.InventoryTransactionLines.AsNoTracking().Where(l => l.BusinessId == businessId && l.TransactionId == transactionId).ToListAsync();
        var ids = originals.Select(l => l.Id).ToArray();
        var previous = await db.InventoryCorrectionLines.AsNoTracking().Where(l => l.BusinessId == businessId && l.TransactionLineId.HasValue && ids.Contains(l.TransactionLineId.Value)).ToListAsync();
        var lines = request.ReverseRemaining ? originals.Select(l => new ReturnLineDto(l.Id,
            StockQuantity.FromMicros(l.QuantityMicros - previous.Where(p => p.TransactionLineId == l.Id).Sum(p => p.QuantityMicros))))
            .Where(l => l.Quantity > 0).ToList() : request.Lines!.ToList();
        if (lines.Count == 0) throw new BusinessRequestException(409, "Document has already been fully returned");
        var correction = New(business, actor, original.Kind + (request.ReverseRemaining ? "Reversal" : "Return"), request.EffectiveDate,
            request.Reason, request.IdempotencyKey, fingerprint);
        correction.TransactionId = transactionId;
        correction.DocumentNumber = await DocumentNumbers.NextAsync(db, businessId, "COR");
        db.InventoryCorrections.Add(correction);
        var sequences = new Dictionary<Guid, long>();
        foreach (var line in lines)
        {
            var source = originals.SingleOrDefault(l => l.Id == line.TransactionLineId)
                ?? throw new BusinessRequestException(404, "Original transaction line not found");
            var quantity = StockQuantity.ToMicros(line.Quantity, allowZero: false);
            var already = previous.Where(l => l.TransactionLineId == source.Id).Sum(l => l.QuantityMicros);
            if (quantity > source.QuantityMicros - already) throw new BusinessRequestException(409, "Return exceeds remaining original quantity");
            var unit = await db.ProductUnits.AsNoTracking().SingleAsync(u => u.BusinessId == businessId && u.Id == source.UnitId);
            var product = await db.InventoryProducts.SingleAsync(p => p.BusinessId == businessId && p.Id == source.ProductId);
            var baseQuantity = StockQuantity.ConvertToBase(quantity, source.ConversionFactorMicros);
            if (!unit.AllowsFractionalQuantity && quantity % StockQuantity.Scale != 0 ||
                !product.AllowsFractionalStock && baseQuantity % StockQuantity.Scale != 0)
                throw new BusinessRequestException(400, "Return quantity must respect the original unit precision");
            // Cumulative proration makes the last partial return consume the exact
            // original rounded line total, even for a one-cent multi-unit sale.
            var totalCredit = checked((long)decimal.Round(source.LineTotalMinor * ((decimal)(already + quantity) / source.QuantityMicros), 0, MidpointRounding.AwayFromZero));
            var credit = totalCredit - previous.Where(l => l.TransactionLineId == source.Id).Sum(l => l.AmountMinor);
            correction.AmountMinor = checked(correction.AmountMinor + credit);
            var delta = original.Kind == "Purchase" ? -baseQuantity : line.Restockable ? baseQuantity : 0;
            var movement = await Movement(businessId, product, unit, quantity * (original.Kind == "Purchase" ? -1 : 1), delta,
                correction, sequences);
            // Returns inherit the original sale/purchase classification, even if
            // the product has moved to another category since it was sold.
            var originalMovement = await db.StockMovements.AsNoTracking().SingleAsync(m => m.BusinessId == businessId && m.Id == source.StockMovementId);
            movement.CategoryId = originalMovement.CategoryId;
            movement.CategoryName = originalMovement.CategoryName;
            movement.CategoryRecorded = originalMovement.CategoryRecorded;
            db.InventoryCorrectionLines.Add(new() { BusinessId = businessId, CorrectionId = correction.Id,
                TransactionLineId = source.Id, ProductId = product.Id, StockMovementId = movement.Id,
                QuantityMicros = quantity, AmountMinor = credit, Restockable = line.Restockable });
        }
        if (original.Kind == "Sale")
        {
            var charge = await db.CustomerCharges.AsNoTracking().SingleOrDefaultAsync(c => c.BusinessId == businessId && c.SaleId == original.Id)
                ?? throw new BusinessRequestException(409, "Legacy sale payment history must be reconciled before returning it");
            await AdjustCharge(charge, correction, -correction.AmountMinor, request.RefundAmount, request.RefundMethod);
        }
        else if (request.RefundAmount != 0 || request.RefundMethod is not null)
            throw new BusinessRequestException(400, "Supplier payment/refund accounting is outside purchase stock returns");
        Audit(correction); await db.SaveChangesAsync(); await tx.CommitAsync();
        return await View(correction);
    }

    public async Task<CorrectionDto> StockAsync(Guid businessId, Guid productId, int actor, bool count, AdjustStockDto request)
    {
        Validate(request.Reason, request.IdempotencyKey);
        var fingerprint = Hash(new { productId, count, quantity = Canonical(request.Quantity), expected = Canonical(request.ExpectedQuantityOnHand), request.EffectiveDate, reason = request.Reason.Trim() });
        await using var tx = await SecurityTransaction.BeginAsync(db);
        var business = await Scope(businessId, actor);
        var retry = await Retry(businessId, request.IdempotencyKey, fingerprint);
        if (retry is not null) return await View(retry);
        var product = await db.InventoryProducts.SingleOrDefaultAsync(p => p.BusinessId == businessId && p.Id == productId)
            ?? throw new BusinessRequestException(404, "Product not found");
        if (product.QuantityOnHandMicros != StockQuantity.ToMicros(request.ExpectedQuantityOnHand))
            throw new BusinessRequestException(409, "Stock changed since it was read; review the current balance before correcting");
        var opening = await db.StockMovements.Where(m => m.BusinessId == businessId && m.ProductId == productId && m.Kind == "OpeningStock")
            .Select(m => (DateOnly?)m.EffectiveDate).FirstOrDefaultAsync();
        Date(request.EffectiveDate, business, opening ?? DateOnly.MinValue);
        if (count && request.Quantity < 0) throw new BusinessRequestException(400, "Count cannot be negative");
        var entered = StockQuantity.ToMicros(Math.Abs(request.Quantity));
        var delta = count ? entered - product.QuantityOnHandMicros : entered * Math.Sign(request.Quantity);
        if (!count && delta == 0) throw new BusinessRequestException(400, "Adjustment cannot be zero");
        if (!product.AllowsFractionalStock && entered % StockQuantity.Scale != 0)
            throw new BusinessRequestException(400, "Product requires whole base units");
        var unit = await db.ProductUnits.SingleAsync(u => u.BusinessId == businessId && u.ProductId == productId && u.IsBaseUnit);
        var correction = New(business, actor, count ? "StockCount" : "StockAdjustment", request.EffectiveDate, request.Reason, request.IdempotencyKey, fingerprint);
        correction.DocumentNumber = await DocumentNumbers.NextAsync(db, businessId, "COR");
        db.InventoryCorrections.Add(correction);
        var movement = await Movement(businessId, product, unit, delta, delta, correction, new());
        db.InventoryCorrectionLines.Add(new() { BusinessId = businessId, CorrectionId = correction.Id, ProductId = productId,
            StockMovementId = movement.Id, QuantityMicros = entered });
        Audit(correction); await db.SaveChangesAsync(); await tx.CommitAsync();
        return await View(correction);
    }

    public async Task<CorrectionDto> OpeningDebtAsync(Guid businessId, Guid chargeId, int actor, CorrectOpeningDebtDto request)
    {
        Validate(request.Reason, request.IdempotencyKey);
        var fingerprint = Hash(new { chargeId, amount = Canonical(request.Amount), request.EffectiveDate, reason = request.Reason.Trim(), refund = Canonical(request.RefundAmount), request.RefundMethod });
        await using var tx = await SecurityTransaction.BeginAsync(db);
        var business = await Scope(businessId, actor);
        var retry = await Retry(businessId, request.IdempotencyKey, fingerprint);
        if (retry is not null) return await View(retry);
        var charge = await db.CustomerCharges.SingleOrDefaultAsync(c => c.BusinessId == businessId && c.Id == chargeId && c.Kind == "OpeningDebt")
            ?? throw new BusinessRequestException(404, "Opening debt not found");
        Date(request.EffectiveDate, business, charge.EffectiveDate);
        var current = charge.AmountMinor + (await db.CustomerChargeAdjustments.Where(a => a.BusinessId == businessId && a.ChargeId == chargeId).SumAsync(a => (long?)a.DeltaMinor) ?? 0);
        var target = InventoryMoney.ExactMinor(request.Amount, business.CurrencyDecimalPlaces, allowZero: true);
        var correction = New(business, actor, "OpeningDebtCorrection", request.EffectiveDate, request.Reason, request.IdempotencyKey, fingerprint);
        correction.AmountMinor = target - current;
        if (correction.AmountMinor == 0) throw new BusinessRequestException(400, "Opening debt is already that amount");
        correction.DocumentNumber = await DocumentNumbers.NextAsync(db, businessId, "COR");
        db.InventoryCorrections.Add(correction);
        await AdjustCharge(charge, correction, correction.AmountMinor, request.RefundAmount, request.RefundMethod);
        Audit(correction); await db.SaveChangesAsync(); await tx.CommitAsync(); return await View(correction);
    }

    private async Task AdjustCharge(CustomerCharge charge, InventoryCorrection correction, long delta, decimal refund, string? method)
    {
        var businessId = charge.BusinessId;
        var adjustments = await db.CustomerChargeAdjustments.Where(a => a.BusinessId == businessId && a.ChargeId == charge.Id).ToListAsync();
        var amount = charge.AmountMinor + adjustments.Sum(a => a.DeltaMinor);
        var paid = (await db.CustomerPaymentAllocations.Where(a => a.BusinessId == businessId && a.ChargeId == charge.Id &&
            !db.CustomerPaymentReversals.Any(r => r.BusinessId == businessId && r.PaymentId == a.PaymentId)).SumAsync(a => (long?)a.AmountMinor) ?? 0) - adjustments.Sum(a => a.RefundMinor);
        var required = Math.Max(0, paid - (amount + delta));
        var actual = InventoryMoney.ExactMinor(refund, charge.CurrencyDecimalPlaces, allowZero: true);
        if (amount + delta < 0 || actual != required)
            throw new BusinessRequestException(409, "Refund must equal the paid amount exceeding the corrected charge: " + InventoryMoney.FromMinor(required, charge.CurrencyDecimalPlaces));
        if (actual > 0 && method is not ("Cash" or "BankTransfer" or "Card" or "Other"))
            throw new BusinessRequestException(400, "Record the method of the external refund");
        // Corrections cannot be dated before subsequent receipts or corrections.
        var latestPayment = await (from a in db.CustomerPaymentAllocations join p in db.CustomerPayments
            on new { a.BusinessId, Id = a.PaymentId } equals new { p.BusinessId, p.Id }
            where a.BusinessId == businessId && a.ChargeId == charge.Id select (DateOnly?)p.EffectiveDate).MaxAsync();
        var latestCorrection = await (from a in db.CustomerChargeAdjustments join c in db.InventoryCorrections
            on new { a.BusinessId, Id = a.CorrectionId } equals new { c.BusinessId, c.Id }
            where a.BusinessId == businessId && a.ChargeId == charge.Id select (DateOnly?)c.EffectiveDate).MaxAsync();
        if (correction.EffectiveDate < latestPayment || correction.EffectiveDate < latestCorrection)
            throw new BusinessRequestException(400, "Correction predates related payment/correction history");
        db.CustomerChargeAdjustments.Add(new() { BusinessId = businessId, CorrectionId = correction.Id, ChargeId = charge.Id,
            DeltaMinor = delta, RefundMinor = actual, RefundMethod = actual > 0 ? method : null });
    }

    private async Task<StockMovement> Movement(Guid businessId, InventoryProduct product, ProductUnit unit, long quantity,
        long delta, InventoryCorrection correction, Dictionary<Guid, long> sequences)
    {
        var balance = checked(product.QuantityOnHandMicros + delta);
        if (balance < 0 || balance > StockQuantity.MaxBalanceMicros) throw new BusinessRequestException(409, "Correction would exceed stock bounds");
        if (!sequences.TryGetValue(product.Id, out var sequence))
            sequence = await db.StockMovements.Where(m => m.BusinessId == businessId && m.ProductId == product.Id).MaxAsync(m => (long?)m.SequenceNumber) ?? 0;
        sequences[product.Id] = ++sequence; product.QuantityOnHandMicros = balance;
        var movement = new StockMovement { BusinessId = businessId, ProductId = product.Id, UnitId = unit.Id,
            SequenceNumber = sequence, Kind = correction.Kind, UnitName = unit.Name, UnitQuantityMicros = quantity,
            ConversionFactorMicros = unit.ConversionFactorMicros, BaseQuantityMicros = delta, BalanceAfterMicros = balance,
            Reason = correction.Reason, EffectiveDate = correction.EffectiveDate, RecordedAtUtc = correction.RecordedAtUtc,
            ActorUserId = correction.ActorUserId, IdempotencyKey = "cor-" + Guid.NewGuid().ToString("N"), RequestFingerprint = correction.RequestFingerprint };
        await InventoryMasterData.SnapshotAsync(db, product, movement);
        db.StockMovements.Add(movement); return movement;
    }

    public async Task<object> DetailAsync(Guid businessId, Guid correctionId, int actor)
    {
        await Scope(businessId, actor);
        var correction = await db.InventoryCorrections.AsNoTracking().SingleOrDefaultAsync(c => c.BusinessId == businessId && c.Id == correctionId)
            ?? throw new BusinessRequestException(404, "Correction not found");
        var lines = await db.InventoryCorrectionLines.AsNoTracking().Where(l => l.BusinessId == businessId && l.CorrectionId == correctionId)
            .OrderBy(l => l.Id).ToListAsync();
        var financial = await db.CustomerChargeAdjustments.AsNoTracking().SingleOrDefaultAsync(a => a.BusinessId == businessId && a.CorrectionId == correctionId);
        // Expose the linked movement so the count/adjustment's signed delta and
        // resulting balance are reviewable without changing any original rows.
        var movementIds = lines.Select(l => l.StockMovementId).ToArray();
        var movements = await db.StockMovements.AsNoTracking().Where(m => m.BusinessId == businessId && movementIds.Contains(m.Id)).ToListAsync();
        return new { Correction = await View(correction), Lines = lines.Select(l => new {
            l.Id, l.ProductId, l.TransactionLineId, l.StockMovementId, l.Restockable,
            Quantity = StockQuantity.FromMicros(l.QuantityMicros), Amount = InventoryMoney.FromMinor(l.AmountMinor, correction.CurrencyDecimalPlaces),
            BaseDelta = StockQuantity.FromMicros(movements.Single(m => m.Id == l.StockMovementId).BaseQuantityMicros),
            BalanceAfter = StockQuantity.FromMicros(movements.Single(m => m.Id == l.StockMovementId).BalanceAfterMicros) }),
            ChargeId = financial?.ChargeId, ChargeDelta = InventoryMoney.FromMinor(financial?.DeltaMinor ?? 0, correction.CurrencyDecimalPlaces), financial?.RefundMethod };
    }

    public async Task<PagedResponseDto<CorrectionDto>> ListAsync(Guid businessId, int actor, int page, int size)
    {
        if (page is < 1 or > 1000 || size is < 1 or > 100 || (long)(page - 1) * size > 10000) throw new BusinessRequestException(400, "Invalid pagination");
        await Scope(businessId, actor);
        var query = db.InventoryCorrections.AsNoTracking().Where(c => c.BusinessId == businessId);
        var count = await query.CountAsync();
        var rows = await query.OrderByDescending(c => c.RecordedAtUtc).ThenBy(c => c.Id).Skip((page - 1) * size).Take(size).ToListAsync();
        var views = new List<CorrectionDto>(); foreach (var row in rows) views.Add(await View(row));
        return new(views, page, size, count, (int)Math.Ceiling(count / (double)size));
    }
    private async Task<InventoryCorrection?> Retry(Guid businessId, string key, string hash)
    {
        var existing = await db.InventoryCorrections.AsNoTracking().SingleOrDefaultAsync(c => c.BusinessId == businessId && c.IdempotencyKey == key);
        if (existing is not null && existing.RequestFingerprint != hash) throw new BusinessRequestException(409, "Idempotency key already used for a different correction");
        return existing;
    }
    private async Task<CorrectionDto> View(InventoryCorrection c) => new(c.Id, c.Kind, c.TransactionId, c.Reason, c.EffectiveDate,
        c.RecordedAtUtc, c.ActorUserId, c.Currency, InventoryMoney.FromMinor(c.AmountMinor, c.CurrencyDecimalPlaces),
        InventoryMoney.FromMinor(await db.CustomerChargeAdjustments.Where(a => a.BusinessId == c.BusinessId && a.CorrectionId == c.Id).SumAsync(a => (long?)a.RefundMinor) ?? 0, c.CurrencyDecimalPlaces), c.DocumentNumber);
    private InventoryCorrection New(Business b, int actor, string kind, DateOnly date, string reason, string key, string hash) => new()
        { BusinessId = b.Id, ActorUserId = actor, Kind = kind, EffectiveDate = date, Reason = reason.Trim(), RecordedAtUtc = clock.GetUtcNow().UtcDateTime,
          Currency = b.Currency, CurrencyDecimalPlaces = b.CurrencyDecimalPlaces, IdempotencyKey = key, RequestFingerprint = hash };
    private void Audit(InventoryCorrection c) => db.BusinessAuditEvents.Add(new() { BusinessId = c.BusinessId, ActorUserId = c.ActorUserId, Action = c.Kind, CreatedAtUtc = c.RecordedAtUtc });
    private void Date(DateOnly date, Business business, DateOnly earliest)
    {
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.GetUtcNow(), TimeZoneInfo.FindSystemTimeZoneById(business.TimeZoneId)).DateTime);
        if (date == default || date > today || date < earliest) throw new BusinessRequestException(400, "Correction date is outside the permitted range");
    }
    private static void Validate(string reason, string key)
    {
        if (reason?.Trim().Length is not (>= 3 and <= 300) || key is null || !System.Text.RegularExpressions.Regex.IsMatch(key, "^[A-Za-z0-9._-]{8,64}$"))
            throw new BusinessRequestException(400, "A reason and valid idempotency key are required");
    }
    private static string Canonical(decimal value) => value.ToString("G29", System.Globalization.CultureInfo.InvariantCulture);
    private static string Hash(object value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value))));
}
