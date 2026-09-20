using System.Windows;
using System.IO;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using 币安量化机器人.Services;
using 币安量化机器人.Services.Agent;
using 币安量化机器人.Services.Access;
using 币安量化机器人.Services.Localization;
using 币安量化机器人.Services.Exchange;

namespace 币安量化机器人;

public partial class App : global::System.Windows.Application
{
    private static IServiceProvider? _serviceProvider;
    private DesktopRuntimeHost? _runtimeHost;

    public static IServiceProvider ServiceProvider
    {
        get => _serviceProvider ?? throw new InvalidOperationException("Service provider not initialized");
        set => _serviceProvider = value;
    }

    public App()
    {
        var migration = AppDataPaths.MigrateLegacyPortableData();
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.Sink(new SensitiveFileLogSink(AppDataPaths.LogFile("app.log")))
            .CreateLogger();
        Log.Information("Portable data migration completed. Copied={Copied}; Skipped={Skipped}; Failed={Failed}", migration.Copied, migration.Skipped, migration.Failed);
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        LocalizationService.Current.Initialize();

        if (e.Args.Any(x => string.Equals(x, "--i18n-test", StringComparison.OrdinalIgnoreCase)))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var result = LocalizationSelfTest.Run();
            Log.Information("Localization self-test completed. Success={Success}; Report={Report}", result.Success, result.ReportPath);
            Shutdown(result.Success ? 0 : 4);
            return;
        }

        if (e.Args.Any(x => string.Equals(x, "--exchange-adapter-test", StringComparison.OrdinalIgnoreCase)))
        {
            ShutdownMode=ShutdownMode.OnExplicitShutdown;
            var result=await ExchangeAdapterTestRunner.RunAsync();
            Log.Information("Exchange adapter tests completed. Success={Success}; Report={Report}",result.Success,result.ReportPath);
            Shutdown(result.Success?0:12);
            return;
        }

        if (e.Args.Any(x => string.Equals(x, "--exchange-public-test", StringComparison.OrdinalIgnoreCase)))
        {
            ShutdownMode=ShutdownMode.OnExplicitShutdown;
            var result=await ExchangePublicTestRunner.RunAsync();
            Log.Information("Exchange public endpoint tests completed. Success={Success}; Report={Report}",result.Success,result.ReportPath);
            Shutdown(result.Success?0:13);
            return;
        }

        if (e.Args.Any(x => string.Equals(x, "--provider-readonly-access", StringComparison.OrdinalIgnoreCase)))
        {
            ShutdownMode=ShutdownMode.OnExplicitShutdown;
            var result=await ProviderReadOnlyAccessRunner.RunConfiguredAsync();
            Log.Information("Provider read-only access completed. Success={Success}; Report={Report}",result.Success,result.ReportPath);
            Shutdown(result.Success?0:16);
            return;
        }

        if (e.Args.Any(x => string.Equals(x, "--teacher-v2-acceptance", StringComparison.OrdinalIgnoreCase)))
        {
            ShutdownMode=ShutdownMode.OnExplicitShutdown;
            var result=await TeacherV2AcceptanceRunner.RunAsync(e.Args);
            Log.Information("Teacher V2 acceptance collection completed. Success={Success}; Directory={Directory}; Errors={Errors}",result.Success,result.ArtifactDirectory,string.Join(',',result.Errors));
            Shutdown(result.Success?0:31);
            return;
        }

        if (e.Args.Any(x => string.Equals(x, "--market-aggregate-acceptance", StringComparison.OrdinalIgnoreCase)))
        {
            ShutdownMode=ShutdownMode.OnExplicitShutdown;
            var result=await WpeAgent.ModelOff.MarketAggregateAcceptanceRunnerV1.RunAsync();
            Log.Information("Market aggregate acceptance collection completed. Success={Success}; Directory={Directory}",result.Success,result.ArtifactDirectory);
            Shutdown(result.Success?0:17);
            return;
        }

