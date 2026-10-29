namespace TradeNest.Api.Services;

// Preserve six-decimal unit prices but store rounded line/header totals in the
// business's declared minor unit. Line rounding is explicit and deterministic;
// it is not inferred from an incomplete hard-coded currency-name registry.
public static class InventoryMoney
{
    public static long PriceMicros(decimal price)
    {
        try { return StockQuantity.ToMicros(price); }
        catch (BusinessRequestException)
        { throw new BusinessRequestException(400, "Unit price must be nonnegative, bounded, and have at most six decimal places"); }
    }
    public static long MinorScale(int places) => places switch
    { 0 => 1, 1 => 10, 2 => 100, 3 => 1000, 4 => 10000,
        _ => throw new BusinessRequestException(400, "Currency precision must be between zero and four") };

    public static long LineTotal(long quantityMicros, long priceMicros, int places)
    {
        var amount = StockQuantity.FromMicros(quantityMicros) * StockQuantity.FromMicros(priceMicros);
        if (amount > StockQuantity.FromMicros(StockQuantity.MaxBalanceMicros))
            throw new BusinessRequestException(400, "Transaction amount exceeds supported limits");
        return checked((long)(decimal.Round(amount, places, MidpointRounding.AwayFromZero) * MinorScale(places)));
    }

    public static decimal FromMinor(long minor, int places) => minor / (decimal)MinorScale(places);

    // Receipts and opening debts are already money amounts, not price products:
    // reject extra precision rather than silently rounding what someone paid.
    public static long ExactMinor(decimal amount, int places, bool allowZero = false)
    {
        var scale = MinorScale(places);
        if (amount < 0 || !allowZero && amount == 0 || amount > StockQuantity.FromMicros(StockQuantity.MaxBalanceMicros) ||
            decimal.Truncate(amount * scale) != amount * scale)
            throw new BusinessRequestException(400, "Money amount must be bounded and match business currency precision");
        return checked((long)(amount * scale));
    }
}
