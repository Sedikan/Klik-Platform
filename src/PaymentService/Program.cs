using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Shared;

var builder = WebApplication.CreateBuilder(args);
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

builder.Services.AddHttpClient("account", client =>
{
    client.BaseAddress = new Uri(builder.Configuration["Services:AccountBaseUrl"] ?? "http://localhost:7001");
    client.Timeout = TimeSpan.FromSeconds(10);
});

builder.Services.AddHttpClient("cashback", client =>
{
    client.BaseAddress = new Uri(builder.Configuration["Services:CashbackBaseUrl"] ?? "http://localhost:7003");
    client.Timeout = TimeSpan.FromSeconds(10);
});

builder.Services.AddSingleton<PaymentStore>();
builder.Services.AddHostedService<OutboxDispatcherService>();

var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.MapPost("/payments", async (
    HttpContext context,
    CreatePaymentRequest request,
    PaymentStore store,
    IHttpClientFactory factory,
    ILogger<Program> logger) =>
{
    var idempotencyKey = context.Request.Headers["Idempotency-Key"].ToString().Trim();
    if (string.IsNullOrWhiteSpace(idempotencyKey))
        return Results.BadRequest(new { error = "Idempotency-Key header is required." });
    if (idempotencyKey.Length > 200)
        return Results.BadRequest(new { error = "Idempotency-Key must not exceed 200 characters." });
    if (request.AccountId == Guid.Empty)
        return Results.BadRequest(new { error = "accountId is required." });
    if (string.IsNullOrWhiteSpace(request.MerchantId))
        return Results.BadRequest(new { error = "merchantId is required." });
    if (request.Amount <= 0 || request.Amount != decimal.Round(request.Amount, 2))
        return Results.BadRequest(new { error = "amount must be > 0 with at most two decimal places." });
    if (!CurrencyCode.TryNormalize(request.Currency, out var currency))
        return Results.BadRequest(new { error = "currency must be a three-letter ISO code." });

    var reservation = store.Reserve(idempotencyKey, request with
    {
        Currency = currency,
        MerchantId = request.MerchantId.Trim()
    });
    if (reservation.Conflict)
        return Results.Conflict(new { error = "Idempotency-Key has already been used with a different payment." });

    var payment = reservation.Payment;
    if (payment.Status != PaymentStatus.Pending)
        return Results.Ok(payment);

    var gate = store.GetProcessingGate(payment.Id);
    await gate.WaitAsync(context.RequestAborted);
    try
    {
        payment = store.Get(payment.Id)!;
        if (payment.Status != PaymentStatus.Pending)
            return Results.Ok(payment);

        using var accountRequest = new HttpRequestMessage(
            HttpMethod.Post,
            $"/accounts/{payment.AccountId}/charges")
        {
            Content = JsonContent.Create(new { amount = payment.Amount.Amount, currency = payment.Amount.Currency })
        };
        accountRequest.Headers.TryAddWithoutValidation("Idempotency-Key", payment.Id.ToString("N"));

        HttpResponseMessage response;
        try
        {
            response = await factory.CreateClient("account").SendAsync(accountRequest, context.RequestAborted);
        }
        catch (HttpRequestException exception)
        {
            logger.LogWarning(exception, "Account Service is unavailable for payment {PaymentId}.", payment.Id);
            return Results.Accepted($"/payments/{payment.Id}", store.Get(payment.Id));
        }
        catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested)
        {
            logger.LogWarning("Account Service timed out for payment {PaymentId}.", payment.Id);
            return Results.Accepted($"/payments/{payment.Id}", store.Get(payment.Id));
        }

        using (response)
        {
            if (response.IsSuccessStatusCode)
            {
                var captured = payment with { Status = PaymentStatus.Captured };
                store.Capture(captured, new PaymentCapturedIntegrationEvent(
                    captured.Id,
                    captured.AccountId,
                    captured.MerchantId,
                    captured.Amount.Amount,
                    captured.Amount.Currency,
                    DateTimeOffset.UtcNow));
                return Results.Ok(store.Get(captured.Id));
            }

            if ((int)response.StatusCode >= 500 || response.StatusCode == HttpStatusCode.RequestTimeout ||
                response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                logger.LogWarning(
                    "Account Service returned transient status {StatusCode} for payment {PaymentId}.",
                    response.StatusCode,
                    payment.Id);
                return Results.Accepted($"/payments/{payment.Id}", store.Get(payment.Id));
            }

            var reason = response.StatusCode == HttpStatusCode.BadRequest
                ? "Charge rejected by Account Service."
                : response.StatusCode == HttpStatusCode.NotFound
                    ? "Account not found."
                    : "Charge rejected by Account Service.";
            store.Fail(payment.Id, reason);
            logger.LogWarning("Payment {PaymentId} failed. Reason: {Reason}", payment.Id, reason);
            return Results.Ok(store.Get(payment.Id));
        }
    }
    finally
    {
        gate.Release();
    }
});

