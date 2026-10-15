using System.Globalization;
using Shared;

var builder = WebApplication.CreateBuilder(args);
var policy = CashbackPolicy.Load(builder.Configuration);
builder.Services.AddSingleton(policy);
builder.Services.AddSingleton<CashbackStore>();

var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.MapPost("/events/payment-captured", (PaymentCapturedIntegrationEvent evt, CashbackStore store) =>
{
    if (evt.PaymentId == Guid.Empty)
        return Results.BadRequest(new { error = "paymentId is required." });
    if (evt.AccountId == Guid.Empty)
        return Results.BadRequest(new { error = "accountId is required." });
    if (evt.Amount <= 0 || evt.Amount != decimal.Round(evt.Amount, 2))
        return Results.BadRequest(new { error = "amount must be > 0 with at most two decimal places." });
    if (string.IsNullOrWhiteSpace(evt.MerchantId))
        return Results.BadRequest(new { error = "merchantId is required." });
    if (!CurrencyCode.TryNormalize(evt.Currency, out var currency))
        return Results.BadRequest(new { error = "currency must be a three-letter ISO code." });
    if (evt.OccurredAtUtc == default)
        return Results.BadRequest(new { error = "occurredAtUtc is required." });

    var result = store.Process(evt with
    {
        MerchantId = evt.MerchantId.Trim(),
        Currency = currency
    });
    if (result.Duplicate)
        return Results.Ok(new { status = "duplicate_ignored", paymentId = evt.PaymentId });

    return Results.Accepted($"/cashback/{evt.AccountId}", new
    {
        evt.PaymentId,
        evt.AccountId,
        requestedCashback = result.RequestedCashback,
        appliedCashback = result.AppliedCashback
    });
});

app.MapGet("/cashback/{accountId:guid}", (Guid accountId, CashbackStore store) =>
{
    var entries = store.GetEntries(accountId);
    var totalByCurrency = entries
        .GroupBy(entry => entry.Currency)
        .ToDictionary(
            group => group.Key,
            group => decimal.Round(group.Sum(entry => entry.CashbackAmount), 2, MidpointRounding.AwayFromZero));

    return Results.Ok(new
    {
        accountId,
        totalCashbackByCurrency = totalByCurrency,
        entries
    });
});

app.Run();

sealed class CashbackPolicy(decimal monthlyLimit, decimal baseRate, decimal campaignRate, DateTimeOffset? campaignStartUtc, DateTimeOffset? campaignEndUtc)
{
    public decimal MonthlyLimit { get; } = monthlyLimit;
    public decimal BaseRate { get; } = baseRate;
    public decimal CampaignRate { get; } = campaignRate;
    public DateTimeOffset? CampaignStartUtc { get; } = campaignStartUtc;
    public DateTimeOffset? CampaignEndUtc { get; } = campaignEndUtc;

    public static CashbackPolicy Load(IConfiguration configuration)
    {
        var monthlyLimit = ReadDecimal(configuration, "Cashback:MonthlyLimit", 100m);
        var baseRate = ReadDecimal(configuration, "Cashback:BaseRate", 0.01m);
        var campaignRate = ReadDecimal(configuration, "Cashback:CampaignRate", 0.03m);
        var startValue = configuration["Cashback:CampaignStartUtc"];
        var endValue = configuration["Cashback:CampaignEndUtc"];

        if (monthlyLimit <= 0 || baseRate < 0 || campaignRate < baseRate || campaignRate > 1)
            throw new InvalidOperationException("Cashback limits and rates are invalid.");
        if (string.IsNullOrWhiteSpace(startValue) != string.IsNullOrWhiteSpace(endValue))
            throw new InvalidOperationException("Configure both Cashback:CampaignStartUtc and Cashback:CampaignEndUtc.");

        if (string.IsNullOrWhiteSpace(startValue))
            return new CashbackPolicy(monthlyLimit, baseRate, campaignRate, null, null);

        if (!DateTimeOffset.TryParse(startValue, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var start) ||
            !DateTimeOffset.TryParse(endValue, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var end) ||
            start > end)
            throw new InvalidOperationException("Cashback campaign dates must be valid UTC timestamps with start <= end.");

        return new CashbackPolicy(monthlyLimit, baseRate, campaignRate, start.ToUniversalTime(), end.ToUniversalTime());
    }

    private static decimal ReadDecimal(IConfiguration configuration, string key, decimal defaultValue)
    {
        var value = configuration[key];
        if (value is null)
            return defaultValue;
        if (!decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed))
            throw new InvalidOperationException($"{key} must be a decimal number.");
        return parsed;
    }
}

sealed class CashbackStore(CashbackPolicy policy)
{
    private readonly object _sync = new();
    private readonly HashSet<Guid> _processedPaymentIds = [];
    private readonly Dictionary<Guid, List<CashbackEntry>> _entriesByAccount = [];

    public CashbackResult Process(PaymentCapturedIntegrationEvent paymentEvent)
    {
        lock (_sync)
        {
            if (_processedPaymentIds.Contains(paymentEvent.PaymentId))
                return new CashbackResult(true, 0m, 0m);

            var rate = policy.CampaignStartUtc is { } start &&
                       policy.CampaignEndUtc is { } end &&
                       paymentEvent.OccurredAtUtc >= start &&
                       paymentEvent.OccurredAtUtc <= end
                ? policy.CampaignRate
                : policy.BaseRate;
            var requested = decimal.Round(paymentEvent.Amount * rate, 2, MidpointRounding.AwayFromZero);

            _entriesByAccount.TryGetValue(paymentEvent.AccountId, out var existingEntries);
            var monthStart = new DateTimeOffset(
                paymentEvent.OccurredAtUtc.Year,
                paymentEvent.OccurredAtUtc.Month,
                1,
                0,
                0,
                0,
                TimeSpan.Zero);
            var alreadyCollected = (existingEntries ?? [])
                .Where(entry =>
                    entry.Currency == paymentEvent.Currency &&
                    entry.CreatedAtUtc >= monthStart &&
                    entry.CreatedAtUtc < monthStart.AddMonths(1))
                .Sum(entry => entry.CashbackAmount);
            var remaining = Math.Max(0m, policy.MonthlyLimit - alreadyCollected);
            var applied = decimal.Round(Math.Min(requested, remaining), 2, MidpointRounding.AwayFromZero);

            if (existingEntries is null)
            {
                existingEntries = [];
                _entriesByAccount.Add(paymentEvent.AccountId, existingEntries);
            }

            existingEntries.Add(new CashbackEntry(
                Guid.NewGuid(),
                paymentEvent.PaymentId,
                paymentEvent.AccountId,
                paymentEvent.MerchantId,
                paymentEvent.Amount,
                applied,
                paymentEvent.Currency,
                paymentEvent.OccurredAtUtc));
            _processedPaymentIds.Add(paymentEvent.PaymentId);

            return new CashbackResult(false, requested, applied);
        }
    }

    public IReadOnlyList<CashbackEntry> GetEntries(Guid accountId)
    {
        lock (_sync)
        {
            return _entriesByAccount.TryGetValue(accountId, out var entries)
                ? entries.OrderByDescending(entry => entry.CreatedAtUtc).ToArray()
                : [];
        }
    }
}

sealed record CashbackResult(bool Duplicate, decimal RequestedCashback, decimal AppliedCashback);

sealed record CashbackEntry(
    Guid Id,
    Guid PaymentId,
    Guid AccountId,
    string MerchantId,
    decimal PaymentAmount,
    decimal CashbackAmount,
    string Currency,
    DateTimeOffset CreatedAtUtc);
