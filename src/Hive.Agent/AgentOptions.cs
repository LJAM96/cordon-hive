using Hive.Contracts;
using Hive.Windows.Inventory;

namespace Hive.Agent;

public sealed class AgentOptions
{
    public const string SectionName = "Agent";

    public string ApiBaseUrl { get; set; } = string.Empty;

    public string SiteId { get; set; } = "default";

    public int HeartbeatIntervalSeconds { get; set; } = ProtocolConstants.DefaultHeartbeatSeconds;

    public int LocalSampleIntervalSeconds { get; set; } = 2;

    public int TelemetryUploadIntervalSeconds { get; set; } = 10;

    public int VolumeSampleIntervalSeconds { get; set; } = 60;

    public int ProcessSampleIntervalSeconds { get; set; } = 30;

    public int InventoryIntervalMinutes { get; set; } = 15;

    public bool IncludeCurrentUserRegistry { get; set; } = false;

    public List<PortableAppRule> PortableApps { get; set; } = [];

    public string DatabasePath { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "CordonHive",
        "agent.db");

    public string? CertificateThumbprint { get; set; }

    public string UserPipeName { get; set; } = "CordonHive.Agent.User";

    public int MaxQueueItems { get; set; } = 1000;
}