app.MapGet("/payments/{id:guid}", (Guid id, PaymentStore store) =>
{
    var payment = store.Get(id);
    return payment is null
        ? Results.NotFound(new { error = "Payment not found." })
        : Results.Ok(payment);
});

app.Run();

sealed class PaymentStore
{
    private readonly object _sync = new();
    private readonly Dictionary<Guid, Payment> _payments = [];
    private readonly Dictionary<string, IdempotencyReservation> _idempotencyIndex = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _processingGates = new();
    private readonly ConcurrentQueue<OutboxMessage> _outbox = new();

    public Payment? Get(Guid id)
    {
        lock (_sync)
            return _payments.GetValueOrDefault(id);
    }

    public IdempotencyResult Reserve(string key, CreatePaymentRequest request)
    {
        lock (_sync)
        {
            if (_idempotencyIndex.TryGetValue(key, out var existing))
                return existing.Request == request
                    ? new IdempotencyResult(_payments[existing.PaymentId], false)
                    : new IdempotencyResult(_payments[existing.PaymentId], true);

            var payment = new Payment(
                Guid.NewGuid(),
                request.AccountId,
                request.MerchantId,
                Money.Create(request.Amount, request.Currency),
                PaymentStatus.Pending,
                DateTimeOffset.UtcNow,
                null);

            _payments.Add(payment.Id, payment);
            _idempotencyIndex.Add(key, new IdempotencyReservation(payment.Id, request));
            return new IdempotencyResult(payment, false);
        }
    }

    public SemaphoreSlim GetProcessingGate(Guid paymentId) =>
        _processingGates.GetOrAdd(paymentId, static _ => new SemaphoreSlim(1, 1));

    public void Capture(Payment payment, PaymentCapturedIntegrationEvent integrationEvent)
    {
        lock (_sync)
        {
            var current = _payments[payment.Id];
            if (current.Status != PaymentStatus.Pending)
                return;

            _payments[payment.Id] = payment;
            _outbox.Enqueue(new OutboxMessage(Guid.NewGuid(), integrationEvent));
        }
    }

    public void Fail(Guid paymentId, string reason)
    {
        lock (_sync)
        {
            var current = _payments[paymentId];
            if (current.Status == PaymentStatus.Pending)
                _payments[paymentId] = current with { Status = PaymentStatus.Failed, FailureReason = reason };
        }
    }

    public bool TryDequeueOutbox(out OutboxMessage? message)
    {
        var succeeded = _outbox.TryDequeue(out var item);
        message = item;
        return succeeded;
    }

    public void EnqueueOutbox(OutboxMessage message) => _outbox.Enqueue(message);
}

sealed class OutboxDispatcherService(
    PaymentStore store,
    IHttpClientFactory clientFactory,
    ILogger<OutboxDispatcherService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            if (!store.TryDequeueOutbox(out var message) || message is null)
            {
                await Task.Delay(300, stoppingToken);
                continue;
            }

            try
            {
                var response = await clientFactory.CreateClient("cashback")
                    .PostAsJsonAsync("/events/payment-captured", message.Event, stoppingToken);
                using (response)
                {
                    if (response.IsSuccessStatusCode)
                        continue;

                    logger.LogWarning(
                        "Cashback dispatch failed for PaymentId {PaymentId}. StatusCode: {StatusCode}",
                        message.Event.PaymentId,
                        response.StatusCode);
                }
            }
            catch (HttpRequestException exception)
            {
                logger.LogError(exception, "Outbox dispatch failed for payment {PaymentId}.", message.Event.PaymentId);
            }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning("Outbox dispatch timed out for payment {PaymentId}.", message.Event.PaymentId);
            }

            store.EnqueueOutbox(message);
            await Task.Delay(1000, stoppingToken);
        }
    }
}

sealed record CreatePaymentRequest(Guid AccountId, decimal Amount, string Currency, string MerchantId);
sealed record IdempotencyReservation(Guid PaymentId, CreatePaymentRequest Request);
sealed record IdempotencyResult(Payment Payment, bool Conflict);

sealed record Payment(
    Guid Id,
    Guid AccountId,
    string MerchantId,
    Money Amount,
    PaymentStatus Status,
    DateTimeOffset CreatedAtUtc,
    string? FailureReason);

sealed record OutboxMessage(Guid Id, PaymentCapturedIntegrationEvent Event);