        if (e.Args.Any(x => string.Equals(x, "--market-local-acceptance", StringComparison.OrdinalIgnoreCase)))
        {
            ShutdownMode=ShutdownMode.OnExplicitShutdown;
            var result=await WpeAgent.ModelOff.MarketLocalAcceptanceRunnerV1.RunAsync(e.Args);
            Log.Information("Market local acceptance collection completed. Success={Success}; Directory={Directory}; Errors={Errors}",result.Success,result.ArtifactDirectory,string.Join(',',result.Errors));
            Shutdown(result.Success?0:18);
            return;
        }

        if (e.Args.Any(x => string.Equals(x, "--market-acceptance-assess", StringComparison.OrdinalIgnoreCase)))
        {
            ShutdownMode=ShutdownMode.OnExplicitShutdown;
            var result=await WpeAgent.ModelOff.MarketAcceptanceAssessmentRunnerV1.RunAsync(e.Args);
            Log.Information("Market aggregate assessment completed. Accepted={Accepted}; Report={Report}; Errors={Errors}",result.Accepted,result.ReportPath,string.Join(',',result.Errors));
            Shutdown(result.Accepted?0:19);
            return;
        }

        if (e.Args.Any(x => string.Equals(x, "--research-local-acceptance", StringComparison.OrdinalIgnoreCase)))
        {
            ShutdownMode=ShutdownMode.OnExplicitShutdown;
            var result=await WpeAgent.ModelOff.ResearchLocalAcceptanceRunnerV1.RunAsync(e.Args);
            Log.Information("Research local acceptance collection completed. Success={Success}; Directory={Directory}; Errors={Errors}",result.Success,result.ArtifactDirectory,string.Join(',',result.Errors));
            Shutdown(result.Success?0:20);
            return;
        }
        if (e.Args.Any(x => string.Equals(x, "--strategy-local-acceptance", StringComparison.OrdinalIgnoreCase)))
        {
            ShutdownMode=ShutdownMode.OnExplicitShutdown;
            var result=await WpeAgent.ModelOff.StrategyLocalAcceptanceRunnerV1.RunAsync(e.Args);
            Log.Information("Strategy local acceptance collection completed. Success={Success}; Directory={Directory}; Errors={Errors}",result.Success,result.ArtifactDirectory,string.Join(',',result.Errors));
            Shutdown(result.Success?0:23);
            return;
        }
        if (e.Args.Any(x => string.Equals(x, "--strategy-aggregate-acceptance", StringComparison.OrdinalIgnoreCase)))
        {
            ShutdownMode=ShutdownMode.OnExplicitShutdown;
            var result=await WpeAgent.ModelOff.StrategyAggregateAcceptanceRunnerV1.RunAsync();
            Log.Information("Strategy aggregate acceptance collection completed. Success={Success}; Directory={Directory}; Errors={Errors}",result.Success,result.ArtifactDirectory,string.Join(',',result.Errors));
            Shutdown(result.Success?0:24);
            return;
        }
        if (e.Args.Any(x => string.Equals(x, "--strategy-acceptance-assess", StringComparison.OrdinalIgnoreCase)))
        {
            ShutdownMode=ShutdownMode.OnExplicitShutdown;
            var result=await WpeAgent.ModelOff.StrategyAcceptanceAssessmentRunnerV1.RunAsync(e.Args);
            Log.Information("Strategy aggregate assessment completed. Accepted={Accepted}; Report={Report}; Errors={Errors}",result.Accepted,result.ReportPath,string.Join(',',result.Errors));
            Shutdown(result.Accepted?0:25);
            return;
        }
        if (e.Args.Any(x => string.Equals(x, "--risk-local-acceptance", StringComparison.OrdinalIgnoreCase)))
        {
            ShutdownMode=ShutdownMode.OnExplicitShutdown;
            var result=await WpeAgent.ModelOff.RiskLocalAcceptanceRunnerV1.RunAsync(e.Args);
            Log.Information("Risk local acceptance collection completed. Success={Success}; Directory={Directory}; Errors={Errors}",result.Success,result.ArtifactDirectory,string.Join(',',result.Errors));
            Shutdown(result.Success?0:26);
            return;
        }
        if (e.Args.Any(x => string.Equals(x, "--risk-aggregate-acceptance", StringComparison.OrdinalIgnoreCase)))
        {
            ShutdownMode=ShutdownMode.OnExplicitShutdown;
            var result=await WpeAgent.ModelOff.RiskAggregateAcceptanceRunnerV1.RunAsync();
            Log.Information("Risk aggregate acceptance collection completed. Success={Success}; Directory={Directory}; Errors={Errors}",result.Success,result.ArtifactDirectory,string.Join(',',result.Errors));
            Shutdown(result.Success?0:27);
            return;
        }
        if (e.Args.Any(x => string.Equals(x, "--risk-acceptance-assess", StringComparison.OrdinalIgnoreCase)))
        {
            ShutdownMode=ShutdownMode.OnExplicitShutdown;
            var result=await WpeAgent.ModelOff.RiskAcceptanceAssessmentRunnerV1.RunAsync(e.Args);
            Log.Information("Risk aggregate assessment completed. Accepted={Accepted}; Report={Report}; Errors={Errors}",result.Accepted,result.ReportPath,string.Join(',',result.Errors));
            Shutdown(result.Accepted?0:28);
            return;
        }
        if (e.Args.Any(x => string.Equals(x, "--execution-local-acceptance", StringComparison.OrdinalIgnoreCase)))
        {
            ShutdownMode=ShutdownMode.OnExplicitShutdown;
            var result=await WpeAgent.ModelOff.ExecutionLocalAcceptanceRunnerV1.RunAsync(e.Args);
            Log.Information("Execution local acceptance collection completed. Success={Success}; Directory={Directory}; Errors={Errors}",result.Success,result.ArtifactDirectory,string.Join(',',result.Errors));
            Shutdown(result.Success?0:29);
            return;
        }
        if (e.Args.Any(x => string.Equals(x, "--execution-aggregate-acceptance", StringComparison.OrdinalIgnoreCase)))
        {
            ShutdownMode=ShutdownMode.OnExplicitShutdown;
            var result=await WpeAgent.ModelOff.ExecutionAggregateAcceptanceRunnerV1.RunAsync();
            Log.Information("Execution aggregate acceptance collection completed. Success={Success}; Directory={Directory}; Errors={Errors}",result.Success,result.ArtifactDirectory,string.Join(',',result.Errors));
            Shutdown(result.Success?0:30);
            return;
        }
        if (e.Args.Any(x => string.Equals(x, "--execution-acceptance-assess", StringComparison.OrdinalIgnoreCase)))
        {
            ShutdownMode=ShutdownMode.OnExplicitShutdown;
            var result=await WpeAgent.ModelOff.ExecutionAcceptanceAssessmentRunnerV1.RunAsync(e.Args);
            Log.Information("Execution aggregate assessment completed. Accepted={Accepted}; Report={Report}; Errors={Errors}",result.Accepted,result.ReportPath,string.Join(',',result.Errors));
            Shutdown(result.Accepted?0:31);
            return;
        }
        if (e.Args.Any(x => string.Equals(x, "--recovery-local-acceptance", StringComparison.OrdinalIgnoreCase)))
        {
            ShutdownMode=ShutdownMode.OnExplicitShutdown;
            var result=await WpeAgent.ModelOff.RecoveryLocalAcceptanceRunnerV1.RunAsync(e.Args);
            Log.Information("Recovery local acceptance collection completed. Success={Success}; Directory={Directory}; Errors={Errors}",result.Success,result.ArtifactDirectory,string.Join(',',result.Errors));
            Shutdown(result.Success?0:32);
            return;
        }
        if (e.Args.Any(x => string.Equals(x, "--recovery-aggregate-acceptance", StringComparison.OrdinalIgnoreCase)))
        {
            ShutdownMode=ShutdownMode.OnExplicitShutdown;
            var result=await WpeAgent.ModelOff.RecoveryAggregateAcceptanceRunnerV1.RunAsync();
            Log.Information("Recovery aggregate acceptance collection completed. Success={Success}; Directory={Directory}; Errors={Errors}",result.Success,result.ArtifactDirectory,string.Join(',',result.Errors));
            Shutdown(result.Success?0:33);
            return;
        }
        if (e.Args.Any(x => string.Equals(x, "--recovery-acceptance-assess", StringComparison.OrdinalIgnoreCase)))
        {
            ShutdownMode=ShutdownMode.OnExplicitShutdown;
            var result=await WpeAgent.ModelOff.RecoveryAcceptanceAssessmentRunnerV1.RunAsync(e.Args);
            Log.Information("Recovery aggregate assessment completed. Accepted={Accepted}; Report={Report}; Errors={Errors}",result.Accepted,result.ReportPath,string.Join(',',result.Errors));
            Shutdown(result.Accepted?0:34);
            return;
        }
        if (e.Args.Any(x => string.Equals(x, "--audit-local-acceptance", StringComparison.OrdinalIgnoreCase)))
        {
            ShutdownMode=ShutdownMode.OnExplicitShutdown;
            var result=await WpeAgent.ModelOff.AuditLocalAcceptanceRunnerV1.RunAsync(e.Args);
            Log.Information("Audit local acceptance collection completed. Success={Success}; Directory={Directory}; Errors={Errors}",result.Success,result.ArtifactDirectory,string.Join(',',result.Errors));
            Shutdown(result.Success?0:35);
            return;
        }
        if (e.Args.Any(x => string.Equals(x, "--audit-aggregate-acceptance", StringComparison.OrdinalIgnoreCase)))
        {
            ShutdownMode=ShutdownMode.OnExplicitShutdown;
            var result=await WpeAgent.ModelOff.AuditAggregateAcceptanceRunnerV1.RunAsync();
            Log.Information("Audit aggregate acceptance collection completed. Success={Success}; Directory={Directory}; Errors={Errors}",result.Success,result.ArtifactDirectory,string.Join(',',result.Errors));
            Shutdown(result.Success?0:36);
            return;
        }
        if (e.Args.Any(x => string.Equals(x, "--audit-acceptance-assess", StringComparison.OrdinalIgnoreCase)))
        {
            ShutdownMode=ShutdownMode.OnExplicitShutdown;
            var result=await WpeAgent.ModelOff.AuditAcceptanceAssessmentRunnerV1.RunAsync(e.Args);
            Log.Information("Audit aggregate assessment completed. Accepted={Accepted}; Report={Report}; Errors={Errors}",result.Accepted,result.ReportPath,string.Join(',',result.Errors));
            Shutdown(result.Accepted?0:37);
            return;
        }

