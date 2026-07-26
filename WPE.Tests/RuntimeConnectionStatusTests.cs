using System.Text.Json;
using WpeAgent.RuntimeContracts;
using WpeAgent.RuntimeServices;
using 币安量化机器人.Core.Models;
using 币安量化机器人.Services.Access;
using 币安量化机器人.Services.Agent;
using 币安量化机器人.Services.Exchange;

namespace WPE.Tests;

public sealed class RuntimeConnectionStatusTests
{
    [Fact]
    public void ReadinessProjection_ContainsOnlyNonSensitiveConnectionMetadata()
    {
        const string cipher="encrypted-api-key-secret";const string account="private-account-id";
        var profile=new ExchangeConnectionProfile{Id="exec-testnet",ProviderId="arbitrary-provider",DisplayName="Arbitrary Testnet",IsTestnet=true,EncryptedCredentials=new(){{"apiKey",cipher}}};
        var settings=new AgentSettings{ActiveExecutionConnectionId=profile.Id,Exchanges=[profile]};
        var report=Report(DateTime.UtcNow,("environment",true),("provider",true),("credentials",true),("exchange",true),("trade_permission",true),("withdraw_permission",true));
        report.Checks.Add(new("sensitive-detail",true,false,$"{account} {cipher}"));
        var store=new RuntimeConnectionStateStore();store.Publish(report,settings);
        var snapshot=RuntimeSnapshotFactory.Create(new SystemState{LastUpdated=DateTime.UtcNow},DateTime.UtcNow,connectionState:store.Read());

        var value=Assert.IsType<RuntimeConnectionStatusV1>(snapshot.ConnectionStatus.Value);
        Assert.Equal(RuntimeCollectionState.Available,snapshot.ConnectionStatus.State);
        Assert.Equal("configured",value.CredentialStatus);Assert.Equal("ready",value.AdapterStatus);
        Assert.Equal("Testnet",value.Environment);Assert.True(value.Ready);Assert.False(value.WithdrawPermission);
        var json=JsonSerializer.Serialize(snapshot);
        Assert.DoesNotContain(cipher,json,StringComparison.Ordinal);Assert.DoesNotContain(account,json,StringComparison.Ordinal);
    }

    [Fact]
    public void MissingCredentialAndWithdrawalWarning_AreExplicitWithoutHints()
    {
        var profile=new ExchangeConnectionProfile{Id="exec",ProviderId="provider",DisplayName="Provider",IsTestnet=true};
        var settings=new AgentSettings{ActiveExecutionConnectionId=profile.Id,Exchanges=[profile]};
        var report=Report(DateTime.UtcNow,("environment",true),("provider",true),("credentials",false),("exchange",false),("trade_permission",false),("withdraw_permission",false));
        var store=new RuntimeConnectionStateStore();store.Publish(report,settings);var value=Assert.IsType<RuntimeConnectionStatusV1>(store.Read().Value);
        Assert.Equal("missing",value.CredentialStatus);Assert.True(value.WithdrawPermission);Assert.NotNull(value.WithdrawalWarning);Assert.False(value.Ready);
    }

    [Fact]
    public void UnsupportedStaleAndErrorStates_DoNotExposeConnectionValue()
    {
        var now=DateTime.UtcNow;var unsupported=RuntimeSnapshotFactory.Create(new SystemState{LastUpdated=now},now);
        Assert.Equal(RuntimeCollectionState.Unsupported,unsupported.ConnectionStatus.State);Assert.Null(unsupported.ConnectionStatus.Value);
        var item=new RuntimeConnectionStatusV1("id","name","provider","Testnet","configured","ready",now,true,true,true,false,null);
        var staleState=new RuntimeConnectionState(RuntimeCollectionState.Available,item,new DateTimeOffset(now-RuntimeConnectionStateStore.StaleAfter-TimeSpan.FromSeconds(1)),null);
        var stale=RuntimeSnapshotFactory.Create(new SystemState{LastUpdated=now},now,connectionState:staleState);
        Assert.Equal(RuntimeCollectionState.Stale,stale.ConnectionStatus.State);Assert.Null(stale.ConnectionStatus.Value);
        var error=RuntimeSnapshotFactory.Create(new SystemState{LastUpdated=now},now,connectionState:RuntimeConnectionState.Error("check failed"));
        Assert.Equal(RuntimeCollectionState.Error,error.ConnectionStatus.State);Assert.Null(error.ConnectionStatus.Value);
    }

    private static AccessReadinessReport Report(DateTime time,params (string Key,bool Passed)[] checks)
    {
        var report=new AccessReadinessReport{CheckedAtUtc=time};foreach(var check in checks)report.Checks.Add(new(check.Key,check.Passed,true,check.Key));return report;
    }
}
