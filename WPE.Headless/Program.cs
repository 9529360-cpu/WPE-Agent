using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using WpeAgent.Headless;
using 币安量化机器人.Services;

var runningAsService = WindowsServiceHelpers.IsWindowsService();
if (runningAsService)
{
    var dataRoot = Environment.GetEnvironmentVariable(AppDataPaths.DataRootEnvironmentVariable);
    if (string.IsNullOrWhiteSpace(dataRoot) || !Path.IsPathRooted(dataRoot))
        return HeadlessRuntimeProcess.ServiceDataRootRequiredExitCode;
}

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options => options.ServiceName = "WPE Agent Headless");
builder.Services.AddHostedService<HeadlessRuntimeWorker>();

using var host = builder.Build();
await host.RunAsync();
return Environment.ExitCode;