        if (e.Args.Any(x => string.Equals(x, "--research-aggregate-acceptance", StringComparison.OrdinalIgnoreCase)))
        {
            ShutdownMode=ShutdownMode.OnExplicitShutdown;
            var result=await WpeAgent.ModelOff.ResearchAggregateAcceptanceRunnerV1.RunAsync();
            Log.Information("Research aggregate acceptance collection completed. Success={Success}; Directory={Directory}; Errors={Errors}",result.Success,result.ArtifactDirectory,string.Join(',',result.Errors));
            Shutdown(result.Success?0:21);
            return;
        }

        if (e.Args.Any(x => string.Equals(x, "--research-acceptance-assess", StringComparison.OrdinalIgnoreCase)))
        {
            ShutdownMode=ShutdownMode.OnExplicitShutdown;
            var result=await WpeAgent.ModelOff.ResearchAcceptanceAssessmentRunnerV1.RunAsync(e.Args);
            Log.Information("Research aggregate assessment completed. Accepted={Accepted}; Report={Report}; Errors={Errors}",result.Accepted,result.ReportPath,string.Join(',',result.Errors));
            Shutdown(result.Accepted?0:22);
            return;
        }

        if (e.Args.Any(x => string.Equals(x, "--architecture-test", StringComparison.OrdinalIgnoreCase)))
        {
            ShutdownMode=ShutdownMode.OnExplicitShutdown;
            var result=await ArchitectureTestRunner.RunAsync();
            Log.Information("Runtime architecture tests completed. Success={Success}; Report={Report}",result.Success,result.ReportPath);
            Shutdown(result.Success?0:14);
            return;
        }
        if (e.Args.Any(x => string.Equals(x, "--strategy-lifecycle-test", StringComparison.OrdinalIgnoreCase)))
        {
            ShutdownMode=ShutdownMode.OnExplicitShutdown;
            var result=await StrategyLifecycleTestRunner.RunAsync();
            Log.Information("Strategy lifecycle tests completed. Success={Success}; Report={Report}",result.Success,result.ReportPath);
            Shutdown(result.Success?0:15);
            return;
        }

