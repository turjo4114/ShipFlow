namespace ShipFlow.Api.Domain;

public interface ITenantOwned { int TenantId { get; set; } }

public class Tenant
{
    public int TenantId { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class Warehouse : ITenantOwned
{
    public int WarehouseId { get; set; }
    public int TenantId { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class Product : ITenantOwned
{
    public int ProductId { get; set; }
    public int TenantId { get; set; }
    public string Sku { get; set; } = "";
    public string Name { get; set; } = "";
    public string UnitOfMeasure { get; set; } = "EA";
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public byte[] RowVersion { get; set; } = Array.Empty<byte>();
}

public class StockMovement : ITenantOwned
{
    public long MovementId { get; set; }
    public int TenantId { get; set; }
    public int WarehouseId { get; set; }
    public int ProductId { get; set; }
    public string MovementType { get; set; } = "";
    public decimal QuantityDelta { get; set; }
    public string? ReferenceNo { get; set; }
    public string? Notes { get; set; }
    public DateTime OccurredAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string? LegacyId { get; set; }
}

public class OutboxMessage : ITenantOwned
{
    public long OutboxMessageId { get; set; }
    public int TenantId { get; set; }
    public string MessageType { get; set; } = "";
    public string Payload { get; set; } = "";
    public byte Status { get; set; }               // 0 pending | 1 processing | 2 done | 3 dead
    public int Attempts { get; set; }
    public DateTime NextAttemptAt { get; set; } = DateTime.UtcNow;
    public DateTime? LockedUntil { get; set; }
    public string? LastError { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ProcessedAt { get; set; }
}

public static class StockMovementTypes
{
    public const string Receipt = "RECEIPT", Issue = "ISSUE", Adjustment = "ADJUSTMENT",
                        TransferIn = "TRANSFER_IN", TransferOut = "TRANSFER_OUT";

    /// <summary>Turns a user-facing positive quantity into the signed ledger delta the DB CHECK expects.</summary>
    public static bool TryGetDelta(string? type, decimal qty, out string normalizedType, out decimal delta, out string? error)
    {
        normalizedType = (type ?? "").Trim().ToUpperInvariant();
        delta = 0; error = null;

        if (qty == 0) { error = "Quantity must be non-zero."; return false; }

        switch (normalizedType)
        {
            case Receipt:
            case TransferIn:
                if (qty < 0) { error = "Quantity must be positive for this movement type."; return false; }
                delta = qty; return true;
            case Issue:
            case TransferOut:
                if (qty < 0) { error = "Quantity must be positive for this movement type."; return false; }
                delta = -qty; return true;
            case Adjustment:
                delta = qty; return true;   // signed by the caller
            default:
                error = "MovementType must be RECEIPT, ISSUE, ADJUSTMENT, TRANSFER_IN or TRANSFER_OUT.";
                return false;
        }
    }
}
