# Klik Platform (portfolio MVP)

A runnable .NET 8 demo of an account-to-payment-to-cashback flow. It uses three ASP.NET Core Minimal API services and does not connect to a real bank or move real money.

## Services

| Service | URL | Responsibility |
| --- | --- | --- |
| Account | `http://localhost:7001` | Link a simulated bank account, top up, and charge its balance |
| Payment | `http://localhost:7002` | Create payments with an `Idempotency-Key` and dispatch captured-payment events |
| Cashback | `http://localhost:7003` | Deduplicate payment events and calculate cashback |

Each service exposes `GET /health`.

## Run on Windows

Install the **.NET 8 SDK**. From this directory, run:

```powershell
.\start.ps1
```

The script opens one terminal for each service. To stop a service, close its terminal. Alternatively, run each command in a separate terminal:

```powershell
dotnet run --project src\AccountService\AccountService.csproj
dotnet run --project src\CashbackService\CashbackService.csproj
dotnet run --project src\PaymentService\PaymentService.csproj
```

To use Docker instead:

```powershell
docker compose up --build
```

With all services running, verify the complete flow and retry behavior:

```powershell
.\scripts\smoke-test.ps1
```

## Try the complete flow

Run these commands in PowerShell after all three services have started. The first response contains the account ID; replace the placeholder in the next command with that ID.

```powershell
$account = Invoke-RestMethod -Method Post `
  -Uri http://localhost:7001/accounts/link `
  -ContentType 'application/json' `
  -Body '{"customerId":"cust-1","bankAccountId":"bank-123","initialBalance":500,"currency":"PLN"}'

$account
```

Create a payment, using a new idempotency key for each distinct payment:

```powershell
$paymentBody = @{
  accountId = $account.id
  amount = 120
  currency = 'PLN'
  merchantId = 'shop-1'
} | ConvertTo-Json

$payment = Invoke-RestMethod -Method Post `
  -Uri http://localhost:7002/payments `
  -Headers @{ 'Idempotency-Key' = 'pay-001' } `
  -ContentType 'application/json' `
  -Body $paymentBody

$payment
```

Check the updated account balance and cashback:

```powershell
Invoke-RestMethod "http://localhost:7001/accounts/$($account.id)"
Invoke-RestMethod "http://localhost:7003/cashback/$($account.id)"
```

Repeat the payment request with the same key and body: it returns the original payment and does not charge the account again. Reusing the key with a different body returns `409 Conflict`. If Account Service is temporarily unavailable, the payment stays `Pending`; retrying the same request is safe.

## Design and current limitations

- Account charges are atomic and idempotent by key. Payment retries always use the same internal charge key.
- Captured-payment events are dispatched asynchronously; Cashback atomically deduplicates events and applies the monthly cap per account and currency.
- Cashback defaults to 1%, with a monthly limit of 100 currency units. An optional campaign rate (3%) is activated by setting both `Cashback__CampaignStartUtc` and `Cashback__CampaignEndUtc` on Cashback Service to ISO-8601 timestamps.
- Amounts must have at most two decimal places. Account balances and cashback are tracked by currency.
- **This is an in-memory demo**: restarting a service clears its data, and the Payment outbox is not durable. Do not use it for real transactions. PostgreSQL-backed storage and a transactional outbox/message broker are required for production.
