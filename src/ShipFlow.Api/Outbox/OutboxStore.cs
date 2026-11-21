using Dapper;
using ShipFlow.Api.Infrastructure;

namespace ShipFlow.Api.Outbox;

public sealed class OutboxRow
{
    public long OutboxMessageId { get; set; }
    public int TenantId { get; set; }
    public string MessageType { get; set; } = "";
    public string Payload { get; set; } = "";
    public int Attempts { get; set; }
}

public sealed class StatRow { public byte Status { get; set; } public int Cnt { get; set; } }

public interface IOutboxStore
{
    Task<IReadOnlyList<OutboxRow>> ClaimAsync(int batchSize, int lockMinutes, CancellationToken ct);
    Task CompleteAsync(long id, CancellationToken ct);
    Task FailAsync(long id, int attempts, int maxAttempts, string error, CancellationToken ct);
    Task<IReadOnlyDictionary<string, int>> GetStatsAsync(int tenantId, CancellationToken ct);
}

public sealed class OutboxStore(ISqlConnectionFactory factory) : IOutboxStore
{
    /// <summary>
    /// Atomic claim in ONE statement (no user transaction needed):
    /// UPDLOCK+READPAST lets several API instances poll concurrently without blocking or double-claiming;
    /// rows stuck in "processing" past LockedUntil (crashed worker) are re-claimed automatically.
    /// </summary>
    public async Task<IReadOnlyList<OutboxRow>> ClaimAsync(int batchSize, int lockMinutes, CancellationToken ct)
    {
        const string sql = """
            ;WITH next AS (
                SELECT TOP (@Batch) *
                FROM dbo.OutboxMessages WITH (ROWLOCK, UPDLOCK, READPAST)
                WHERE (Status = 0 AND NextAttemptAt <= SYSUTCDATETIME())
                   OR (Status = 1 AND LockedUntil < SYSUTCDATETIME())
                ORDER BY OutboxMessageId
            )
            UPDATE next
               SET Status = 1,
                   Attempts = Attempts + 1,
                   LockedUntil = DATEADD(MINUTE, @LockMinutes, SYSUTCDATETIME())
            OUTPUT inserted.OutboxMessageId, inserted.TenantId, inserted.MessageType, inserted.Payload, inserted.Attempts;
            """;
        await using var conn = await factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<OutboxRow>(new CommandDefinition(sql,
            new { Batch = batchSize, LockMinutes = lockMinutes }, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task CompleteAsync(long id, CancellationToken ct)
    {
        const string sql = "UPDATE dbo.OutboxMessages SET Status = 2, LockedUntil = NULL, LastError = NULL, ProcessedAt = SYSUTCDATETIME() WHERE OutboxMessageId = @Id;";
        await using var conn = await factory.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(sql, new { Id = id }, cancellationToken: ct));
    }

    public async Task FailAsync(long id, int attempts, int maxAttempts, string error, CancellationToken ct)
    {
        // Exponential backoff between outbox attempts: 10s, 20s, 40s ... capped at 15 min. Beyond MaxAttempts -> dead letter.
        var delaySeconds = (int)Math.Min(900, 10 * Math.Pow(2, Math.Max(0, attempts - 1)));
        const string sql = """
            UPDATE dbo.OutboxMessages
               SET Status = CASE WHEN @Dead = 1 THEN 3 ELSE 0 END,
                   NextAttemptAt = DATEADD(SECOND, @Delay, SYSUTCDATETIME()),
                   LockedUntil = NULL,
                   LastError = LEFT(@Error, 1000)
             WHERE OutboxMessageId = @Id;
            """;
        await using var conn = await factory.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(sql,
            new { Id = id, Dead = attempts >= maxAttempts ? 1 : 0, Delay = delaySeconds, Error = error }, cancellationToken: ct));
    }

    public async Task<IReadOnlyDictionary<string, int>> GetStatsAsync(int tenantId, CancellationToken ct)
    {
        const string sql = "SELECT Status, COUNT(*) AS Cnt FROM dbo.OutboxMessages WHERE TenantId = @TenantId GROUP BY Status;";
        await using var conn = await factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<StatRow>(new CommandDefinition(sql, new { TenantId = tenantId }, cancellationToken: ct));
        var names = new[] { "pending", "processing", "done", "dead" };
        var result = names.ToDictionary(n => n, _ => 0);
        foreach (var r in rows) if (r.Status < names.Length) result[names[r.Status]] = r.Cnt;
        return result;
    }
}
