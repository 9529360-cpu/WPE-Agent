using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using WpeAgent.Headless;
using 币安量化机器人.Services;

if (args.Length > 0 && string.Equals(args[0], "control", StringComparison.Ordinal))
    return await HeadlessLocalControlClient.RunAsync(args[1..]);

var dataRoot = HeadlessDataRootBootstrap.Resolve(
    args,
    Environment.GetEnvironmentVariable(AppDataPaths.DataRootEnvironmentVariable));
if (!dataRoot.Success)
    return HeadlessRuntimeProcess.ServiceDataRootInvalidExitCode;
if (dataRoot.Root is not null)
    Environment.SetEnvironmentVariable(
        AppDataPaths.DataRootEnvironmentVariable,
        dataRoot.Root,
        EnvironmentVariableTarget.Process);

var runningAsService = WindowsServiceHelpers.IsWindowsService();
if (runningAsService && dataRoot.Root is null)
    return HeadlessRuntimeProcess.ServiceDataRootRequiredExitCode;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options => options.ServiceName = "WPE Agent Headless");
builder.Services.AddHostedService<HeadlessRuntimeWorker>();
builder.Services.AddHostedService<HeadlessLocalControlWorker>();

using var host = builder.Build();
await host.RunAsync();
return Environment.ExitCode;
