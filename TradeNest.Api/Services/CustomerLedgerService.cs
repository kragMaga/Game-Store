using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TradeNest.Api.Data;
using TradeNest.Api.Dtos;
using TradeNest.Api.Entities;
using Microsoft.EntityFrameworkCore;

namespace TradeNest.Api.Services;

// Debt is derived from immutable charges and effective payment allocations.
// There is deliberately no editable Customer.Balance field to drift out of sync.
public sealed class CustomerLedgerService(TradeNestContext db, BusinessAccess access, TimeProvider clock)
{
    private async Task<BusinessMembership> Scope(Guid businessId, int actorId, bool admin = false, bool finance = false)
    {
        db.InventoryBusinessId = null;
        var member = await access.RequireAsync(businessId, actorId, admin);
        if (member.Role == BusinessRole.Viewer || finance && !BusinessAccess.CanRecordPayments(member))
            throw new BusinessRequestException(403, "Customer payment permission required");
        db.InventoryBusinessId = businessId;
        return member;
    }

    public async Task<CustomerDto> CreateAsync(Guid businessId, int actorId, CreateCustomerDto request)
    {
        var name = Text(request.Name, 160);
        await using var tx = await SecurityTransaction.BeginAsync(db);
        await Scope(businessId, actorId, admin: true);
        var normalized = request.Code.ToUpperInvariant();
        if (await db.Customers.AnyAsync(c => c.BusinessId == businessId && c.NormalizedCode == normalized))
            throw new BusinessRequestException(409, "Customer code already exists in this business");
        var customer = new Customer { BusinessId = businessId, Code = request.Code, NormalizedCode = normalized,
            Name = name, Email = Optional(request.Email), Phone = Optional(request.Phone), CreatedAtUtc = clock.GetUtcNow().UtcDateTime };
        db.Customers.Add(customer); Audit(businessId, actorId, "CustomerCreated");
        await db.SaveChangesAsync(); await tx.CommitAsync();
        return CustomerView(customer);
    }

    public async Task UpdateAsync(Guid businessId, Guid customerId, int actorId, UpdateCustomerDto request)
    {
        var name = Text(request.Name, 160);
        await using var tx = await SecurityTransaction.BeginAsync(db);
        await Scope(businessId, actorId, admin: true);
        var customer = await db.Customers.SingleOrDefaultAsync(c => c.BusinessId == businessId && c.Id == customerId)
            ?? throw new BusinessRequestException(404, "Customer not found");
        customer.Name = name; customer.Email = Optional(request.Email); customer.Phone = Optional(request.Phone); customer.IsActive = request.IsActive;
        Audit(businessId, actorId, "CustomerUpdated");
        await db.SaveChangesAsync(); await tx.CommitAsync();
        // Archiving stops new sales, but does not prevent settlement of old debt.
    }

