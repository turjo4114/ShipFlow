using Microsoft.Data.SqlClient;

namespace ShipFlow.Api.Infrastructure;

/// <summary>Opens pooled connections for the Dapper-based read side and the outbox. EF Core has its own pool usage.</summary>
public interface ISqlConnectionFactory
{
    Task<SqlConnection> OpenAsync(CancellationToken ct);
}

public sealed class SqlConnectionFactory(IConfiguration cfg) : ISqlConnectionFactory
{
    private readonly string _cs = cfg.GetConnectionString("ShipFlow")
        ?? throw new InvalidOperationException("ConnectionStrings:ShipFlow is missing.");

    public async Task<SqlConnection> OpenAsync(CancellationToken ct)
    {
        var conn = new SqlConnection(_cs);
        await conn.OpenAsync(ct);
        return conn;
    }
}
