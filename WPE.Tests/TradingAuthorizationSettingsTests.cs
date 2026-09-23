using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using WpeAgent.TradingAuthorization;
using 币安量化机器人.Core.Models;
using 币安量化机器人.Services.Agent;

namespace WPE.Tests;

public sealed class TradingAuthorizationSettingsTests : IDisposable
{
    private static readonly DateTimeOffset Now=new(2026,7,21,4,0,0,TimeSpan.Zero);
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "wpe-authorization-settings-" + Guid.NewGuid().ToString("N"));
    private string SettingsPath => Path.Combine(_directory, "agent-settings.json");
    private string DatabasePath => Path.Combine(_directory, "agent.db");

    [Fact]
    public void MissingSettings_DefaultsToReview()
    {
        var settings = new AgentSettingsStore(SettingsPath).Load();

        Assert.Equal(TradingAuthorizationMode.Review, settings.AuthorizationMode);
    }

    [Fact]
    public void MissingSettings_FailClosedToReviewAndCannotAuthorizeMutation()
    {
        var settings = new AgentSettingsStore(SettingsPath).Load();

        var decision = TradingAuthorizationPolicy.Evaluate(RequestFor(settings.AuthorizationMode) with { ApprovalReceipt = null }, Now);

        Assert.Equal(TradingAuthorizationMode.Review, settings.AuthorizationMode);
        Assert.False(decision.Allowed);
        Assert.Equal("authorization.review-approval-required", decision.Code);
    }

    [Theory]
    [InlineData(TradingAuthorizationMode.Research)]
    [InlineData(TradingAuthorizationMode.Signal)]
    [InlineData(TradingAuthorizationMode.Review)]
    [InlineData(TradingAuthorizationMode.Auto)]
    public async Task ValidMode_RoundTripsOnlyThroughAuditedChangeBoundary(TradingAuthorizationMode mode)
    {
        var store = Store();var audit=AuditStore();var settings=store.Load();
        if(mode==TradingAuthorizationMode.Review)
        {
            var intermediate=await store.ChangeAuthorizationModeAsync(settings,TradingAuthorizationMode.Signal,true,false,"user-1","device-1","prepare review test",audit,CancellationToken.None);
            Assert.True(intermediate.Changed);
        }

        var result=await store.ChangeAuthorizationModeAsync(settings,mode,true,mode==TradingAuthorizationMode.Auto,"user-1","device-1","explicit mode change",audit,CancellationToken.None);

        Assert.True(result.Changed);
        Assert.Equal(mode, store.Load().AuthorizationMode);
    }

    [Fact]
    public void GenericSave_CannotChangeTradingAuthorizationMode()
    {
        var store=Store();var settings=store.Load();settings.AuthorizationMode=TradingAuthorizationMode.Auto;

        store.Save(settings);

        Assert.Equal(TradingAuthorizationMode.Review,settings.AuthorizationMode);Assert.Equal(TradingAuthorizationMode.Review,store.Load().AuthorizationMode);
    }

    [Theory]
    [InlineData(ExchangeEnvironment.Mainnet,true,true,"authorization.auto-testnet-required")]
    [InlineData(ExchangeEnvironment.Testnet,false,true,"authorization.auto-testnet-required")]
    [InlineData(ExchangeEnvironment.Testnet,null,true,"authorization.auto-testnet-required")]
    [InlineData(ExchangeEnvironment.Testnet,true,false,"authorization.auto-confirmation-required")]
    public async Task Auto_RejectsMainnetUnknownProfileOrMissingConfirmation(ExchangeEnvironment environment,bool? profileIsTestnet,bool confirmed,string code)
    {
        var store=Store();var audit=AuditStore();var settings=store.Load();settings.Environment=environment;

        var result=await store.ChangeAuthorizationModeAsync(settings,TradingAuthorizationMode.Auto,profileIsTestnet,confirmed,"user-1","device-1","test auto guard",audit,CancellationToken.None);

        Assert.False(result.Changed);Assert.Equal(code,result.Code);Assert.Equal(TradingAuthorizationMode.Review,settings.AuthorizationMode);Assert.Equal(TradingAuthorizationMode.Review,store.Load().AuthorizationMode);Assert.Empty(await audit.GetTradingAuthorizationModeChangesAsync(10,CancellationToken.None));
    }

    [Fact]
    public async Task Auto_RejectsUnknownEnvironmentValue()
    {
        var store=Store();var audit=AuditStore();var settings=store.Load();settings.Environment=(ExchangeEnvironment)999;

        var result=await store.ChangeAuthorizationModeAsync(settings,TradingAuthorizationMode.Auto,true,true,"user-1","device-1","unknown environment guard",audit,CancellationToken.None);

        Assert.False(result.Changed);Assert.Equal("authorization.auto-testnet-required",result.Code);Assert.Equal(TradingAuthorizationMode.Review,store.Load().AuthorizationMode);Assert.Empty(await audit.GetTradingAuthorizationModeChangesAsync(10,CancellationToken.None));
    }

    [Fact]
    public async Task SuccessfulModeChange_AuditsOnlyBoundMetadataAndRedactedReason()
    {
        var store=Store();var audit=AuditStore();var settings=store.Load();const string secret="top-secret-value";

        var result=await store.ChangeAuthorizationModeAsync(settings,TradingAuthorizationMode.Signal,true,false,"user-1","device-1",$"apiKey={secret} enable signals",audit,CancellationToken.None);
        var record=Assert.Single(await audit.GetTradingAuthorizationModeChangesAsync(10,CancellationToken.None));

        Assert.True(result.Changed);Assert.Equal(TradingAuthorizationMode.Review,record.OldMode);Assert.Equal(TradingAuthorizationMode.Signal,record.NewMode);Assert.Equal("user-1",record.UserId);Assert.Equal("device-1",record.DeviceId);Assert.Equal(Now,record.ChangedAtUtc);Assert.Contains("[REDACTED]",record.Reason,StringComparison.Ordinal);Assert.DoesNotContain(secret,record.Reason,StringComparison.Ordinal);Assert.Equal(TradingAuthorizationMode.Signal,store.Load().AuthorizationMode);
    }

    [Fact]
    public async Task BlankReason_LeavesSettingsAndAuditUnchanged()
    {
        var store=Store();var audit=AuditStore();var settings=store.Load();

        var result=await store.ChangeAuthorizationModeAsync(settings,TradingAuthorizationMode.Research,true,false,"user-1","device-1"," ",audit,CancellationToken.None);

        Assert.False(result.Changed);Assert.Equal("authorization.mode-context-invalid",result.Code);Assert.Equal(TradingAuthorizationMode.Review,store.Load().AuthorizationMode);Assert.Empty(await audit.GetTradingAuthorizationModeChangesAsync(10,CancellationToken.None));
    }

    [Theory]
    [InlineData("Unknown")]
    [InlineData("999")]
    [InlineData("{}")]
    public void InvalidMode_FallsBackToReviewAndNormalizesAiMode(string value)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(SettingsPath, $$"""
            { "AuthorizationMode": {{JsonSerializer.Serialize(value == "{}" ? new object() : value)}}, "AiMode": 2 }
            """, Encoding.UTF8);

        var settings = new AgentSettingsStore(SettingsPath).Load();

        Assert.Equal(TradingAuthorizationMode.Review, settings.AuthorizationMode);
        Assert.Equal(AiRuntimeMode.LocalOnly, settings.AiMode);
    }

    [Theory]
    [InlineData("999")]
    [InlineData("null")]
    [InlineData("[]")]
    public void InvalidJsonValue_FallsBackToReviewAndNormalizesAiMode(string rawValue)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(SettingsPath, $"{{\"AuthorizationMode\":{rawValue},\"AiMode\":1}}", Encoding.UTF8);

        var settings = new AgentSettingsStore(SettingsPath).Load();

        Assert.Equal(TradingAuthorizationMode.Review, settings.AuthorizationMode);
        Assert.Equal(AiRuntimeMode.LocalOnly, settings.AiMode);
    }

    [Fact]
    public void LegacySettingsWithoutAuthorizationMode_DefaultsToReviewAndLocalOnly()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(SettingsPath, "{\"AiMode\":1}", Encoding.UTF8);

        var settings = new AgentSettingsStore(SettingsPath).Load();

        Assert.Equal(TradingAuthorizationMode.Review, settings.AuthorizationMode);
        Assert.Equal(AiRuntimeMode.LocalOnly, settings.AiMode);
    }

    [Fact]
    public void CorruptSettings_DefaultsToReviewWithSafeDiagnostic()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(SettingsPath, "not-json", Encoding.UTF8);
        var store = new AgentSettingsStore(SettingsPath);

        var settings = store.Load();

        Assert.Equal(TradingAuthorizationMode.Review, settings.AuthorizationMode);
        Assert.NotNull(store.LastLoadDiagnostic);
        Assert.DoesNotContain("not-json", store.LastLoadDiagnostic!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void CorruptSettings_FailClosedToReviewAndCannotAuthorizeMutation()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(SettingsPath, "not-json", Encoding.UTF8);
        var store = new AgentSettingsStore(SettingsPath);

        var settings = store.Load();
        var decision = TradingAuthorizationPolicy.Evaluate(RequestFor(settings.AuthorizationMode) with { ApprovalReceipt = null }, Now);

        Assert.Equal(TradingAuthorizationMode.Review, settings.AuthorizationMode);
        Assert.False(decision.Allowed);
        Assert.Equal("authorization.review-approval-required", decision.Code);
        Assert.NotNull(store.LastLoadDiagnostic);
    }

    [Theory]
    [InlineData(AiRuntimeMode.LocalOnly)]
    [InlineData(AiRuntimeMode.Hybrid)]
    [InlineData(AiRuntimeMode.AIResearch)]
    public void LegacyAiMode_IsNormalizedWithoutChangingAuthorizationMode(AiRuntimeMode aiMode)
    {
        var store = new AgentSettingsStore(SettingsPath);
        store.Save(new AgentSettings { AiMode = aiMode, AuthorizationMode = TradingAuthorizationMode.Review });

        var settings = store.Load();

        Assert.Equal(AiRuntimeMode.LocalOnly, settings.AiMode);
        Assert.Equal(TradingAuthorizationMode.Review, settings.AuthorizationMode);
    }

    [Fact]
    public async Task InitialAutoSetup_CorruptSettingsRemainBlocked()
    {
        Directory.CreateDirectory(_directory);File.WriteAllText(SettingsPath,"{ invalid",Encoding.UTF8);var store=Store();var settings=store.Load();
        var result=await store.ChangeAuthorizationModeAfterReadinessAsync(settings,"user","device","initial setup",AuditStore(),CancellationToken.None);
        Assert.False(result.Changed);Assert.Equal("authorization.auto-legacy-or-corrupt-blocked",result.Code);Assert.Equal(TradingAuthorizationMode.Review,settings.AuthorizationMode);
    }

    [Fact]
    public async Task InitialAutoSetup_CompletedLegacyReviewRemainsBlocked()
    {
        var store=Store();var settings=store.Load();settings.SetupCompleted=true;store.Save(settings);
        var result=await store.ChangeAuthorizationModeAfterReadinessAsync(settings,"user","device","initial setup",AuditStore(),CancellationToken.None);
        Assert.False(result.Changed);Assert.Equal("authorization.auto-legacy-or-corrupt-blocked",result.Code);Assert.Equal(TradingAuthorizationMode.Review,store.Load().AuthorizationMode);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
    private AgentSettingsStore Store()=>new(SettingsPath,()=>Now);
    private AgentSqliteStore AuditStore()=>new(DatabasePath,()=>Now);
    private static TradingAuthorizationRequest RequestFor(TradingAuthorizationMode mode)
    {
        const string correlation = "correlation-1";
        const string hash = "intent-hash-1";
        const string user = "user-1";
        const string device = "device-1";
        const string session = "session-1";
        var risk = new DeterministicRiskReceipt("risk-1", correlation, hash, true, Now.AddMinutes(-1), Now.AddMinutes(5));
        var approval = new TradingApprovalReceipt("approval-1", correlation, hash, user, device, session, true, Now.AddMinutes(-1), Now.AddMinutes(5));
        return new(mode, true, correlation, hash, user, device, session, risk, approval);
    }
}
