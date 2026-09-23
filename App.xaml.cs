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
    private TradingRuntimeHost? _runtimeHost;
    private WpfLocalizationBridge? _localizationBridge;

    public static IServiceProvider ServiceProvider
    {
        get => _serviceProvider ?? throw new InvalidOperationException("Service provider not initialized");
        set => _serviceProvider = value;
    }

    public App()
    {
        RuntimeProcessBootstrap.Initialize("app.log");
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _localizationBridge = new WpfLocalizationBridge(LocalizationService.Current);

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

        if (e.Args.Any(x => string.Equals(x, "--architecture-test", StringComparison.OrdinalIgnoreCase)))
        {
            ShutdownMode=ShutdownMode.OnExplicitShutdown;
            var result=await ArchitectureTestRunner.RunAsync();
            Log.Information("Runtime architecture tests completed. Success={Success}; Report={Report}",result.Success,result.ReportPath);
            Shutdown(result.Success?0:14);
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

        if (e.Args.Any(x => string.Equals(x, "--configure-testnet-env", StringComparison.OrdinalIgnoreCase)))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var apiKey = Environment.GetEnvironmentVariable("WPE_TESTNET_API_KEY");
            var apiSecret = Environment.GetEnvironmentVariable("WPE_TESTNET_API_SECRET");
            if (string.IsNullOrWhiteSpace(apiKey)) apiKey = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(apiSecret)) apiSecret = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(apiSecret)) { Shutdown(38); return; }
            var store = new AgentSettingsStore();
            var bootstrapSettings = store.Load();
            bootstrapSettings.Environment = ExchangeEnvironment.Testnet;
            bootstrapSettings.EnvironmentMode = "FuturesTestnet";
            bootstrapSettings.ActiveUser = "LOCAL-" + DeviceLicenseService.GetCurrentDeviceCode();
            bootstrapSettings.Symbols = ["BTCUSDT"];
            bootstrapSettings.MainnetTradingConfirmed = false;
            bootstrapSettings.MainnetConfirmedAtUtc = null;
            var profile = store.GetActiveExchange(bootstrapSettings, false);
            profile.ProviderId = "binance-futures";
            profile.DisplayName = "Binance Futures Testnet";
            profile.Enabled = true;
            profile.ExecutionEnabled = true;
            profile.IsTestnet = true;
            profile.Endpoint = "https://testnet.binancefuture.com";
            store.SaveEnvironmentExchange(bootstrapSettings, profile, apiKey, apiSecret);
            Shutdown(0);
            return;
        }

        if (e.Args.Any(x => string.Equals(x, "--finalize-testnet-auto", StringComparison.OrdinalIgnoreCase)))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var finalizeStore = new AgentSettingsStore();
            var finalizeSettings = finalizeStore.Load();
            var readiness = await new AccessReadinessService().CheckAsync(finalizeSettings);
            if (!readiness.Ready) { Shutdown(39); return; }
            finalizeSettings.LastAccessCheckAtUtc = readiness.CheckedAtUtc;
            var finalizeProfile = finalizeStore.GetActiveExchange(finalizeSettings, false);
            finalizeProfile.LastVerifiedAtUtc = readiness.CheckedAtUtc;
            finalizeStore.SyncActiveExchangeToEnvironmentSlot(finalizeSettings, finalizeProfile);
            finalizeStore.Save(finalizeSettings);
            Console.Error.WriteLine($"AUTO_PRECHECK env={finalizeSettings.Environment}; isTestnet={finalizeProfile.IsTestnet}; enabled={finalizeProfile.Enabled}; execution={finalizeProfile.ExecutionEnabled}; read={finalizeProfile.ReadPermission}; trade={finalizeProfile.TradePermission}; access={finalizeSettings.LastAccessCheckAtUtc:O}; verified={finalizeProfile.LastVerifiedAtUtc:O}");
            var audit = new AgentSqliteStore();
            var changed = await finalizeStore.ChangeAuthorizationModeAfterReadinessAsync(
                finalizeSettings,
                finalizeSettings.ActiveUser,
                DeviceLicenseService.GetCurrentDeviceCode(),
                "Target-machine Testnet readiness verified for automatic execution.",
                audit,
                CancellationToken.None);
            if (!changed.Changed && changed.Code != "authorization.mode-unchanged") { Console.Error.WriteLine(changed.Code); Shutdown(40); return; }
            finalizeSettings.SetupCompleted = true;
            finalizeSettings.SetupCompletedAtUtc = DateTime.UtcNow;
            finalizeStore.Save(finalizeSettings);
            Shutdown(0);
            return;
        }

        if (e.Args.Any(x => string.Equals(x, "--testnet-state-diagnostic", StringComparison.OrdinalIgnoreCase)))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var diagnosticStore = new AgentSettingsStore();
            var diagnosticSettings = diagnosticStore.Load();
            var diagnosticProfile = diagnosticStore.GetActiveExchange(diagnosticSettings);
            if (diagnosticSettings.Environment != ExchangeEnvironment.Testnet || !diagnosticProfile.IsTestnet) { Shutdown(45); return; }
            var diagnosticCredentials = diagnosticStore.GetExchangeCredentials(diagnosticProfile);
            var diagnosticCatalog = new ExchangeProviderCatalog();
            await using var diagnosticProvider = diagnosticCatalog.Create(diagnosticProfile, diagnosticCredentials);
            var diagnosticPositions = await diagnosticProvider.GetPositionsAsync(CancellationToken.None);
            var diagnosticOrders = await diagnosticProvider.GetOpenOrdersAsync(null, CancellationToken.None);
            var diagnosticRecent = new List<ExchangeOrder>();
            if (diagnosticProvider is IRecentOrderProvider recentOrderProvider)
            {
                foreach (var diagnosticSymbol in diagnosticSettings.Symbols.Distinct(StringComparer.OrdinalIgnoreCase))
                    diagnosticRecent.AddRange(await recentOrderProvider.GetRecentOrdersAsync(diagnosticSymbol, 50, CancellationToken.None));
            }
            var diagnosticRoot = Path.Combine(AppDataPaths.TestArtifactsDirectory, "testnet-state-diagnostic");
            Directory.CreateDirectory(diagnosticRoot);
            var diagnosticPath = Path.Combine(diagnosticRoot, $"state-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json");
            var diagnosticPayload = new
            {
                observedAtUtc = DateTimeOffset.UtcNow,
                positions = diagnosticPositions.Select(x => new { x.Symbol, side = x.Side.ToString(), x.Quantity, x.EntryPrice, x.MarkPrice, x.Leverage, x.Isolated }),
                openOrders = diagnosticOrders.Select(x => new { x.Symbol, x.ClientOrderId, x.Status, x.ExecutedQuantity, x.AvgPrice, x.Type, side = x.PositionSide?.ToString(), x.IsProtection, x.UpdatedAt }),
                recentOrders = diagnosticRecent.OrderByDescending(x => x.UpdatedAt).Take(50).Select(x => new { x.Symbol, x.ClientOrderId, x.Status, x.ExecutedQuantity, x.AvgPrice, x.Type, side = x.PositionSide?.ToString(), x.IsProtection, x.UpdatedAt })
            };
            await File.WriteAllTextAsync(diagnosticPath, System.Text.Json.JsonSerializer.Serialize(diagnosticPayload, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            Log.Information("Testnet state diagnostic completed. Report={Report}", diagnosticPath);
            Shutdown(0);
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


        // Keep the shell alive across setup/main-window transitions. Runtime identity is local
        // machine identity; the legacy commercial license gate is no longer a startup prerequisite.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var localIdentity = "LOCAL-" + DeviceLicenseService.GetCurrentDeviceCode();
        var runtimeHost = new TradingRuntimeHost(localIdentity);
        _runtimeHost = runtimeHost;
        var settings = new AgentSettingsStore().Load();
        if (!settings.SetupCompleted)
        {
            var setup = new SetupWindow(localIdentity);
            if (setup.ShowDialog() != true || !setup.SetupCompleted) { Shutdown(); return; }
        }
        var accessReady = await runtimeHost.InitializeAsync();
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
        }, runtimeHost.StartAgentAsync);
        MainWindow = reference;
        reference.Show();
        if (accessReady) await runtimeHost.StartAgentAsync();
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        try
        {
            if (_runtimeHost is not null)
            {
                await _runtimeHost.DisposeAsync();
                _runtimeHost = null;
            }
            else
            {
                await ServiceLocator.DisposeAsync();
            }
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
            _localizationBridge?.Dispose();
            _localizationBridge = null;
            Log.CloseAndFlush();
            base.OnExit(e);
        }
    }

}