        // Configure dependency injection
        var services = new ServiceCollection();
        ServiceProvider = ServiceConfiguration.ConfigureServices(services);

        if (e.Args.Any(x => string.Equals(x, "--license-test", StringComparison.OrdinalIgnoreCase)))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var result = await DeviceLicenseTestRunner.RunAsync();
            Log.Information("Device-license tests completed. Success={Success}; Report={Report}",result.Success,result.ReportPath);
            Shutdown(result.Success?0:10);
            return;
        }

        if (e.Args.Any(x => string.Equals(x, "--device-code", StringComparison.OrdinalIgnoreCase)))
        {
            ShutdownMode=ShutdownMode.OnExplicitShutdown;
            var path=AppDataPaths.RuntimeFile("device-code.txt");await File.WriteAllTextAsync(path,DeviceLicenseService.GetCurrentDeviceCode());
            Shutdown(0);
            return;
        }

        var activationFileIndex=Array.FindIndex(e.Args,x=>string.Equals(x,"--activate-license",StringComparison.OrdinalIgnoreCase));
        if(activationFileIndex>=0)
        {
            ShutdownMode=ShutdownMode.OnExplicitShutdown;
            if(activationFileIndex+1>=e.Args.Length||!File.Exists(e.Args[activationFileIndex+1])){Shutdown(11);return;}
            var activationResult=new DeviceLicenseService().Activate(await File.ReadAllTextAsync(e.Args[activationFileIndex+1]));
            Shutdown(activationResult.Success?0:11);
            return;
        }

        if (e.Args.Any(x => string.Equals(x, "--access-test", StringComparison.OrdinalIgnoreCase)))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var result = await AccessTestRunner.RunAsync();
            Log.Information("Access and credential security tests completed. Success={Success}; Report={Report}", result.Success, result.ReportPath);
            Shutdown(result.Success ? 0 : 8);
            return;
        }

        if (e.Args.Any(x => string.Equals(x, "--access-live-test", StringComparison.OrdinalIgnoreCase)))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var result = await AccessTestRunner.RunLiveAsync();
            Log.Information("Live access readiness test completed. Success={Success}; Report={Report}", result.Success, result.ReportPath);
            Shutdown(result.Success ? 0 : 9);
            return;
        }

        if (e.Args.Any(x => string.Equals(x, "--llm-governance-test", StringComparison.OrdinalIgnoreCase)))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var result = await LlmGovernanceTestRunner.RunAsync();
            Log.Information("LLM governance tests completed. Success={Success}; Report={Report}", result.Success, result.ReportPath);
            Shutdown(result.Success ? 0 : 12);
            return;
        }

        if (e.Args.Any(x => string.Equals(x, "--autonomy-test", StringComparison.OrdinalIgnoreCase)))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var result = await AutonomyTestRunner.RunAsync();
            Log.Information("Autonomy fault-injection tests completed. Success={Success}; Report={Report}", result.Success, result.ReportPath);
            Shutdown(result.Success ? 0 : 5);
            return;
        }

        if (e.Args.Any(x => string.Equals(x, "--four-pillars-test", StringComparison.OrdinalIgnoreCase)))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var result = await FourPillarsTestRunner.RunAsync();
            Log.Information("Four-pillar tests completed. Success={Success}; Report={Report}", result.Success, result.ReportPath);
            Shutdown(result.Success ? 0 : 6);
            return;
        }

        if (e.Args.Any(x => string.Equals(x, "--four-pillars-live-test", StringComparison.OrdinalIgnoreCase)))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var result = await FourPillarsTestRunner.RunLiveAsync();
            Log.Information("Four-pillar live tests completed. Success={Success}; Report={Report}", result.Success, result.ReportPath);
            Shutdown(result.Success ? 0 : 7);
            return;
        }

        if (e.Args.Any(x => string.Equals(x, "--smoke-test", StringComparison.OrdinalIgnoreCase)))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var result = await SmokeTestRunner.RunAsync();
            Log.Information("Testnet smoke test completed. Success={Success}; Report={Report}", result.Success, result.ReportPath);
            Shutdown(result.Success ? 0 : 2);
            return;
        }

        if (e.Args.Any(x => string.Equals(x, "--maturity-test", StringComparison.OrdinalIgnoreCase)))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var result = await MaturityTestRunner.RunAsync();
            Log.Information("Maturity tests completed. Success={Success}; Report={Report}", result.Success, result.ReportPath);
            Shutdown(result.Success ? 0 : 3);
            return;
        }

        _ = StartPublicMarketAsync();

        // 激活窗关闭后还要继续打开初始化向导或主控制台，不能让 WPF
        // 在两个窗口切换的间隙按“最后窗口关闭”自动终止应用。
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var licenseService = new DeviceLicenseService();
        var license = licenseService.TryLoad();
        if (!license.Success)
        {
            var activation = new ActivationWindow(licenseService);
            if (activation.ShowDialog() != true || activation.ActivatedLicense is null) { Shutdown(); return; }
            license = new DeviceLicenseResult(true,"Activation.Valid",activation.ActivatedLicense);
        }
        var localIdentity = "DEVICE-" + license.License!.LicenseId;
        var settingsStore = new AgentSettingsStore();
        var settings = settingsStore.Load();
        settings.ActiveUser = localIdentity;
        settingsStore.Save(settings);
        var runtimeMode = RuntimeModePolicy.Resolve(settings);
        var state = ServiceLocator.SystemState;
        state.BrainMode = runtimeMode.RequestedMode;
        state.BrainEffectiveMode = runtimeMode.EffectiveMode;
        state.BrainRemoteAllowed = runtimeMode.AllowRemoteBrain;
        state.BrainFallbackReason = runtimeMode.FallbackReason;
        state.ActiveBrainProvider = runtimeMode.ProviderName;
        state.ActiveBrainModel = runtimeMode.ModelName;
        state.BrainName = runtimeMode.EffectiveMode == Core.Models.AiRuntimeMode.LocalOnly ? "WPE Local Brain" : runtimeMode.ProviderName;
        state.LastUpdated = DateTime.UtcNow;
        if (!settings.SetupCompleted)
        {
            var setup = new SetupWindow(localIdentity);
            if (setup.ShowDialog() != true || !setup.SetupCompleted) { Shutdown(); return; }
        }
        var runtimeHost = new DesktopRuntimeHost(localIdentity);
        _runtimeHost = runtimeHost;
        var accessReady = await runtimeHost.RefreshAccessAsync();
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        var reference = new WpeAgent.ReferenceUiWindow(runtimeHost.BuildRuntimeJson, () =>
        {
            var setup = new SetupWindow(runtimeHost.UserName, true);
            setup.ShowDialog();
            _ = runtimeHost.RefreshAccessAsync();
        }, () =>
        {
            var setup = new SetupWindow(runtimeHost.UserName, true, 6);
            setup.ShowDialog();
            _ = runtimeHost.RefreshAccessAsync();
        }, runtimeHost.StartAgentAsync, runtimeHost.BuildHistoricalPageResponseJsonAsync);
        MainWindow = reference;
        reference.Show();
        if (accessReady) AutoTradingAgent.StartDefault();
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        try
        {
            await AutoTradingAgent.StopAsync();
            if (_runtimeHost is not null)
            {
                await _runtimeHost.DisposeAsync();
                _runtimeHost = null;
            }
            await ServiceLocator.DisposeAsync();
            if (ServiceProvider is IAsyncDisposable asyncDisposable)
            {
                await asyncDisposable.DisposeAsync();
            }
            else if (ServiceProvider is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
        finally
        {
            Log.CloseAndFlush();
            base.OnExit(e);
        }
    }

    private static async Task StartPublicMarketAsync()
    {
        try
        {
            await ServiceLocator.PublicMarket.StartAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Public market runtime failed to start and remains unavailable.");
        }
    }
}
