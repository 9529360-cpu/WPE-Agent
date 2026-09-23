using System.Diagnostics;
using 币安量化机器人.Core.Models;
using 币安量化机器人.Services.Agent;
using 币安量化机器人.Services.Exchange;

namespace 币安量化机器人.Services.Access;

public sealed record AccessCheck(string Key, bool Passed, bool Critical, string Detail, long DurationMs = 0);

public enum OptionalModelDiagnosticStatusV1 { Succeeded, TimedOut, Cancelled, Malformed, Failed }

public sealed record OptionalModelDiagnosticV1(OptionalModelDiagnosticStatusV1 Status, string Detail, long DurationMs)
{
    public const string Schema = "wpe.optional-model-diagnostic/1.0";
    public bool Authoritative => false;
    public bool UsedForDecision => false;
}

public static class OptionalModelDiagnosticRunnerV1
{
    public static async Task<OptionalModelDiagnosticV1> RunAsync(
        Func<CancellationToken, Task<string?>> probe,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(probe);
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        var sw = Stopwatch.StartNew();
        if (cancellationToken.IsCancellationRequested)
            return new(OptionalModelDiagnosticStatusV1.Cancelled, "optional diagnostic cancelled", sw.ElapsedMilliseconds);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        try
        {
            var detail = await probe(timeoutCts.Token).WaitAsync(timeoutCts.Token);
            return string.IsNullOrWhiteSpace(detail)
                ? new(OptionalModelDiagnosticStatusV1.Malformed, "optional diagnostic returned no usable detail", sw.ElapsedMilliseconds)
                : new(OptionalModelDiagnosticStatusV1.Succeeded, SafeDiagnostic(detail), sw.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new(OptionalModelDiagnosticStatusV1.Cancelled, "optional diagnostic cancelled", sw.ElapsedMilliseconds);
        }
        catch (OperationCanceledException)
        {
            return new(OptionalModelDiagnosticStatusV1.TimedOut, "optional diagnostic timed out", sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            return new(OptionalModelDiagnosticStatusV1.Failed, $"optional diagnostic failed: {ex.GetType().Name}", sw.ElapsedMilliseconds);
        }
    }

    private static string SafeDiagnostic(string detail) =>
        global::币安量化机器人.Services.SensitiveDataRedactor.ForLog(detail, 180);
}

public sealed class AccessReadinessReport
{
    public DateTime CheckedAtUtc { get; init; } = DateTime.UtcNow;
    public List<AccessCheck> Checks { get; } = [];
    public bool Ready => Checks.Count > 0 && Checks.Where(x => x.Critical).All(x => x.Passed);
    public string Summary => string.Join(Environment.NewLine, Checks.Select(x => $"{(x.Passed ? "PASS" : "FAIL")} {x.Detail}"));
}

public sealed class AccessReadinessService
{
    public async Task<AccessReadinessReport> CheckAsync(AgentSettings settings, CancellationToken ct = default)
    {
        var report = new AccessReadinessReport();
        var providerCanRead = false;
        void Add(string key, bool pass, bool critical, string detail, long ms = 0) => report.Checks.Add(new(key, pass, critical, detail, ms));

        Add("runtime_mode", true, true, "Local deterministic trading brain");

        var settingsStore = new AgentSettingsStore();
        var profile = settingsStore.GetActiveExchange(settings);
        Add("environment", profile?.IsTestnet == true, true, profile?.IsTestnet == true ? "Testnet isolation enabled" : "Mainnet execution is blocked");

        var catalog = new ExchangeProviderCatalog();
        var installed = profile is not null && catalog.IsInstalled(profile.ProviderId);
        Add("provider", installed, true, profile is null ? "No execution exchange selected" : installed ? $"{profile.DisplayName} adapter loaded" : $"{profile.DisplayName} adapter is not installed");

        IReadOnlyDictionary<string, string> credentials = new Dictionary<string, string>();
        if (profile is not null)
        {
            try
            {
                credentials = settingsStore.GetExchangeCredentials(profile);
            }
            catch (Exception ex)
            {
                Add("credential_storage", false, true, "Credential storage unavailable: " + Safe(ex.GetType().Name));
            }
        }

        var descriptor = profile is null ? null : catalog.All.FirstOrDefault(x => x.Id.Equals(profile.ProviderId, StringComparison.OrdinalIgnoreCase));
        var credentialsReady = descriptor is not null &&
            descriptor.CredentialFields.Where(x => x.Required)
                .All(x => credentials.TryGetValue(x.Key, out var value) && value.Length > 4);
        Add("credentials", credentialsReady, true, credentialsReady ? $"{profile!.DisplayName} credentials loaded from secure storage" : "Exchange credentials are incomplete");

        if (profile is not null && profile.IsTestnet && installed && credentialsReady)
        {
            try
            {
                var sw = Stopwatch.StartNew();
                await using var provider = catalog.Create(profile, credentials);
                var health = await provider.HealthCheckAsync(ct);
                var permission = await provider.CheckPermissionsAsync(ct);
                var account = await provider.GetAccountAsync(ct);
                var binanceClock=provider is BinanceFuturesAdapter binance?await binance.GetClockMeasurementAsync(ct):null;
                providerCanRead = permission.CanRead;
                profile.ReadPermission = permission.CanRead;
                profile.TradePermission = permission.CanTrade;
                profile.WithdrawPermission = permission.CanWithdraw;
                profile.AccountId = permission.AccountId;
                profile.LastVerifiedAtUtc = binanceClock is not null&&binanceClock.Trusted&&provider is BinanceFuturesAdapter adjustedBinance
                    ? adjustedBinance.ExchangeAdjustedUtcNow.UtcDateTime
                    : DateTime.UtcNow;
                Add("exchange", health.Healthy, true, $"{profile.DisplayName} connected; {SensitiveDataRedactor.MaskIdentifier(permission.AccountId)}; available {account.AvailableBalance:N2}", sw.ElapsedMilliseconds);
                Add("trade_permission", permission.CanTrade, true, TradePermissionDetail(permission));
                Add("withdraw_permission", !permission.CanWithdraw, false, permission.CanWithdraw ? "Warning: withdraw permission is enabled" : "No withdraw permission detected");
                if(binanceClock is not null)
                {
                    Add("clock_os",true,false,$"Raw OS clock skew {binanceClock.RawOsSkewMilliseconds} ms");
                    Add("clock",binanceClock.Trusted,true,binanceClock.Trusted
                        ?$"Compensated exchange clock ready; uncertainty {binanceClock.UncertaintyMilliseconds} ms; RTT {binanceClock.MaximumRoundTripMilliseconds} ms"
                        :$"Compensated exchange clock unavailable ({binanceClock.Code})");
                }
                else Add("clock", ClockWithinTradingTolerance(health.ClockSkewMs), true, $"Exchange clock skew {health.ClockSkewMs} ms");
            }
            catch (Exception ex)
            {
                Add("exchange", false, true, $"{profile.DisplayName} connection failed: {Safe(ex.Message)}");
            }
        }


        try
        {
            var db = new AgentSqliteStore();
            await db.SetStateAsync("access-health", DateTime.UtcNow.ToString("O"), ct);
            Add("database", true, true, "Local database is writable");
        }
        catch (Exception ex)
        {
            Add("database", false, true, "Local database is unavailable: " + Safe(ex.Message));
        }

        var risk = settings.Risk.MaxRiskPerTrade > 0 && settings.Risk.MaxAccountExposure <= .60m && settings.Risk.Leverage <= 20;
        Add("risk", risk, true, risk ? "Risk Manager parameters are valid" : "Risk parameters are out of bounds");
        Add("data", ProviderReadPathAvailable(providerCanRead), true, providerCanRead ? "Provider read path is healthy" : "Provider read path is unavailable");
        Add("local_brain", true, true, "WPE Local Brain / deterministic rules ready");
        return report;
    }

    private static string Safe(string value) => SensitiveDataRedactor.ForLog(value,180);
    internal static bool ClockWithinTradingTolerance(long clockSkewMilliseconds) =>
        Math.Abs(clockSkewMilliseconds) <= BinanceFuturesAdapter.MaximumTradingClockSkewMilliseconds;
    internal static bool ProviderReadPathAvailable(bool canRead) => canRead;
    internal static string TradePermissionDetail(ExchangePermissionSnapshot permission) => permission.CanTrade
        ? "API trade permission is enabled"
        : permission.Warnings.Any(x=>x.Contains("clock skew",StringComparison.OrdinalIgnoreCase))
            ? "API trade permission is enabled, but trading is disabled by the server clock safety gate"
            : "API trade permission is missing";
}
