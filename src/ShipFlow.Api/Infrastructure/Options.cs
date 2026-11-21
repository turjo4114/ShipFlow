namespace ShipFlow.Api.Infrastructure;

public sealed class CarrierOptions
{
    public string BaseUrl { get; set; } = "http://localhost:8080";
    public bool EnableMock { get; set; } = true;
    public double MockFailureRate { get; set; } = 0.3;
}

public sealed class OutboxOptions
{
    public int PollSeconds { get; set; } = 2;
    public int BatchSize { get; set; } = 20;
    public int LockMinutes { get; set; } = 5;
    public int MaxAttempts { get; set; } = 8;
}
