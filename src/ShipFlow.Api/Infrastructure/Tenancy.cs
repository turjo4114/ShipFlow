using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using ShipFlow.Api.Data;

namespace ShipFlow.Api.Infrastructure;

public interface ITenantContext
{
    int TenantId { get; }
    string? TenantCode { get; }
    bool IsResolved { get; }
    void Set(int tenantId, string tenantCode);
}

/// <summary>Scoped per request. Filled by <see cref="TenantMiddleware"/>.</summary>
public sealed class TenantContext : ITenantContext
{
    public int TenantId { get; private set; }
    public string? TenantCode { get; private set; }
    public bool IsResolved => TenantId > 0;
    public void Set(int tenantId, string tenantCode) { TenantId = tenantId; TenantCode = tenantCode; }
}

/// <summary>
/// Resolves the tenant from the X-Tenant-Code header for every /api call.
/// In production replace the header with a claim from a validated JWT - never trust a raw header from the internet.
/// </summary>
public sealed class TenantMiddleware(RequestDelegate next, IMemoryCache cache)
{
    public const string HeaderName = "X-Tenant-Code";

    public async Task InvokeAsync(HttpContext ctx, ITenantContext tenant, ShipFlowDbContext db)
    {
        if (!ctx.Request.Path.StartsWithSegments("/api")) { await next(ctx); return; }

        var code = ctx.Request.Headers[HeaderName].ToString().Trim();
        if (code.Length == 0)
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            await ctx.Response.WriteAsJsonAsync(new { error = $"Missing {HeaderName} header." });
            return;
        }

        var key = $"tenant:{code.ToUpperInvariant()}";
        if (!cache.TryGetValue(key, out int tenantId))
        {
            tenantId = await db.Tenants.AsNoTracking()
                .Where(t => t.Code == code && t.IsActive)
                .Select(t => t.TenantId)
                .FirstOrDefaultAsync(ctx.RequestAborted);
            if (tenantId > 0) cache.Set(key, tenantId, TimeSpan.FromMinutes(5));
        }

        if (tenantId == 0)
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            await ctx.Response.WriteAsJsonAsync(new { error = "Unknown or inactive tenant." });
            return;
        }

        tenant.Set(tenantId, code);
        await next(ctx);
    }
}
