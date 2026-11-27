using Hive.Agent;
using Hive.Infrastructure.Sqlite;
using Hive.Windows.Inventory;
using Hive.Windows.SystemInfo;
using Hive.Windows.Telemetry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

if (args.Length > 0 && string.Equals(args[0], "enroll", StringComparison.OrdinalIgnoreCase))
{
    var configBuilder = Host.CreateApplicationBuilder();
    configBuilder.Services.Configure<AgentOptions>(
        configBuilder.Configuration.GetSection(AgentOptions.SectionName));
    configBuilder.Services.AddSingleton<AgentLocalStore>(sp =>
    {
        var options = sp.GetRequiredService<IOptions<AgentOptions>>().Value;
        return AgentLocalStore.FromFilePath(options.DatabasePath, options.MaxQueueItems);
    });
    configBuilder.Services.AddSingleton<IAgentLocalStore>(sp => sp.GetRequiredService<AgentLocalStore>());
    configBuilder.Services.AddSingleton<WindowsMachineInfoProvider>();
    configBuilder.Services.AddSingleton<AgentHttpClientFactory>();

    using var provider = configBuilder.Services.BuildServiceProvider();
    var options = provider.GetRequiredService<IOptions<AgentOptions>>().Value;
    var store = provider.GetRequiredService<IAgentLocalStore>();
    var machineInfo = provider.GetRequiredService<WindowsMachineInfoProvider>();
    var httpClientFactory = provider.GetRequiredService<AgentHttpClientFactory>();
    using var enrollClient = httpClientFactory.CreateClient(certificateThumbprint: null);

    return await AgentEnrollmentBootstrapper.RunAsync(
        args,
        options,
        store,
        machineInfo,
        enrollClient,
        Console.Out,
        Console.Error).ConfigureAwait(false);
}

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "CordonHiveAgent";
});

builder.Services.Configure<AgentOptions>(
    builder.Configuration.GetSection(AgentOptions.SectionName));

builder.Services.AddSingleton<AgentLocalStore>(sp =>
{
    var options = sp.GetRequiredService<IOptions<AgentOptions>>().Value;
    return AgentLocalStore.FromFilePath(options.DatabasePath, options.MaxQueueItems);
});
builder.Services.AddSingleton<IAgentLocalStore>(sp => sp.GetRequiredService<AgentLocalStore>());
builder.Services.AddSingleton<WindowsMachineInfoProvider>();
builder.Services.AddSingleton<ITelemetryCollector, WindowsTelemetryCollector>();
builder.Services.AddSingleton<ISoftwareInventoryCollector, WindowsSoftwareInventoryCollector>();
builder.Services.AddSingleton<AgentRuntimeState>();
builder.Services.AddSingleton<AgentHttpClientFactory>();
builder.Services.AddSingleton(sp => sp.GetRequiredService<AgentHttpClientFactory>().CreateClient());

builder.Services.AddHostedService<HeartbeatWorker>();
builder.Services.AddHostedService<TelemetryWorker>();
builder.Services.AddHostedService<InventoryWorker>();
builder.Services.AddHostedService<AgentUserIpcWorker>();

var host = builder.Build();
await host.RunAsync().ConfigureAwait(false);
return 0;
