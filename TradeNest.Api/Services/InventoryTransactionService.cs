using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TradeNest.Api.Data;
using TradeNest.Api.Dtos;
using TradeNest.Api.Entities;
using Microsoft.EntityFrameworkCore;

namespace TradeNest.Api.Services;

// All posting uses a SQLite writer transaction BEFORE authorization/stock reads.
// Headers, line snapshots, running balances, movements, and audit events either
// commit together or roll back together, including later-line validation failures.
public sealed class InventoryTransactionService(TradeNestContext db, BusinessAccess access, TimeProvider clock, CustomerLedgerService ledger)
{
    private async Task<BusinessMembership> Scope(Guid businessId, int actorId)
    {
        db.InventoryBusinessId = null;
        var member = await access.RequireAsync(businessId, actorId);
        if (member.Role == BusinessRole.Viewer)
            throw new BusinessRequestException(403, "Sales and purchase permission required");
        db.InventoryBusinessId = businessId;
        return member;
    }

    public Task<InventoryTransactionDto> RecordAsync(Guid businessId, int actorId, string kind, RecordInventoryTransactionDto request) =>
        RecordCore(businessId, actorId, kind, request, preview: false);

    // The preview runs the exact posting validation, then rolls back every write.
    // A subsequent POST rechecks stock/authority; a preview reserves nothing.
    private async Task<InventoryTransactionDto> RecordCore(Guid businessId, int actorId, string kind, RecordInventoryTransactionDto request, bool preview)
    {
        Kind(kind);
        if (request.Lines is null || request.Lines.Count is < 1 or > 100 || request.Lines.Any(item => item is null))
            throw new BusinessRequestException(400, "A transaction requires one to one hundred lines");
        if (request.EffectiveDate == default) throw new BusinessRequestException(400, "Effective date is required");
        if (kind == "Sale" && request.SupplierId.HasValue)
            throw new BusinessRequestException(400, "A sale cannot have a supplier");
        if (kind == "Purchase" && (request.CustomerId.HasValue || request.AmountPaid.HasValue || request.PaymentMethod is not null || request.DueDate.HasValue))
            throw new BusinessRequestException(400, "Customer/payment fields belong to sales, not purchase receipts");
        var reference = string.IsNullOrWhiteSpace(request.Reference) ? null : request.Reference.Trim();
        var fingerprint = Fingerprint(kind, request, reference);
        await using var transaction = await SecurityTransaction.BeginAsync(db);
        var member = await Scope(businessId, actorId);
        var existing = await db.InventoryTransactions.AsNoTracking().SingleOrDefaultAsync(item =>
            item.BusinessId == businessId && item.IdempotencyKey == request.IdempotencyKey);
        if (existing is not null)
        {
            // Another staff member cannot use a guessed key to retrieve someone
            // else's financial record. Admins may inspect all business documents.
            if (existing.RequestFingerprint != fingerprint ||
                existing.ActorUserId != actorId && !BusinessAccess.IsAdministrator(member.Role))
                throw new BusinessRequestException(409, "Idempotency key is already in use");
            return await Detail(existing);
        }

        var business = await db.Businesses.AsNoTracking().SingleAsync(item => item.Id == businessId);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.GetUtcNow(), TimeZoneInfo.FindSystemTimeZoneById(business.TimeZoneId)).DateTime);
        if (request.EffectiveDate > today) throw new BusinessRequestException(400, "Transactions cannot be future-dated");
        if (request.SupplierId.HasValue && !await db.Suppliers.AnyAsync(item => item.BusinessId == businessId && item.Id == request.SupplierId))
            throw new BusinessRequestException(404, "Supplier not found");
        _ = InventoryMoney.MinorScale(business.CurrencyDecimalPlaces);
        var document = new InventoryTransaction { BusinessId = businessId, Kind = kind, SupplierId = request.SupplierId,
            DocumentNumber = await DocumentNumbers.NextAsync(db, businessId, kind == "Sale" ? "SALE" : "PUR"),
            Reference = reference, Currency = business.Currency, CurrencyDecimalPlaces = business.CurrencyDecimalPlaces,
            EffectiveDate = request.EffectiveDate, RecordedAtUtc = clock.GetUtcNow().UtcDateTime, ActorUserId = actorId,
            IdempotencyKey = request.IdempotencyKey, RequestFingerprint = fingerprint };
        db.InventoryTransactions.Add(document);
        var lines = new List<InventoryTransactionLine>();
        var sequences = new Dictionary<Guid, long>();
        foreach (var input in request.Lines)
        {
            var quantity = StockQuantity.ToMicros(input.Quantity, allowZero: false);
            var price = InventoryMoney.PriceMicros(input.UnitPrice);
            // Tracking deliberately reuses the updated balance for repeated product
            // lines, so splitting a sale cannot bypass the available-stock check.
            var product = await db.InventoryProducts.SingleOrDefaultAsync(item => item.BusinessId == businessId && item.Id == input.ProductId)
                ?? throw new BusinessRequestException(404, "Product not found");
            if (!product.IsActive) throw new BusinessRequestException(409, "Product is inactive");
            var unit = await db.ProductUnits.AsNoTracking().SingleOrDefaultAsync(item =>
                item.BusinessId == businessId && item.ProductId == input.ProductId && item.Id == input.UnitId)
                ?? throw new BusinessRequestException(404, "Product unit not found");
            Whole(quantity, unit.AllowsFractionalQuantity);
            var baseQuantity = StockQuantity.ConvertToBase(quantity, unit.ConversionFactorMicros);
            Whole(baseQuantity, product.AllowsFractionalStock);
            var openingDate = await db.StockMovements.Where(item => item.BusinessId == businessId && item.ProductId == product.Id && item.Kind == "OpeningStock")
                .Select(item => (DateOnly?)item.EffectiveDate).SingleOrDefaultAsync();
            if (openingDate.HasValue && request.EffectiveDate < openingDate.Value)
                throw new BusinessRequestException(400, "Transaction predates this product's opening balance");
            var delta = kind == "Purchase" ? baseQuantity : -baseQuantity;
            var balance = product.QuantityOnHandMicros + delta;
            if (balance < 0) throw new BusinessRequestException(409, "Insufficient stock for this sale");
            if (balance > StockQuantity.MaxBalanceMicros) throw new BusinessRequestException(400, "Stock balance exceeds supported limits");
            var amount = InventoryMoney.LineTotal(quantity, price, business.CurrencyDecimalPlaces);
            document.TotalMinor += amount;
            if (InventoryMoney.FromMinor(document.TotalMinor, business.CurrencyDecimalPlaces) > StockQuantity.FromMicros(StockQuantity.MaxBalanceMicros))
                throw new BusinessRequestException(400, "Transaction total exceeds supported limits");
            if (!sequences.TryGetValue(product.Id, out var sequence))
                sequence = await db.StockMovements.Where(item => item.BusinessId == businessId && item.ProductId == product.Id)
                    .Select(item => (long?)item.SequenceNumber).MaxAsync() ?? 0;
            sequences[product.Id] = ++sequence;
            var movement = new StockMovement { BusinessId = businessId, ProductId = product.Id, UnitId = unit.Id,
                SequenceNumber = sequence, Kind = kind, UnitName = unit.Name, UnitQuantityMicros = kind == "Purchase" ? quantity : -quantity,
                ConversionFactorMicros = unit.ConversionFactorMicros, BaseQuantityMicros = delta, BalanceAfterMicros = balance,
                Reason = $"{kind} {reference ?? document.Id.ToString()}", EffectiveDate = request.EffectiveDate,
                RecordedAtUtc = document.RecordedAtUtc, ActorUserId = actorId,
                IdempotencyKey = "txn-" + Guid.NewGuid().ToString("N"), RequestFingerprint = fingerprint };
            await InventoryMasterData.SnapshotAsync(db, product, movement);
            var line = new InventoryTransactionLine { BusinessId = businessId, TransactionId = document.Id, Position = lines.Count + 1,
                ProductId = product.Id, UnitId = unit.Id, StockMovementId = movement.Id, ProductName = product.Name, Sku = product.Sku,
                UnitName = unit.Name, QuantityMicros = quantity, ConversionFactorMicros = unit.ConversionFactorMicros,
                BaseQuantityMicros = baseQuantity, UnitPriceMicros = price, LineTotalMinor = amount };
            db.StockMovements.Add(movement);
            db.InventoryTransactionLines.Add(line);
            product.QuantityOnHandMicros = balance;
            lines.Add(line);
        }
        // Extend the SAME transaction with the sale charge and initial payment.
        // Failure of customer/payment validation must roll back stock as well.
        if (kind == "Sale") await ledger.PostSaleAsync(document, request, business);
        db.BusinessAuditEvents.Add(new() { BusinessId = businessId, ActorUserId = actorId,
            Action = kind + "Recorded", CreatedAtUtc = document.RecordedAtUtc });
        await db.SaveChangesAsync();
        if (!preview) await transaction.CommitAsync();
        return await Detail(document);
    }

    public async Task<PagedResponseDto<InventoryTransactionSummaryDto>> ListAsync(Guid businessId, int actorId, string kind, int page, int size, TransactionFilters? filters = null)
    {
        Kind(kind); Page(page, size);
        var member = await Scope(businessId, actorId);
        var query = VisibleDocuments(businessId, actorId, member).Where(item => item.Kind == kind);
        filters ??= new();
        if (filters.From > filters.To || filters.Search?.Length > 100) throw new BusinessRequestException(400, "Invalid date range or search length");
        if (filters.From.HasValue) query = query.Where(t => t.EffectiveDate >= filters.From.Value);
        if (filters.To.HasValue) query = query.Where(t => t.EffectiveDate <= filters.To.Value);
        if (filters.CustomerId.HasValue) query = query.Where(t => t.CustomerId == filters.CustomerId);
        if (filters.SupplierId.HasValue) query = query.Where(t => t.SupplierId == filters.SupplierId);
        if (filters.StaffUserId.HasValue) query = query.Where(t => t.ActorUserId == filters.StaffUserId);
        if (filters.ProductId.HasValue) query = query.Where(t => db.InventoryTransactionLines.Any(l => l.BusinessId == businessId && l.TransactionId == t.Id && l.ProductId == filters.ProductId));
        if (!string.IsNullOrWhiteSpace(filters.Search))
        {
            var pattern = BusinessSearch.ContainsPattern(filters.Search);
            query = query.Where(t => (t.DocumentNumber != null && EF.Functions.Like(t.DocumentNumber, pattern, "\\")) || (t.Reference != null && EF.Functions.Like(t.Reference, pattern, "\\")) || (t.CustomerName != null && EF.Functions.Like(t.CustomerName, pattern, "\\")));
        }
        var count = await query.CountAsync();
        var rows = await query.OrderByDescending(item => item.RecordedAtUtc).ThenBy(item => item.Id)
            .Skip((page - 1) * size).Take(size).ToListAsync();
        var statuses = await ledger.DescribeSalesAsync(businessId, rows.Select(row => row.Id));
        return new(rows.Select(row => Summary(row, statuses.GetValueOrDefault(row.Id))).ToList(), page, size, count, (int)Math.Ceiling(count / (double)size));
    }

    public async Task<TransactionEntryPreviewDto> PreviewAsync(Guid businessId, int actorId, string kind, RecordInventoryTransactionDto request)
    {
        var entry = await RecordCore(businessId, actorId, kind, request, preview: true);
        var member = await Scope(businessId, actorId);
        var visible = VisibleDocuments(businessId, actorId, member).Where(t => t.Kind == kind);
        var alreadyRecorded = await visible.AnyAsync(t => t.Id == entry.Transaction.Id);
        var reference = string.IsNullOrWhiteSpace(request.Reference) ? null : request.Reference.Trim();
        var hash = Fingerprint(kind, request, reference);
        var referencePattern = reference is null ? null : BusinessSearch.Escape(reference);
        var duplicates = await visible.Where(t => t.Id != entry.Transaction.Id && (t.RequestFingerprint == hash || (referencePattern != null && t.Reference != null && EF.Functions.Like(t.Reference, referencePattern, "\\"))))
            .OrderByDescending(t => t.RecordedAtUtc).ThenBy(t => t.Id).Take(5)
            .Select(t => new DuplicateDocumentDto(t.Id, t.DocumentNumber, t.EffectiveDate, t.Reference)).ToListAsync();
        // Transient preview IDs/numbers must not look like committed references.
        if (!alreadyRecorded) entry = new(entry.Transaction with { Id = Guid.Empty, DocumentNumber = null },
            entry.Lines.Select(l => l with { Id = Guid.Empty, StockMovementId = Guid.Empty }).ToList());
        return new(entry, alreadyRecorded, duplicates);
    }

    public async Task<InventoryTransactionDto> GetAsync(Guid businessId, Guid id, int actorId, string kind)
    {
        Kind(kind);
        var member = await Scope(businessId, actorId);
        var document = await VisibleDocuments(businessId, actorId, member).SingleOrDefaultAsync(item => item.Id == id && item.Kind == kind)
            ?? throw new BusinessRequestException(404, "Transaction not found");
        return await Detail(document);
    }

    public async Task<SupplierDto> CreateSupplierAsync(Guid businessId, int actorId, CreateSupplierDto request)
    {
        var name = request.Name.Trim();
        if (name.Length < 2 || name.Length > 160) throw new BusinessRequestException(400, "Supplier name must contain two to 160 characters");
        await using var transaction = await SecurityTransaction.BeginAsync(db);
        var member = await Scope(businessId, actorId);
        if (!BusinessAccess.IsAdministrator(member.Role)) throw new BusinessRequestException(403, "Business administrator permission required");
        var normalized = name.ToUpperInvariant();
        if (await db.Suppliers.AnyAsync(item => item.BusinessId == businessId && item.NormalizedName == normalized))
            throw new BusinessRequestException(409, "Supplier name already exists in this business");
        var supplier = new Supplier { BusinessId = businessId, Name = name, NormalizedName = normalized,
            ContactReference = string.IsNullOrWhiteSpace(request.ContactReference) ? null : request.ContactReference.Trim() };
        db.Suppliers.Add(supplier);
        db.BusinessAuditEvents.Add(new() { BusinessId = businessId, ActorUserId = actorId, Action = "SupplierCreated", CreatedAtUtc = clock.GetUtcNow().UtcDateTime });
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return new(supplier.Id, supplier.Name, supplier.ContactReference);
    }

    public async Task<PagedResponseDto<SupplierDto>> SuppliersAsync(Guid businessId, int actorId, int page, int size)
    {
        Page(page, size); await Scope(businessId, actorId);
        var query = db.Suppliers.AsNoTracking().Where(item => item.BusinessId == businessId);
        var count = await query.CountAsync();
        var rows = await query.OrderBy(item => item.NormalizedName).ThenBy(item => item.Id).Skip((page - 1) * size).Take(size)
            .Select(item => new SupplierDto(item.Id, item.Name, item.ContactReference)).ToListAsync();
        return new(rows, page, size, count, (int)Math.Ceiling(count / (double)size));
    }

    private IQueryable<InventoryTransaction> VisibleDocuments(Guid businessId, int actorId, BusinessMembership member)
    {
        var query = db.InventoryTransactions.AsNoTracking().Where(item => item.BusinessId == businessId);
        // Staff need to check their own entries; cross-staff financial visibility
        // is reserved for administrators until configurable reporting permissions exist.
        return BusinessAccess.IsAdministrator(member.Role) ? query : query.Where(item => item.ActorUserId == actorId);
    }
    private async Task<InventoryTransactionDto> Detail(InventoryTransaction document)
    {
        var lines = await db.InventoryTransactionLines.AsNoTracking().Where(item => item.BusinessId == document.BusinessId && item.TransactionId == document.Id)
            .OrderBy(item => item.Position).ToListAsync();
        var statuses = await ledger.DescribeSalesAsync(document.BusinessId, new[] { document.Id });
        return new(Summary(document, statuses.GetValueOrDefault(document.Id)), lines.Select(item => Line(item, document.CurrencyDecimalPlaces)).ToList());
    }
    private static InventoryTransactionSummaryDto Summary(InventoryTransaction item, SaleFinancialStatus? status = null) => new(item.Id, item.Kind, item.SupplierId,
        item.Reference, item.Currency, item.CurrencyDecimalPlaces, InventoryMoney.FromMinor(item.TotalMinor, item.CurrencyDecimalPlaces),
        item.EffectiveDate, item.RecordedAtUtc, item.ActorUserId, item.CustomerId, item.CustomerName,
        status?.PaymentsTracked ?? false, status?.AmountPaid, status?.Outstanding, item.DocumentNumber);
    private static InventoryTransactionLineDto Line(InventoryTransactionLine item, int places) => new(item.Id, item.ProductId,
        item.UnitId, item.StockMovementId, item.ProductName, item.Sku, item.UnitName, StockQuantity.FromMicros(item.QuantityMicros),
        StockQuantity.FromMicros(item.ConversionFactorMicros), StockQuantity.FromMicros(item.BaseQuantityMicros),
        StockQuantity.FromMicros(item.UnitPriceMicros), InventoryMoney.FromMinor(item.LineTotalMinor, places));
    private static void Whole(long quantity, bool fractional)
    {
        if (!fractional && quantity % StockQuantity.Scale != 0) throw new BusinessRequestException(400, "This unit requires whole quantities");
    }
    private static void Kind(string kind)
    {
        if (kind is not ("Purchase" or "Sale")) throw new BusinessRequestException(400, "Unknown transaction kind");
    }
    private static void Page(int page, int size)
    {
        if (page is < 1 or > 1000 || size is < 1 or > 100 || (long)(page - 1) * size > 10_000)
            throw new BusinessRequestException(400, "Invalid pagination parameters");
    }
    private static string Fingerprint(string kind, RecordInventoryTransactionDto request, string? reference)
    {
        // Canonical decimal strings treat 2 and 2.00 as the same input. Line order
        // is preserved because it determines the recorded running balances.
        var value = JsonSerializer.Serialize(new { kind, request.EffectiveDate, request.SupplierId, reference,
            request.CustomerId, request.DueDate, paid = (request.AmountPaid ?? 0).ToString("G29", CultureInfo.InvariantCulture), request.PaymentMethod,
            lines = request.Lines.Select(item => new { item.ProductId, item.UnitId,
                quantity = item.Quantity.ToString("G29", CultureInfo.InvariantCulture),
                price = item.UnitPrice.ToString("G29", CultureInfo.InvariantCulture) }) });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }
}
