using Microsoft.Extensions.Hosting;

namespace WpeAgent.Headless;

public sealed class HeadlessRuntimeWorker(IHostApplicationLifetime applicationLifetime) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var exitCode = await HeadlessRuntimeProcess.RunAsync(stoppingToken).ConfigureAwait(false);
        Environment.ExitCode = exitCode;
        applicationLifetime.StopApplication();
    }
}
