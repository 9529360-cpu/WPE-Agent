using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;

namespace WpeAgent.Headless;

public sealed class HeadlessRuntimeWorker(IHostApplicationLifetime applicationLifetime) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var exitCode = await HeadlessRuntimeProcess.RunAsync(stoppingToken).ConfigureAwait(false);
        Environment.ExitCode = exitCode;

        if (exitCode != 0 && WindowsServiceHelpers.IsWindowsService())
        {
            // Windows SCM recovery actions observe the non-zero process termination.
            Environment.Exit(exitCode);
        }

        applicationLifetime.StopApplication();
    }
}
