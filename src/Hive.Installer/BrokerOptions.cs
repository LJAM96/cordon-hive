namespace Hive.Installer;

public sealed class BrokerOptions
{
    public const string SectionName = "Broker";

    public bool Enabled { get; set; } = false;

    public IReadOnlyList<string> AllowedInstallProfiles { get; set; } = ["validated-msi-no-restart"];

    public string BrokerPipeName { get; set; } = "CordonHive.Installer.Broker";

    public string MaintenanceMutexName { get; set; } = @"Local\CordonHive.MaintenanceMutex";

    public string? TrustedSigningPublicKeyPem { get; set; } = null;

    public string MsiExecPath { get; set; } = "msiexec.exe";
}
