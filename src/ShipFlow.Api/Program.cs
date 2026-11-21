using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.OpenApi.Models;
using Polly;
using ShipFlow.Api.Data;
using ShipFlow.Api.Endpoints;
using ShipFlow.Api.Infrastructure;
using ShipFlow.Api.Outbox;
using ShipFlow.Api.Repositories;
using ShipFlow.Api.Resilience;

var builder = WebApplication.CreateBuilder(args);
var cs = builder.Configuration.GetConnectionString("ShipFlow")
         ?? throw new InvalidOperationException("ConnectionStrings:ShipFlow is missing.");

// ---------- Options ----------
builder.Services.Configure<CarrierOptions>(builder.Configuration.GetSection("Carrier"));
builder.Services.Configure<OutboxOptions>(builder.Configuration.GetSection("Outbox"));

// ---------- Tenancy ----------
builder.Services.AddMemoryCache();
builder.Services.AddScoped<ITenantContext, TenantContext>();

// ---------- EF Core (write side) ----------
// Pooling lives in the connection string (Min/Max Pool Size). EnableRetryOnFailure retries transient faults,
// including deadlock victims (error 1205) and Azure/network blips.
builder.Services.AddDbContext<ShipFlowDbContext>(o => o.UseSqlServer(cs, sql =>
{
    sql.EnableRetryOnFailure(maxRetryCount: 5, maxRetryDelay: TimeSpan.FromSeconds(10), errorNumbersToAdd: null);
    sql.CommandTimeout(30);
}));

// ---------- Repositories / read side ----------
builder.Services.AddSingleton<ISqlConnectionFactory, SqlConnectionFactory>();
builder.Services.AddScoped<IProductRepository, ProductRepository>();
builder.Services.AddScoped<IWarehouseRepository, WarehouseRepository>();
builder.Services.AddScoped<IStockMovementRepository, StockMovementRepository>();
builder.Services.AddScoped<IOutboxWriter, OutboxWriter>();
builder.Services.AddScoped<IUnitOfWork, EfUnitOfWork>();
builder.Services.AddScoped<IStockReportReader, StockReportReader>();

// ---------- Fault-tolerant carrier gateway (Polly v8 pipeline) ----------
// Order matters: total timeout -> retry (exp. backoff + jitter) -> circuit breaker -> per-attempt timeout.
builder.Services.AddHttpClient<ICarrierGateway, CarrierGateway>((sp, client) =>
{
    var o = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<CarrierOptions>>().Value;
    client.BaseAddress = new Uri(o.BaseUrl);
})
.AddResilienceHandler("carrier-pipeline", pipeline =>
{
    pipeline.AddTimeout(new HttpTimeoutStrategyOptions { Timeout = TimeSpan.FromSeconds(30) });

    pipeline.AddRetry(new HttpRetryStrategyOptions
    {
        MaxRetryAttempts = 4,
        Delay = TimeSpan.FromMilliseconds(500),
        BackoffType = DelayBackoffType.Exponential,   // 0.5s, 1s, 2s, 4s (+ jitter)
        UseJitter = true                              // avoids synchronized retry storms
    });

    pipeline.AddCircuitBreaker(new HttpCircuitBreakerStrategyOptions
    {
        SamplingDuration = TimeSpan.FromSeconds(30),
        FailureRatio = 0.5,                           // open when >=50% of calls fail...
        MinimumThroughput = 8,                        // ...given at least 8 calls in the window
        BreakDuration = TimeSpan.FromSeconds(30)      // stay open 30s, then half-open probe
    });

    pipeline.AddTimeout(new HttpTimeoutStrategyOptions { Timeout = TimeSpan.FromSeconds(5) });
});

// ---------- Persistent background processing ----------
builder.Services.AddSingleton<IOutboxStore, OutboxStore>();
builder.Services.AddHostedService<OutboxProcessor>();

// ---------- Swagger (with a tenant header box) ----------
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.AddSecurityDefinition("Tenant", new OpenApiSecurityScheme
    {
        Name = TenantMiddleware.HeaderName, In = ParameterLocation.Header, Type = SecuritySchemeType.ApiKey,
        Description = "Tenant code, e.g. ACME"
    });
    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        { new OpenApiSecurityScheme { Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Tenant" } }, Array.Empty<string>() }
    });
});

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseMiddleware<TenantMiddleware>();

// ---------- Health ----------
app.MapGet("/health/live", () => Results.Ok(new { status = "alive" }));

app.MapGet("/health/ready", async (ISqlConnectionFactory factory, CancellationToken ct) =>
{
    try
    {
        await using var conn = await factory.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT DB_NAME() + N' on ' + CAST(SERVERPROPERTY('ProductVersion') AS NVARCHAR(50))";
        return Results.Ok(new { status = "ready", database = (string?)await cmd.ExecuteScalarAsync(ct) });
    }
    catch (Exception ex)
    {
        return Results.Json(new { status = "not-ready", error = ex.Message }, statusCode: 503);
    }
});

app.MapShipFlowApi();
app.Run();
