namespace TradeNest.Api.Services;

// Public APIs use decimal quantities; persistence uses exact integer millionths.
// Reject excess precision instead of rounding away goods during unit conversion.
public static class StockQuantity
{
    public const long Scale = 1_000_000;
    public const long MaxBalanceMicros = 9_000_000_000_000_000;
    public static decimal FromMicros(long value) => value / (decimal)Scale;

    public static long ToMicros(decimal value, bool allowZero = true)
    {
        if (value < 0 || !allowZero && value == 0 || value > FromMicros(MaxBalanceMicros) ||
            decimal.Truncate(value * Scale) != value * Scale)
            throw new BusinessRequestException(400, "Quantity must be nonnegative, bounded, and have at most six decimal places");
        return checked((long)(value * Scale));
    }

    public static long ConvertToBase(long quantity, long factor)
    {
        // Decimal multiplication avoids overflowing an intermediate long product.
        var result = quantity / (decimal)Scale * factor;
        if (result > MaxBalanceMicros || decimal.Truncate(result) != result)
            throw new BusinessRequestException(400, "Converted stock exceeds limits or requires more than six decimal places");
        return (long)result;
    }
}