    public async Task<PagedResponseDto<CustomerDto>> ListAsync(Guid businessId, int actorId, int page, int size, string? search, bool includeInactive)
    {
        Page(page, size); await Scope(businessId, actorId);
        if (search?.Length > 100) throw new BusinessRequestException(400, "Search cannot exceed 100 characters");
        var query = db.Customers.AsNoTracking().Where(c => c.BusinessId == businessId && (includeInactive || c.IsActive));
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim(); var code = term.ToUpperInvariant();
            var pattern = "%" + term.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
            query = query.Where(c => EF.Functions.Like(c.Name, pattern, "\\") || c.NormalizedCode.Contains(code));
        }
        var count = await query.CountAsync();
        var rows = await query.OrderBy(c => c.NormalizedCode).ThenBy(c => c.Id).Skip((page - 1) * size).Take(size).ToListAsync();
        return Paged(rows.Select(CustomerView).ToList(), page, size, count);
    }

    public async Task<CustomerChargeDto> OpeningAsync(Guid businessId, Guid customerId, int actorId, OpeningCustomerDebtDto request)
    {
        var reason = Text(request.Reason, 300, 3);
        var hash = Hash(new { customerId, amount = Decimal(request.Amount), request.EffectiveDate, request.DueDate, reason });
        await using var tx = await SecurityTransaction.BeginAsync(db);
        await Scope(businessId, actorId, admin: true);
        var existing = await db.CustomerCharges.AsNoTracking().SingleOrDefaultAsync(c => c.BusinessId == businessId && c.IdempotencyKey == request.IdempotencyKey);
        if (existing is not null)
        {
            if (existing.RequestFingerprint != hash) throw new BusinessRequestException(409, "Idempotency key is already in use");
            return ChargeView(await Rows(businessId).SingleAsync(c => c.Id == existing.Id));
        }
        var customer = await FindCustomer(businessId, customerId);
        if (!customer.IsActive) throw new BusinessRequestException(409, "Customer is inactive");
        if (await db.CustomerCharges.AnyAsync(c => c.BusinessId == businessId && c.CustomerId == customerId))
            throw new BusinessRequestException(409, "Opening debt must precede other customer charges and can be recorded only once");
        var business = await Business(businessId);
        Dates(request.EffectiveDate, request.DueDate, business);
        var charge = new CustomerCharge { BusinessId = businessId, CustomerId = customerId, Kind = "OpeningDebt",
            AmountMinor = InventoryMoney.ExactMinor(request.Amount, business.CurrencyDecimalPlaces, allowZero: true), Currency = business.Currency,
            CurrencyDecimalPlaces = business.CurrencyDecimalPlaces, EffectiveDate = request.EffectiveDate, DueDate = request.DueDate,
            Reason = reason, ActorUserId = actorId, RecordedAtUtc = clock.GetUtcNow().UtcDateTime,
            IdempotencyKey = request.IdempotencyKey, RequestFingerprint = hash };
        db.CustomerCharges.Add(charge); Audit(businessId, actorId, "OpeningCustomerDebtRecorded");
        await db.SaveChangesAsync(); await tx.CommitAsync();
        return ChargeView(new ChargeRow { Id = charge.Id, Kind = charge.Kind, EffectiveDate = charge.EffectiveDate,
            DueDate = charge.DueDate, Reason = reason, Amount = charge.AmountMinor, Places = charge.CurrencyDecimalPlaces });
    }

    // Called only inside the sale's existing writer transaction. Do not open a
    // nested transaction: sale, stock, charge, and initial receipt must be atomic.
    internal async Task PostSaleAsync(InventoryTransaction sale, RecordInventoryTransactionDto request, Business business)
    {
        Customer? customer = null;
        if (request.CustomerId.HasValue)
        {
            customer = await FindCustomer(sale.BusinessId, request.CustomerId.Value);
            if (!customer.IsActive) throw new BusinessRequestException(409, "Customer is inactive");
            sale.CustomerId = customer.Id; sale.CustomerName = customer.Name;
        }
        Dates(sale.EffectiveDate, request.DueDate, business);
        if (customer is not null)
        {
            var openingDate = await db.CustomerCharges.Where(c => c.BusinessId == sale.BusinessId && c.CustomerId == customer.Id && c.Kind == "OpeningDebt")
                .Select(c => (DateOnly?)c.EffectiveDate).SingleOrDefaultAsync();
            if (openingDate.HasValue && sale.EffectiveDate < openingDate)
                throw new BusinessRequestException(400, "Sale predates the customer's opening debt");
        }
        var paid = InventoryMoney.ExactMinor(request.AmountPaid ?? 0, sale.CurrencyDecimalPlaces, allowZero: true);
        if (paid > sale.TotalMinor) throw new BusinessRequestException(400, "Payment cannot exceed the sale total");
        if (paid < sale.TotalMinor && customer is null)
            throw new BusinessRequestException(400, "A customer is required for an unpaid or partially paid sale");
        if (customer is null && request.DueDate.HasValue)
            throw new BusinessRequestException(400, "Walk-in sales cannot have a debt due date");
        var charge = new CustomerCharge { BusinessId = sale.BusinessId, CustomerId = customer?.Id, SaleId = sale.Id, Kind = "Sale",
            AmountMinor = sale.TotalMinor, Currency = sale.Currency, CurrencyDecimalPlaces = sale.CurrencyDecimalPlaces,
            EffectiveDate = sale.EffectiveDate, DueDate = request.DueDate, Reason = "Recorded sale", ActorUserId = sale.ActorUserId,
            RecordedAtUtc = sale.RecordedAtUtc, IdempotencyKey = "sale-" + sale.Id.ToString("N"), RequestFingerprint = sale.RequestFingerprint };
        db.CustomerCharges.Add(charge);
        if (paid > 0)
        {
            Method(request.PaymentMethod);
            var payment = new CustomerPayment { DocumentNumber = await DocumentNumbers.NextAsync(db, sale.BusinessId, "PAY"), BusinessId = sale.BusinessId, CustomerId = customer?.Id, Kind = "AtSale",
                Method = request.PaymentMethod!, AmountMinor = paid, Currency = sale.Currency, CurrencyDecimalPlaces = sale.CurrencyDecimalPlaces,
                EffectiveDate = sale.EffectiveDate, Reference = sale.Reference, ActorUserId = sale.ActorUserId, RecordedAtUtc = sale.RecordedAtUtc,
                IdempotencyKey = "sale-" + sale.Id.ToString("N"), RequestFingerprint = sale.RequestFingerprint };
            db.CustomerPayments.Add(payment);
            db.CustomerPaymentAllocations.Add(new() { BusinessId = sale.BusinessId, PaymentId = payment.Id, ChargeId = charge.Id, AmountMinor = paid });
        }
        else if (request.PaymentMethod is not null) Method(request.PaymentMethod);
    }

    public Task<CustomerPaymentDto> PayAsync(Guid businessId, Guid customerId, int actorId, RecordCustomerPaymentDto request) =>
        PayCore(businessId, customerId, actorId, request, preview: false);
    private async Task<CustomerPaymentDto> PayCore(Guid businessId, Guid customerId, int actorId, RecordCustomerPaymentDto request, bool preview)
    {
        if (request.Allocations is null || request.Allocations.Count is < 1 or > 100 || request.Allocations.Any(a => a is null) ||
            request.Allocations.Select(a => a.ChargeId).Distinct().Count() != request.Allocations.Count)
            throw new BusinessRequestException(400, "Provide one to one hundred distinct charge allocations");
        Method(request.Method);
        var reference = Optional(request.Reference);
        var hash = Hash(new { customerId, amount = Decimal(request.Amount), request.EffectiveDate, request.Method, reference,
            allocations = request.Allocations.OrderBy(a => a.ChargeId).Select(a => new { a.ChargeId, amount = Decimal(a.Amount) }) });
        await using var tx = await SecurityTransaction.BeginAsync(db);
        await Scope(businessId, actorId, finance: true);
        var existing = await db.CustomerPayments.AsNoTracking().SingleOrDefaultAsync(p => p.BusinessId == businessId && p.IdempotencyKey == request.IdempotencyKey);
        if (existing is not null)
        {
            if (existing.RequestFingerprint != hash) throw new BusinessRequestException(409, "Idempotency key is already in use");
            return PaymentView(existing, await IsReversed(businessId, existing.Id));
        }
        _ = await FindCustomer(businessId, customerId);
        var business = await Business(businessId); Dates(request.EffectiveDate, null, business);
        var amount = InventoryMoney.ExactMinor(request.Amount, business.CurrencyDecimalPlaces);
        var payment = new CustomerPayment { DocumentNumber = await DocumentNumbers.NextAsync(db, businessId, "PAY"), BusinessId = businessId, CustomerId = customerId, Kind = "LaterPayment", Method = request.Method,
            AmountMinor = amount, Currency = business.Currency, CurrencyDecimalPlaces = business.CurrencyDecimalPlaces,
            EffectiveDate = request.EffectiveDate, Reference = reference, ActorUserId = actorId, RecordedAtUtc = clock.GetUtcNow().UtcDateTime,
            IdempotencyKey = request.IdempotencyKey, RequestFingerprint = hash };
        long allocated = 0;
        foreach (var allocation in request.Allocations)
        {
            var charge = await Rows(businessId).SingleOrDefaultAsync(c => c.Id == allocation.ChargeId && c.CustomerId == customerId)
                ?? throw new BusinessRequestException(404, "Customer charge not found");
            var part = InventoryMoney.ExactMinor(allocation.Amount, business.CurrencyDecimalPlaces);
            if (charge.Currency != business.Currency || charge.Places != business.CurrencyDecimalPlaces)
                throw new BusinessRequestException(409, "Charge currency does not match business settings");
            if (part > charge.Amount - charge.Paid) throw new BusinessRequestException(409, "Allocation exceeds the outstanding charge");
            if (request.EffectiveDate < charge.EffectiveDate) throw new BusinessRequestException(400, "Payment predates its charge");
            allocated = checked(allocated + part);
            db.CustomerPaymentAllocations.Add(new() { BusinessId = businessId, PaymentId = payment.Id, ChargeId = charge.Id, AmountMinor = part });
        }
        if (allocated != amount) throw new BusinessRequestException(400, "Allocations must equal the payment amount; unallocated credit is not supported");
        db.CustomerPayments.Add(payment); Audit(businessId, actorId, "CustomerPaymentRecorded");
        await db.SaveChangesAsync(); if (!preview) await tx.CommitAsync();
        return PaymentView(payment, false);
    }

    public async Task<PaymentEntryPreviewDto> PreviewPaymentAsync(Guid businessId, Guid customerId, int actorId, RecordCustomerPaymentDto request)
    {
        var result = await PayCore(businessId, customerId, actorId, request, preview: true);
        await Scope(businessId, actorId, finance: true);
        var already = await db.CustomerPayments.AnyAsync(p => p.BusinessId == businessId && p.Id == result.Id);
        var amount = InventoryMoney.ExactMinor(request.Amount, (await Business(businessId)).CurrencyDecimalPlaces);
        var reference = Optional(request.Reference);
        var referencePattern = reference is null ? null : BusinessSearch.Escape(reference);
        var duplicates = await db.CustomerPayments.AsNoTracking().Where(p => p.BusinessId == businessId && p.CustomerId == customerId && p.Id != result.Id &&
            !db.CustomerPaymentReversals.Any(r => r.BusinessId == businessId && r.PaymentId == p.Id) &&
            ((p.AmountMinor == amount && p.EffectiveDate == request.EffectiveDate && p.Method == request.Method) || (referencePattern != null && p.Reference != null && EF.Functions.Like(p.Reference, referencePattern, "\\"))))
            .OrderByDescending(p => p.RecordedAtUtc).ThenBy(p => p.Id).Take(5).Select(p => new DuplicateDocumentDto(p.Id, p.DocumentNumber, p.EffectiveDate, p.Reference)).ToListAsync();
        return new(already ? result : result with { Id = Guid.Empty, DocumentNumber = null }, already, request.Allocations, duplicates);
    }

    public async Task<CustomerPaymentReversalDto> ReverseAsync(Guid businessId, Guid paymentId, int actorId, ReverseCustomerPaymentDto request)
    {
        var reason = Text(request.Reason, 300, 3);
        var hash = Hash(new { paymentId, request.EffectiveDate, reason });
        await using var tx = await SecurityTransaction.BeginAsync(db);
        await Scope(businessId, actorId, admin: true);
        var existing = await db.CustomerPaymentReversals.AsNoTracking().SingleOrDefaultAsync(r => r.BusinessId == businessId && r.IdempotencyKey == request.IdempotencyKey);
        if (existing is not null)
        {
            if (existing.RequestFingerprint != hash) throw new BusinessRequestException(409, "Idempotency key is already in use");
            return ReversalView(existing);
        }
        var payment = await db.CustomerPayments.AsNoTracking().SingleOrDefaultAsync(p => p.BusinessId == businessId && p.Id == paymentId)
            ?? throw new BusinessRequestException(404, "Payment not found");
        if (payment.CustomerId is null) throw new BusinessRequestException(409, "Walk-in payment correction requires the sale-return workflow");
        // A refunded charge must not lose its receipt afterwards: that would turn
        // the refund into negative net receipts. Keep the original cash evidence.
        var chargeIds = db.CustomerPaymentAllocations.Where(a => a.BusinessId == businessId && a.PaymentId == paymentId).Select(a => a.ChargeId);
        if (await db.CustomerChargeAdjustments.AnyAsync(a => a.BusinessId == businessId && chargeIds.Contains(a.ChargeId) && a.RefundMinor > 0))
            throw new BusinessRequestException(409, "Cannot reverse a receipt linked to a refunded charge");
        if (await IsReversed(businessId, paymentId)) throw new BusinessRequestException(409, "Payment has already been reversed");
        Dates(request.EffectiveDate, null, await Business(businessId));
        if (request.EffectiveDate < payment.EffectiveDate) throw new BusinessRequestException(400, "Reversal predates its payment");
        var reversal = new CustomerPaymentReversal { DocumentNumber = await DocumentNumbers.NextAsync(db, businessId, "REV"), BusinessId = businessId, PaymentId = paymentId, Reason = reason,
            EffectiveDate = request.EffectiveDate, ActorUserId = actorId, RecordedAtUtc = clock.GetUtcNow().UtcDateTime,
            IdempotencyKey = request.IdempotencyKey, RequestFingerprint = hash };
        db.CustomerPaymentReversals.Add(reversal); Audit(businessId, actorId, "CustomerPaymentReversed");
        await db.SaveChangesAsync(); await tx.CommitAsync();
        return ReversalView(reversal);
    }

    public async Task<CustomerStatementDto> StatementAsync(Guid businessId, Guid customerId, int actorId, int page, int size)
    {
        Page(page, size); await Scope(businessId, actorId, finance: true);
        var customer = await FindCustomer(businessId, customerId);
        var business = await Business(businessId);
        var query = Rows(businessId).Where(c => c.CustomerId == customerId);
        var balance = await Balance(customer, business, query);
        var count = await query.CountAsync();
        var rows = await query.OrderBy(c => c.EffectiveDate).ThenBy(c => c.Id).Skip((page - 1) * size).Take(size).ToListAsync();
        return new(balance, Paged(rows.Select(ChargeView).ToList(), page, size, count));
    }

    public async Task<PagedResponseDto<CustomerPaymentDto>> PaymentsAsync(Guid businessId, Guid customerId, int actorId, int page, int size, PaymentFilters? filters = null)
    {
        Page(page, size); await Scope(businessId, actorId, finance: true);
        _ = await FindCustomer(businessId, customerId);
        var query = db.CustomerPayments.AsNoTracking().Where(p => p.BusinessId == businessId && p.CustomerId == customerId);
        filters ??= new();
        if (filters.From > filters.To || filters.Search?.Length > 100) throw new BusinessRequestException(400, "Invalid date range or search length");
        if (filters.From.HasValue) query = query.Where(p => p.EffectiveDate >= filters.From);
        if (filters.To.HasValue) query = query.Where(p => p.EffectiveDate <= filters.To);
        if (filters.StaffUserId.HasValue) query = query.Where(p => p.ActorUserId == filters.StaffUserId);
        if (!string.IsNullOrWhiteSpace(filters.Search)) { var pattern = BusinessSearch.ContainsPattern(filters.Search); query = query.Where(p => (p.DocumentNumber != null && EF.Functions.Like(p.DocumentNumber, pattern, "\\")) || (p.Reference != null && EF.Functions.Like(p.Reference, pattern, "\\"))); }
        var count = await query.CountAsync();
        var rows = await query.OrderByDescending(p => p.RecordedAtUtc).ThenBy(p => p.Id).Skip((page - 1) * size).Take(size)
            .Select(p => new { Payment = p, Reversed = db.CustomerPaymentReversals.Any(r => r.BusinessId == businessId && r.PaymentId == p.Id) }).ToListAsync();
        return Paged(rows.Select(p => PaymentView(p.Payment, p.Reversed)).ToList(), page, size, count);
    }

    public async Task<PagedResponseDto<CustomerBalanceDto>> DebtorsAsync(Guid businessId, int actorId, int page, int size)
    {
        Page(page, size); await Scope(businessId, actorId, admin: true);
        var business = await Business(businessId);
        // Filter in SQL before paging. Archived customers with unsettled debt stay
        // in this report; deactivation must never hide money still owed.
        var debt = Rows(businessId).Where(c => c.CustomerId != null).GroupBy(c => c.CustomerId)
            .Where(g => g.Sum(c => c.Amount - c.Paid) > 0).Select(g => g.Key);
        var query = db.Customers.AsNoTracking().Where(c => c.BusinessId == businessId && debt.Contains(c.Id));
        var count = await query.CountAsync();
        var customers = await query.OrderBy(c => c.NormalizedCode).ThenBy(c => c.Id).Skip((page - 1) * size).Take(size).ToListAsync();
        var balances = new List<CustomerBalanceDto>();
        foreach (var customer in customers) balances.Add(await Balance(customer, business, Rows(businessId).Where(c => c.CustomerId == customer.Id)));
        return Paged(balances, page, size, count);
    }

    internal async Task<Dictionary<Guid, SaleFinancialStatus>> DescribeSalesAsync(Guid businessId, IEnumerable<Guid> saleIds)
    {
        var ids = saleIds.ToArray();
        var rows = await Rows(businessId).Where(c => c.SaleId.HasValue && ids.Contains(c.SaleId.Value)).ToListAsync();
        return rows.ToDictionary(c => c.SaleId!.Value, c => new SaleFinancialStatus(true,
            InventoryMoney.FromMinor(c.Paid, c.Places), InventoryMoney.FromMinor(c.Amount - c.Paid, c.Places)));
    }

    public async Task<CustomerPaymentDetailDto> PaymentDetailAsync(Guid businessId, Guid paymentId, int actorId, bool allowOwnSaleReceipt = false)
    {
        var member = await Scope(businessId, actorId, finance: !allowOwnSaleReceipt);
        var payment = await db.CustomerPayments.AsNoTracking().SingleOrDefaultAsync(p => p.BusinessId == businessId && p.Id == paymentId)
            ?? throw new BusinessRequestException(404, "Payment not found");
        // Ordinary staff may print receipts for money taken on their own sales.
        // This exception grants no access to other receipts or customer statements.
        if (!BusinessAccess.CanRecordPayments(member) && !(allowOwnSaleReceipt && payment.Kind == "AtSale" && payment.ActorUserId == actorId))
            throw new BusinessRequestException(403, "Customer payment permission required");
        if (payment.CustomerId is null && !BusinessAccess.IsAdministrator(member.Role) && payment.ActorUserId != actorId)
            throw new BusinessRequestException(404, "Payment not found");
        var parts = await (from allocation in db.CustomerPaymentAllocations.AsNoTracking()
                           join charge in db.CustomerCharges.AsNoTracking() on new { allocation.BusinessId, Id = allocation.ChargeId }
                               equals new { charge.BusinessId, charge.Id }
                           where allocation.BusinessId == businessId && allocation.PaymentId == paymentId
                           orderby allocation.ChargeId
                           select new { allocation.ChargeId, charge.SaleId, allocation.AmountMinor }).ToListAsync();
        var reversal = await db.CustomerPaymentReversals.AsNoTracking().SingleOrDefaultAsync(r => r.BusinessId == businessId && r.PaymentId == paymentId);
        return new(PaymentView(payment, reversal is not null), parts.Select(p => new CustomerPaymentAllocationDto(p.ChargeId, p.SaleId,
            InventoryMoney.FromMinor(p.AmountMinor, payment.CurrencyDecimalPlaces))).ToList(), reversal is null ? null : ReversalView(reversal));
    }

    private async Task<CustomerBalanceDto> Balance(Customer customer, Business business, IQueryable<ChargeRow> query)
    {
        var today = Today(business);
        var totals = await query.GroupBy(c => c.CustomerId).Select(g => new
        {
            Opening = g.Sum(c => c.Kind == "OpeningDebt" ? c.Amount : 0), Sales = g.Sum(c => c.Kind == "Sale" ? c.Amount : 0),
            Paid = g.Sum(c => c.Paid), Outstanding = g.Sum(c => c.Amount - c.Paid),
            Overdue = g.Sum(c => c.DueDate.HasValue && c.DueDate < today ? c.Amount - c.Paid : 0)
        }).SingleOrDefaultAsync();
        var places = business.CurrencyDecimalPlaces;
        return new(customer.Id, customer.Code, customer.Name, business.Currency, places,
            InventoryMoney.FromMinor(totals?.Opening ?? 0, places), InventoryMoney.FromMinor(totals?.Sales ?? 0, places),
            InventoryMoney.FromMinor(totals?.Paid ?? 0, places), InventoryMoney.FromMinor(totals?.Outstanding ?? 0, places),
            InventoryMoney.FromMinor(totals?.Overdue ?? 0, places));
    }

    internal IQueryable<ChargeRow> Rows(Guid businessId) => db.CustomerCharges.AsNoTracking().Where(c => c.BusinessId == businessId)
        .Select(c => new ChargeRow { Id = c.Id, CustomerId = c.CustomerId, SaleId = c.SaleId, Kind = c.Kind, Amount = c.AmountMinor + (db.CustomerChargeAdjustments.Where(a => a.BusinessId == businessId && a.ChargeId == c.Id).Sum(a => (long?)a.DeltaMinor) ?? 0),
            Currency = c.Currency, Places = c.CurrencyDecimalPlaces, EffectiveDate = c.EffectiveDate, DueDate = c.DueDate, Reason = c.Reason,
            Paid = (db.CustomerPaymentAllocations.Where(a => a.BusinessId == businessId && a.ChargeId == c.Id &&
                !db.CustomerPaymentReversals.Any(r => r.BusinessId == businessId && r.PaymentId == a.PaymentId)).Sum(a => (long?)a.AmountMinor) ?? 0) - (db.CustomerChargeAdjustments.Where(a => a.BusinessId == businessId && a.ChargeId == c.Id).Sum(a => (long?)a.RefundMinor) ?? 0) });
    internal sealed class ChargeRow
    {
        public Guid Id { get; init; } public Guid? CustomerId { get; init; } public Guid? SaleId { get; init; }
        public string Kind { get; init; } = ""; public string Currency { get; init; } = "";
        public string Reason { get; init; } = ""; public long Amount { get; init; } public long Paid { get; init; }
        public int Places { get; init; } public DateOnly EffectiveDate { get; init; } public DateOnly? DueDate { get; init; }
    }
    private Task<Business> Business(Guid id) => db.Businesses.AsNoTracking().SingleAsync(b => b.Id == id);
    private async Task<Customer> FindCustomer(Guid businessId, Guid id) =>
        await db.Customers.AsNoTracking().SingleOrDefaultAsync(c => c.BusinessId == businessId && c.Id == id)
        ?? throw new BusinessRequestException(404, "Customer not found");
    private Task<bool> IsReversed(Guid businessId, Guid id) => db.CustomerPaymentReversals.AnyAsync(r => r.BusinessId == businessId && r.PaymentId == id);
    private DateOnly Today(Business business) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.GetUtcNow(), TimeZoneInfo.FindSystemTimeZoneById(business.TimeZoneId)).DateTime);
    private void Dates(DateOnly effective, DateOnly? due, Business business)
    {
        if (effective == default || effective > Today(business) || due.HasValue && due < effective)
            throw new BusinessRequestException(400, "Effective date must not be future-dated, and due date must not precede it");
    }
    private static void Method(string? method)
    {
        if (method is not ("Cash" or "BankTransfer" or "Card" or "Other")) throw new BusinessRequestException(400, "A valid payment method is required");
    }
    private static string Text(string value, int max, int min = 2)
    {
        var text = value?.Trim() ?? "";
        if (text.Length < min || text.Length > max) throw new BusinessRequestException(400, "Text length is invalid");
        return text;
    }
    private static string? Optional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string Decimal(decimal value) => value.ToString("G29", CultureInfo.InvariantCulture);
    private static string Hash(object value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value))));
    private static void Page(int page, int size)
    {
        if (page is < 1 or > 1000 || size is < 1 or > 100 || (long)(page - 1) * size > 10_000) throw new BusinessRequestException(400, "Invalid pagination parameters");
    }
    private static PagedResponseDto<T> Paged<T>(IReadOnlyList<T> rows, int page, int size, int count) => new(rows, page, size, count, (int)Math.Ceiling(count / (double)size));
    private void Audit(Guid businessId, int actorId, string action) => db.BusinessAuditEvents.Add(new()
        { BusinessId = businessId, ActorUserId = actorId, Action = action, CreatedAtUtc = clock.GetUtcNow().UtcDateTime });
    private static CustomerDto CustomerView(Customer c) => new(c.Id, c.Code, c.Name, c.Email, c.Phone, c.IsActive);
    private static CustomerChargeDto ChargeView(ChargeRow c) => new(c.Id, c.SaleId, c.Kind, c.EffectiveDate, c.DueDate, c.Reason,
        InventoryMoney.FromMinor(c.Amount, c.Places), InventoryMoney.FromMinor(c.Paid, c.Places), InventoryMoney.FromMinor(c.Amount - c.Paid, c.Places));
    private static CustomerPaymentDto PaymentView(CustomerPayment p, bool reversed) => new(p.Id, p.CustomerId, p.Kind, p.Method,
        InventoryMoney.FromMinor(p.AmountMinor, p.CurrencyDecimalPlaces), p.EffectiveDate, p.Reference, p.RecordedAtUtc, p.ActorUserId, reversed, p.DocumentNumber);
    private static CustomerPaymentReversalDto ReversalView(CustomerPaymentReversal r) => new(r.Id, r.PaymentId, r.Reason, r.EffectiveDate, r.RecordedAtUtc, r.ActorUserId, r.DocumentNumber);
}
