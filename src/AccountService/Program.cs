using Shared;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<AccountRepository>();

var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.MapPost("/accounts/link", (LinkAccountRequest request, AccountRepository repository) =>
{
    if (string.IsNullOrWhiteSpace(request.CustomerId))
        return Results.BadRequest(new { error = "customerId is required." });
    if (string.IsNullOrWhiteSpace(request.BankAccountId))
        return Results.BadRequest(new { error = "bankAccountId is required." });
    if (request.InitialBalance < 0 || request.InitialBalance != decimal.Round(request.InitialBalance, 2))
        return Results.BadRequest(new { error = "initialBalance must be non-negative with at most two decimal places." });
    if (!CurrencyCode.TryNormalize(request.Currency, out var currency))
        return Results.BadRequest(new { error = "currency must be a three-letter ISO code." });

    var account = new LinkedAccount(
        Guid.NewGuid(),
        request.CustomerId.Trim(),
        request.BankAccountId.Trim(),
        decimal.Round(request.InitialBalance, 2, MidpointRounding.AwayFromZero),
        currency,
        DateTimeOffset.UtcNow);

    repository.Add(account);
    return Results.Created($"/accounts/{account.Id}", account);
});

app.MapGet("/accounts/{id:guid}", (Guid id, AccountRepository repository) =>
{
    var account = repository.Get(id);
    return account is null
        ? Results.NotFound(new { error = "Account not found." })
        : Results.Ok(account);
});

app.MapPost("/accounts/{id:guid}/topups", (Guid id, TopUpRequest request, AccountRepository repository) =>
{
    if (request.Amount <= 0 || request.Amount != decimal.Round(request.Amount, 2))
        return Results.BadRequest(new { error = "amount must be > 0 with at most two decimal places." });

    var result = repository.TopUp(id, request.Amount, request.Currency);
    return result.Error switch
    {
        "Account not found." => Results.NotFound(new { error = result.Error }),
        "Currency does not match the account." => Results.Conflict(new { error = result.Error }),
        _ when result.Account is not null => Results.Ok(result.Account),
        _ => Results.Problem("Unable to top up account.")
    };
});

app.MapPost("/accounts/{id:guid}/charges", (Guid id, ChargeRequest request, HttpContext context, AccountRepository repository) =>
{
    var idempotencyKey = context.Request.Headers["Idempotency-Key"].ToString().Trim();
    if (string.IsNullOrWhiteSpace(idempotencyKey))
        return Results.BadRequest(new { error = "Idempotency-Key header is required." });
    if (idempotencyKey.Length > 200)
        return Results.BadRequest(new { error = "Idempotency-Key must not exceed 200 characters." });
    if (request.Amount <= 0 || request.Amount != decimal.Round(request.Amount, 2))
        return Results.BadRequest(new { error = "amount must be > 0 with at most two decimal places." });
    if (!CurrencyCode.TryNormalize(request.Currency, out var currency))
        return Results.BadRequest(new { error = "currency must be a three-letter ISO code." });

    var result = repository.Charge(id, idempotencyKey, request.Amount, currency);
    return result.Error switch
    {
        "Account not found." => Results.NotFound(new { error = result.Error }),
        "Insufficient funds." => Results.BadRequest(new { error = result.Error }),
        "Currency does not match the account." => Results.Conflict(new { error = result.Error }),
        "Idempotency-Key has already been used with a different charge." => Results.Conflict(new { error = result.Error }),
        _ when result.Account is not null => Results.Ok(new { account = result.Account, duplicate = result.IsDuplicate }),
        _ => Results.Problem("Unable to charge account.")
    };
});

app.Run();

sealed class AccountRepository
{
    private readonly object _sync = new();
    private readonly Dictionary<Guid, LinkedAccount> _accounts = [];
    private readonly Dictionary<string, ChargeRecord> _charges = new(StringComparer.Ordinal);

    public LinkedAccount? Get(Guid id)
    {
        lock (_sync)
            return _accounts.GetValueOrDefault(id);
    }

    public void Add(LinkedAccount account)
    {
        lock (_sync)
            _accounts.Add(account.Id, account);
    }

    public AccountOperationResult TopUp(Guid id, decimal amount, string requestedCurrency)
    {
        lock (_sync)
        {
            if (!_accounts.TryGetValue(id, out var account))
                return new AccountOperationResult(null, "Account not found.");
            if (!CurrencyCode.TryNormalize(requestedCurrency, out var currency) || currency != account.Currency)
                return new AccountOperationResult(null, "Currency does not match the account.");

            var updated = account with { Balance = account.Balance + amount };
            _accounts[id] = updated;
            return new AccountOperationResult(updated, null);
        }
    }

    public ChargeResult Charge(Guid id, string idempotencyKey, decimal amount, string currency)
    {
        lock (_sync)
        {
            if (_charges.TryGetValue(idempotencyKey, out var previous))
            {
                if (previous.AccountId != id || previous.Amount != amount || previous.Currency != currency)
                    return new ChargeResult(null, "Idempotency-Key has already been used with a different charge.", false);
                return new ChargeResult(previous.AccountAfterCharge, null, true);
            }

            if (!_accounts.TryGetValue(id, out var account))
                return new ChargeResult(null, "Account not found.", false);
            if (account.Currency != currency)
                return new ChargeResult(null, "Currency does not match the account.", false);
            if (account.Balance < amount)
                return new ChargeResult(null, "Insufficient funds.", false);

            var updated = account with { Balance = account.Balance - amount };
            _accounts[id] = updated;
            _charges.Add(idempotencyKey, new ChargeRecord(id, amount, currency, updated));
            return new ChargeResult(updated, null, false);
        }
    }
}

sealed record LinkAccountRequest(string CustomerId, string BankAccountId, decimal InitialBalance, string Currency = "PLN");
sealed record TopUpRequest(decimal Amount, string Currency = "PLN");
sealed record ChargeRequest(decimal Amount, string Currency);

sealed record LinkedAccount(
    Guid Id,
    string CustomerId,
    string BankAccountId,
    decimal Balance,
    string Currency,
    DateTimeOffset LinkedAtUtc);

sealed record AccountOperationResult(LinkedAccount? Account, string? Error);
sealed record ChargeResult(LinkedAccount? Account, string? Error, bool IsDuplicate);
sealed record ChargeRecord(Guid AccountId, decimal Amount, string Currency, LinkedAccount AccountAfterCharge);
