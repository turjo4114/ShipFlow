using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ShipFlow.Api.Contracts;
using ShipFlow.Api.Domain;
using ShipFlow.Api.Infrastructure;
using ShipFlow.Api.Outbox;
using ShipFlow.Api.Repositories;

namespace ShipFlow.Api.Endpoints;

public static class ApiEndpoints
{
    private static bool IsDuplicateKey(DbUpdateException ex) =>
        ex.InnerException is SqlException { Number: 2601 or 2627 };

    public static void MapShipFlowApi(this WebApplication app)
    {
        var api = app.MapGroup("/api");

        // ---------- Products ----------
        api.MapGet("/products", async (IProductRepository repo, int? skip, int? take, CancellationToken ct) =>
            Results.Ok(await repo.ListAsync(Math.Max(0, skip ?? 0), Math.Clamp(take ?? 50, 1, 200), ct)));

        api.MapPost("/products", async (CreateProductRequest r, IProductRepository repo, IUnitOfWork uow, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(r.Sku) || string.IsNullOrWhiteSpace(r.Name))
                return Results.BadRequest(new { error = "Sku and Name are required." });

            var p = new Product { Sku = r.Sku.Trim(), Name = r.Name.Trim(), UnitOfMeasure = string.IsNullOrWhiteSpace(r.UnitOfMeasure) ? "EA" : r.UnitOfMeasure.Trim() };
            repo.Add(p);
            try { await uow.SaveChangesAsync(ct); }
            catch (DbUpdateException ex) when (IsDuplicateKey(ex)) { return Results.Conflict(new { error = $"SKU '{p.Sku}' already exists." }); }
            return Results.Created($"/api/products/{p.ProductId}", new { p.ProductId, p.Sku, p.Name });
        });

        // ---------- Warehouses ----------
        api.MapGet("/warehouses", async (IWarehouseRepository repo, CancellationToken ct) =>
            Results.Ok(await repo.ListAsync(ct)));

        api.MapPost("/warehouses", async (CreateWarehouseRequest r, IWarehouseRepository repo, IUnitOfWork uow, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(r.Code) || string.IsNullOrWhiteSpace(r.Name))
                return Results.BadRequest(new { error = "Code and Name are required." });

            var w = new Warehouse { Code = r.Code.Trim(), Name = r.Name.Trim() };
            repo.Add(w);
            try { await uow.SaveChangesAsync(ct); }
            catch (DbUpdateException ex) when (IsDuplicateKey(ex)) { return Results.Conflict(new { error = $"Warehouse '{w.Code}' already exists." }); }
            return Results.Created($"/api/warehouses/{w.WarehouseId}", new { w.WarehouseId, w.Code, w.Name });
        });

        // ---------- Stock movements (ledger writes) ----------
        api.MapGet("/stock-movements", async (int productId, int? warehouseId, int? take, IStockMovementRepository repo, CancellationToken ct) =>
            Results.Ok(await repo.ListAsync(productId, warehouseId, Math.Clamp(take ?? 100, 1, 500), ct)));

        api.MapPost("/stock-movements", async (
            CreateMovementRequest r, IProductRepository products, IWarehouseRepository warehouses,
            IStockMovementRepository movements, IOutboxWriter outbox, IUnitOfWork uow, CancellationToken ct) =>
        {
            if (!StockMovementTypes.TryGetDelta(r.MovementType, r.Quantity, out var type, out var delta, out var error))
                return Results.BadRequest(new { error });

            // Query filters make these lookups tenant-scoped: another tenant's ids simply "don't exist".
            if (await products.GetByIdAsync(r.ProductId, ct) is null) return Results.NotFound(new { error = "Product not found." });
            if (await warehouses.GetByIdAsync(r.WarehouseId, ct) is null) return Results.NotFound(new { error = "Warehouse not found." });

            var m = new StockMovement
            {
                WarehouseId = r.WarehouseId, ProductId = r.ProductId, MovementType = type, QuantityDelta = delta,
                ReferenceNo = r.ReferenceNo, Notes = r.Notes, OccurredAt = (r.OccurredAt ?? DateTime.UtcNow).ToUniversalTime()
            };
            movements.Add(m);

            // Same SaveChanges => same DB transaction: the ledger row and its outbox event commit together or not at all.
            outbox.Enqueue("StockMovementRecorded", new { r.WarehouseId, r.ProductId, MovementType = type, QuantityDelta = delta, r.ReferenceNo, m.OccurredAt });

            await uow.SaveChangesAsync(ct);
            return Results.Created($"/api/stock-movements/{m.MovementId}", new { m.MovementId, m.QuantityDelta });
        });

        // ---------- Reports (Dapper + window functions) ----------
        api.MapGet("/stock/sheet", async (int warehouseId, int productId, DateTime? from, DateTime? to,
            ITenantContext tenant, IStockReportReader reader, CancellationToken ct) =>
            Results.Ok(await reader.GetSheetAsync(tenant.TenantId, warehouseId, productId, from?.ToUniversalTime(), to?.ToUniversalTime(), ct)));

        api.MapGet("/stock/on-hand", async (DateTime? asOf, ITenantContext tenant, IStockReportReader reader, CancellationToken ct) =>
            Results.Ok(await reader.GetOnHandAsync(tenant.TenantId, asOf?.ToUniversalTime(), ct)));

        // ---------- Bulk logistics update (queued, never inline) ----------
        api.MapPost("/logistics/bulk-update", async (BulkLogisticsRequest r, IStockMovementRepository movements,
            IOutboxWriter outbox, IUnitOfWork uow, CancellationToken ct) =>
        {
            var ids = (r.MovementIds ?? Array.Empty<long>()).Distinct().ToArray();
            if (ids.Length is 0 or > 1000) return Results.BadRequest(new { error = "Provide between 1 and 1000 movement ids." });
            if (await movements.CountByIdsAsync(ids, ct) != ids.Length) return Results.BadRequest(new { error = "One or more movement ids do not exist for this tenant." });

            outbox.Enqueue("BulkLogisticsUpdate", new { MovementIds = ids });
            await uow.SaveChangesAsync(ct);
            return Results.Accepted(value: new { queued = true, count = ids.Length });
        });

        api.MapGet("/ops/outbox", async (ITenantContext tenant, IOutboxStore store, CancellationToken ct) =>
            Results.Ok(await store.GetStatsAsync(tenant.TenantId, ct)));

        // ---------- Mock carrier (dev only) - lets you watch retries and the circuit breaker work ----------
        var carrier = app.Services.GetRequiredService<IOptions<CarrierOptions>>().Value;
        if (carrier.EnableMock)
        {
            app.MapPost("/_mock/carrier/updates", (IOptions<CarrierOptions> o) =>
                Random.Shared.NextDouble() < o.Value.MockFailureRate
                    ? Results.StatusCode(StatusCodes.Status503ServiceUnavailable)
                    : Results.Ok(new { accepted = true }))
               .ExcludeFromDescription();
        }
    }
}
