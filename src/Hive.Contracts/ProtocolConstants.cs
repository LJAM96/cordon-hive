namespace Hive.Contracts;

/// <summary>
/// Shared protocol, API versioning, and framing limits across server, agent, and UI.
/// </summary>
public static class ProtocolConstants
{
    public const string ApiPrefix = "/api/v1";
    public const string ApiVersion = "v1";
    public const int ProtocolVersion = 1;
    public const int IpcProtocolVersion = 1;
    public const int MaxIpcMessageBytes = 65536;
    public const int DefaultHeartbeatSeconds = 15;
    public const string CorrelationIdHeader = "X-Correlation-Id";
    public const string IdempotencyKeyHeader = "Idempotency-Key";
}
