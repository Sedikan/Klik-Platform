namespace Shared;

public sealed record Money(decimal Amount, string Currency)
{
    public static Money Create(decimal amount, string currency)
    {
        if (amount <= 0) throw new ArgumentOutOfRangeException(nameof(amount), "Amount must be greater than 0.");
        if (amount != decimal.Round(amount, 2, MidpointRounding.AwayFromZero))
            throw new ArgumentException("Amount cannot have more than two decimal places.", nameof(amount));
        if (!CurrencyCode.TryNormalize(currency, out var normalizedCurrency))
            throw new ArgumentException("Currency must be a three-letter ISO code.", nameof(currency));
        return new Money(amount, normalizedCurrency);
    }
}

public static class CurrencyCode
{
    public static bool TryNormalize(string? value, out string normalized)
    {
        normalized = value?.Trim().ToUpperInvariant() ?? string.Empty;
        return normalized.Length == 3 && normalized.All(c => c is >= 'A' and <= 'Z');
    }
}

public enum PaymentStatus
{
    Pending = 0,
    Authorized = 1,
    Captured = 2,
    Failed = 3
}

public sealed record PaymentCapturedIntegrationEvent(
    Guid PaymentId,
    Guid AccountId,
    string MerchantId,
    decimal Amount,
    string Currency,
    DateTimeOffset OccurredAtUtc);
