$ErrorActionPreference = "Stop"

$accountService = "http://localhost:7001"
$paymentService = "http://localhost:7002"
$cashbackService = "http://localhost:7003"

foreach ($uri in @("$accountService/health", "$paymentService/health", "$cashbackService/health")) {
    $health = Invoke-RestMethod -Uri $uri
    if ($health.status -ne "ok") {
        throw "Health check failed: $uri"
    }
}

$suffix = [Guid]::NewGuid().ToString("N")
$account = Invoke-RestMethod -Method Post `
    -Uri "$accountService/accounts/link" `
    -ContentType "application/json" `
    -Body (@{
        customerId = "smoke-$suffix"
        bankAccountId = "bank-$suffix"
        initialBalance = 500
        currency = "PLN"
    } | ConvertTo-Json -Compress)

$paymentKey = "payment-$suffix"
$paymentBody = @{
    accountId = $account.id
    amount = 120
    currency = "PLN"
    merchantId = "smoke-shop"
} | ConvertTo-Json -Compress

$payment = Invoke-RestMethod -Method Post `
    -Uri "$paymentService/payments" `
    -Headers @{ "Idempotency-Key" = $paymentKey } `
    -ContentType "application/json" `
    -Body $paymentBody

if ($payment.status -ne "Captured") {
    throw "Expected a captured payment, got '$($payment.status)'."
}

$retry = Invoke-RestMethod -Method Post `
    -Uri "$paymentService/payments" `
    -Headers @{ "Idempotency-Key" = $paymentKey } `
    -ContentType "application/json" `
    -Body $paymentBody
if ($retry.id -ne $payment.id) {
    throw "An idempotent retry returned a different payment."
}

$accountAfterPayment = Invoke-RestMethod -Uri "$accountService/accounts/$($account.id)"
if ($accountAfterPayment.balance -ne 380) {
    throw "Expected account balance 380 after payment, got $($accountAfterPayment.balance)."
}

$cashback = $null
for ($attempt = 0; $attempt -lt 20; $attempt++) {
    Start-Sleep -Milliseconds 250
    $cashback = Invoke-RestMethod -Uri "$cashbackService/cashback/$($account.id)"
    if ($cashback.entries.Count -eq 1) {
        break
    }
}
if ($cashback.entries.Count -ne 1 -or $cashback.entries[0].cashbackAmount -ne 1.2) {
    throw "Expected one cashback entry of 1.20."
}

$chargeKey = "charge-$suffix"
$chargeBody = @{ amount = 10; currency = "PLN" } | ConvertTo-Json -Compress
Invoke-RestMethod -Method Post `
    -Uri "$accountService/accounts/$($account.id)/charges" `
    -Headers @{ "Idempotency-Key" = $chargeKey } `
    -ContentType "application/json" `
    -Body $chargeBody | Out-Null
Invoke-RestMethod -Method Post `
    -Uri "$accountService/accounts/$($account.id)/charges" `
    -Headers @{ "Idempotency-Key" = $chargeKey } `
    -ContentType "application/json" `
    -Body $chargeBody | Out-Null

$accountAfterRetry = Invoke-RestMethod -Uri "$accountService/accounts/$($account.id)"
if ($accountAfterRetry.balance -ne 370) {
    throw "Expected duplicate charge to leave balance at 370, got $($accountAfterRetry.balance)."
}

$conflictBody = @{
    accountId = $account.id
    amount = 121
    currency = "PLN"
    merchantId = "smoke-shop"
} | ConvertTo-Json -Compress
$conflictStatus = $null
try {
    Invoke-RestMethod -Method Post `
        -Uri "$paymentService/payments" `
        -Headers @{ "Idempotency-Key" = $paymentKey } `
        -ContentType "application/json" `
        -Body $conflictBody | Out-Null
}
catch {
    if ($_.Exception.Response) {
        $conflictStatus = [int]$_.Exception.Response.StatusCode
    }
}
if ($conflictStatus -ne 409) {
    throw "Expected idempotency-key reuse with a different payment to return HTTP 409."
}

$limitAccount = Invoke-RestMethod -Method Post `
    -Uri "$accountService/accounts/link" `
    -ContentType "application/json" `
    -Body (@{
        customerId = "limit-customer-$suffix"
        bankAccountId = "limit-bank-$suffix"
        initialBalance = 20000
        currency = "PLN"
    } | ConvertTo-Json -Compress)

foreach ($amount in @(1200, 12000)) {
    $limitPaymentBody = @{
        accountId = $limitAccount.id
        amount = $amount
        currency = "PLN"
        merchantId = "limit-shop"
    } | ConvertTo-Json -Compress
    Invoke-RestMethod -Method Post `
        -Uri "$paymentService/payments" `
        -Headers @{ "Idempotency-Key" = "limit-$amount-$suffix" } `
        -ContentType "application/json" `
        -Body $limitPaymentBody | Out-Null
}

$limitedCashback = $null
for ($attempt = 0; $attempt -lt 20; $attempt++) {
    Start-Sleep -Milliseconds 250
    $limitedCashback = Invoke-RestMethod -Uri "$cashbackService/cashback/$($limitAccount.id)"
    if ($limitedCashback.entries.Count -eq 2) {
        break
    }
}
$monthlyTotal = $limitedCashback.totalCashbackByCurrency.PSObject.Properties["PLN"].Value
if ($limitedCashback.entries.Count -ne 2 -or $monthlyTotal -ne 100) {
    throw "Expected the monthly cashback cap to stop at 100 PLN."
}

Write-Host "PASS: payment, retry safety, balance, cashback, monthly cap, and idempotency conflict."
