$ErrorActionPreference = "Stop"
$services = @(
    @{ Name = "Account Service"; Project = "src\AccountService\AccountService.csproj" },
    @{ Name = "Cashback Service"; Project = "src\CashbackService\CashbackService.csproj" },
    @{ Name = "Payment Service"; Project = "src\PaymentService\PaymentService.csproj" }
)

$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnet) {
    throw ".NET 8 SDK is required. Install it from https://dotnet.microsoft.com/download/dotnet/8.0 and run this script again."
}

$sdks = & $dotnet.Source --list-sdks
if ($LASTEXITCODE -ne 0 -or -not ($sdks | Where-Object { $_ -match '^8\.' })) {
    throw ".NET 8 SDK is required. Install it from https://dotnet.microsoft.com/download/dotnet/8.0 and run this script again."
}

foreach ($service in $services) {
    Start-Process -FilePath $dotnet.Source `
        -ArgumentList @("run", "--project", $service.Project) `
        -WorkingDirectory $PSScriptRoot `
        -WindowStyle Normal
    Write-Host "Started $($service.Name)."
}

Write-Host "Account:  http://localhost:7001"
Write-Host "Payment:  http://localhost:7002"
Write-Host "Cashback: http://localhost:7003"
