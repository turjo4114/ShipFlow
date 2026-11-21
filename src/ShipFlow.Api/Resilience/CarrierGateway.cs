using System.Text;

namespace ShipFlow.Api.Resilience;

public interface ICarrierGateway
{
    /// <summary>Pushes one outbox message to the external carrier. Throws on failure after resilience handling.</summary>
    Task SendAsync(long messageId, int tenantId, string messageType, string payloadJson, CancellationToken ct);
}

/// <summary>
/// Thin typed client. Retry / circuit breaker / timeouts are NOT here - they are a pipeline registered on this
/// HttpClient in Program.cs, so the business code stays free of fault-tolerance plumbing.
/// </summary>
public sealed class CarrierGateway(HttpClient http) : ICarrierGateway
{
    public async Task SendAsync(long messageId, int tenantId, string messageType, string payloadJson, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/_mock/carrier/updates")
        {
            Content = new StringContent(payloadJson, Encoding.UTF8, "application/json")
        };
        // Delivery is at-least-once, so the carrier must be able to de-duplicate: stable key per outbox row.
        req.Headers.Add("Idempotency-Key", $"shipflow-outbox-{messageId}");
        req.Headers.Add("X-Tenant-Id", tenantId.ToString());
        req.Headers.Add("X-Message-Type", messageType);

        using var res = await http.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();
    }
}
