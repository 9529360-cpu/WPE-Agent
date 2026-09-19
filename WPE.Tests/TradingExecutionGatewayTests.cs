using Microsoft.Data.Sqlite;
using WpeAgent.TradingAuthorization;
using 币安量化机器人.Core.Models;
using 币安量化机器人.Services;
using 币安量化机器人.Services.Agent;

namespace WPE.Tests;

public sealed class TradingExecutionGatewayTests:IDisposable
{
    private static readonly DateTimeOffset Now=new(2026,7,21,2,0,0,TimeSpan.Zero);
    private readonly string _directory=Path.Combine(Path.GetTempPath(),"wpe-execution-gateway-"+Guid.NewGuid().ToString("N"));
    private readonly AgentStatus _originalStatus=ServiceLocator.SystemState.Status;
    private string DatabasePath=>Path.Combine(_directory,"agent.db");

    [Theory]
    [InlineData(TradingAuthorizationMode.Research)]
    [InlineData(TradingAuthorizationMode.Signal)]
    public async Task ReadOnlyModes_RejectBeforeReliableExecutorMutation(TradingAuthorizationMode mode)
    {
        var setup=Setup(mode);

        var result=await setup.Gateway.ExecutePlanAsync(Command(mode),CancellationToken.None);

        Assert.False(result.Executed);Assert.Equal(0,setup.Exchange.MutationCount);
    }

    [Fact]
    public async Task Auto_MainnetRejectsBeforeReliableExecutorMutation()
    {
        var setup=Setup(TradingAuthorizationMode.Auto,ExchangeEnvironment.Mainnet);

        var result=await setup.Gateway.ExecutePlanAsync(Command(TradingAuthorizationMode.Auto) with{Authorization=Authorization(TradingAuthorizationMode.Auto) with{IsTestnet=true}},CancellationToken.None);

        Assert.False(result.Executed);Assert.Equal("authorization.testnet-required",result.Code);Assert.Equal(0,setup.Exchange.MutationCount);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("expired")]
    [InlineData("revoked")]
    [InlineData("mismatch")]
    public async Task Auto_InvalidRiskReceiptRejectsBeforeReliableExecutorMutation(string state)
    {
        var setup=Setup(TradingAuthorizationMode.Auto);var authorization=Authorization(TradingAuthorizationMode.Auto);var risk=authorization.RiskReceipt!;
        risk=state switch{"expired"=>risk with{ExpiresAtUtc=Now},"revoked"=>risk with{RevokedAtUtc=Now.AddMinutes(-1)},"mismatch"=>risk with{IntentHash="different"},_=>risk};
        authorization=authorization with{RiskReceipt=state=="missing"?null:risk};

        var result=await setup.Gateway.ExecutePlanAsync(Command(TradingAuthorizationMode.Auto) with{Authorization=authorization},CancellationToken.None);

        Assert.False(result.Executed);Assert.Equal(0,setup.Exchange.MutationCount);
    }

    [Fact]
    public async Task Auto_ValidTestnetRiskReceiptUsesReliableExecutor()
    {
        var setup=Setup(TradingAuthorizationMode.Auto);

        var result=await setup.Gateway.ExecutePlanAsync(Command(TradingAuthorizationMode.Auto),CancellationToken.None);

        Assert.True(result.Executed);Assert.True(setup.Exchange.MutationCount>0);Assert.Equal("correlation-1",await ExecutionCorrelation());
    }

    [Fact]
    public async Task AutomaticOrderObserverConfirmsMatchingExchangeOrderWithoutMutation()
    {
        var setup=Setup(TradingAuthorizationMode.Auto);var artifact=AutomaticArtifact();
        setup.Exchange.ObservedOrder=new("BTCUSDT","order-observed","client-1","FILLED",.001m,50_000m,"MARKET",PositionSide.Long,false,Now.UtcDateTime);
        var before=setup.Exchange.MutationCount;

        var evidence=await new TradingAutomaticExecutionGateway(setup.Gateway,setup.Exchange,setup.Store)
            .ObserveOrdersAsync(artifact,CancellationToken.None);

        Assert.Equal(ModelOffExchangeOrderEvidenceStateV1.Confirmed,evidence.State);
        Assert.Equal(1,evidence.ExpectedCount);Assert.Equal(1,evidence.FoundCount);
        Assert.Equal(before,setup.Exchange.MutationCount);
    }

    [Fact]
    public async Task AutomaticOrderObserverReportsMissingWithoutMutation()
    {
        var setup=Setup(TradingAuthorizationMode.Auto);var before=setup.Exchange.MutationCount;

        var evidence=await new TradingAutomaticExecutionGateway(setup.Gateway,setup.Exchange,setup.Store)
            .ObserveOrdersAsync(AutomaticArtifact(),CancellationToken.None);

        Assert.Equal(ModelOffExchangeOrderEvidenceStateV1.Missing,evidence.State);
        Assert.Equal(0,evidence.FoundCount);Assert.Equal(before,setup.Exchange.MutationCount);
    }

    [Fact]
    public async Task Review_ConsumesPersistedApprovalBeforeCallingReliableExecutor()
    {
        var setup=Setup(TradingAuthorizationMode.Review);await PersistApproval(setup.Store);

        var result=await setup.Gateway.ExecutePlanAsync(Command(TradingAuthorizationMode.Review),CancellationToken.None);

        Assert.True(result.Executed);Assert.True(setup.Exchange.MutationCount>0);Assert.Equal("correlation-1",await ExecutionCorrelation());
        Assert.Equal(Now,(await setup.Store.GetTradingApprovalRequestAsync("request-1",CancellationToken.None))?.ConsumedAtUtc);
        Assert.Equal(Now,(await setup.Store.GetTradingApprovalReceiptAsync("receipt-1",CancellationToken.None))?.Receipt.ConsumedAtUtc);
    }

    [Fact]
    public async Task Review_UnpersistedApprovalRejectsWithoutMutation()
    {
        var setup=Setup(TradingAuthorizationMode.Review);

        var result=await setup.Gateway.ExecutePlanAsync(Command(TradingAuthorizationMode.Review),CancellationToken.None);

        Assert.False(result.Executed);Assert.Equal("approval.not-consumable",result.Code);Assert.Equal(0,setup.Exchange.MutationCount);
    }

    [Fact]
    public async Task Review_ReplayRejectsWithoutAdditionalMutation()
    {
        var setup=Setup(TradingAuthorizationMode.Review);await PersistApproval(setup.Store);var command=Command(TradingAuthorizationMode.Review);
        var first=await setup.Gateway.ExecutePlanAsync(command,CancellationToken.None);var mutations=setup.Exchange.MutationCount;

        var replay=await setup.Gateway.ExecutePlanAsync(command,CancellationToken.None);

        Assert.True(first.Executed);Assert.False(replay.Executed);Assert.Equal(mutations,setup.Exchange.MutationCount);
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("revoked")]
    [InlineData("correlation")]
    [InlineData("hash")]
    [InlineData("user")]
    [InlineData("device")]
    [InlineData("session")]
    public async Task Review_InvalidApprovalRejectsWithoutMutationOrConsumption(string state)
    {
        var setup=Setup(TradingAuthorizationMode.Review);var request=Request();var receipt=Receipt();
        if(state=="expired"){request=request with{ExpiresAtUtc=Now};receipt=receipt with{ExpiresAtUtc=Now};}
        else if(state=="revoked")receipt=receipt with{RevokedAtUtc=Now.AddMinutes(-1)};
        Assert.True((await setup.Store.SaveTradingApprovalRequestAsync(request,CancellationToken.None)).Succeeded);
        Assert.True((await setup.Store.SaveTradingApprovalReceiptAsync(request.RequestId,receipt,CancellationToken.None)).Succeeded);
        var authorization=Authorization(TradingAuthorizationMode.Review);authorization=state switch{"correlation"=>authorization with{CorrelationId="different"},"hash"=>authorization with{IntentHash="different"},"user"=>authorization with{UserId="different"},"device"=>authorization with{DeviceId="different"},"session"=>authorization with{SessionId="different"},_=>authorization};

        var result=await setup.Gateway.ExecutePlanAsync(Command(TradingAuthorizationMode.Review) with{Authorization=authorization},CancellationToken.None);

        Assert.False(result.Executed);Assert.Equal(0,setup.Exchange.MutationCount);
        Assert.Null((await setup.Store.GetTradingApprovalRequestAsync(request.RequestId,CancellationToken.None))?.ConsumedAtUtc);
        Assert.Null((await setup.Store.GetTradingApprovalReceiptAsync(receipt.ReceiptId,CancellationToken.None))?.Receipt.ConsumedAtUtc);
    }

    [Fact]
    public async Task Review_ChangedExecutionPayloadRejectsBeforeApprovalConsumptionOrMutation()
    {
        var setup=Setup(TradingAuthorizationMode.Review);await PersistApproval(setup.Store);var command=Command(TradingAuthorizationMode.Review);var changed=command.Intents[0] with{Quantity=.002m};

        var result=await setup.Gateway.ExecutePlanAsync(command with{Intents=[changed]},CancellationToken.None);

        Assert.False(result.Executed);Assert.Equal("execution.intent-hash-mismatch",result.Code);Assert.Equal(0,setup.Exchange.MutationCount);
        Assert.Null((await setup.Store.GetTradingApprovalRequestAsync("request-1",CancellationToken.None))?.ConsumedAtUtc);
    }

    [Fact]
    public async Task FailureDiagnostic_DoesNotEchoAuthorizationIdentifiers()
    {
        var setup=Setup(TradingAuthorizationMode.Review);var authorization=Authorization(TradingAuthorizationMode.Review) with{IntentHash="private-hash",ApprovalReceipt=null};

        var result=await setup.Gateway.ExecutePlanAsync(Command(TradingAuthorizationMode.Review) with{ApprovalRequestId="private-request",Authorization=authorization},CancellationToken.None);

        Assert.False(result.Executed);Assert.DoesNotContain("private",result.Code,StringComparison.Ordinal);Assert.Equal(0,setup.Exchange.MutationCount);
    }

    [Fact]
    public void ReliableOrderExecutor_ImplementsSingleMutationBoundary()
    {
        var setup=Setup(TradingAuthorizationMode.Auto);

        Assert.IsAssignableFrom<ITradingMutationExecutor>(setup.Executor);
    }

    [Fact]
    public void ProductionCompositionRoot_WiresTradingExecutionGatewayToReliableOrderExecutor()
    {
        var root=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..","..","..",".."));
        var source=File.ReadAllText(Path.Combine(root,"Services","AutoTradingAgent.cs"));

        Assert.Contains("var executor=new ReliableOrderExecutor(",source,StringComparison.Ordinal);
        Assert.Contains("capabilitySnapshot,",source,StringComparison.Ordinal);
        Assert.Contains("capabilityRefresh",source,StringComparison.Ordinal);
        Assert.Contains("ProviderCapabilityProbe",source,StringComparison.Ordinal);
        Assert.Contains("var recoveryServices=await ProductionRecoveryComposition.CreateAsync(exchange,executor,Db",source,StringComparison.Ordinal);
        Assert.Contains("var executionGateway=recoveryServices.Gateway;",source,StringComparison.Ordinal);
        Assert.Contains("ExecutePositionManagementRecoveryAsync(recoveryServices.Recovery",source,StringComparison.Ordinal);
        Assert.DoesNotContain("var executionGateway=new TradingExecutionGateway(executor,Db);",source,StringComparison.Ordinal);
        Assert.DoesNotContain("new TradingExecutionGateway(new ",source,StringComparison.Ordinal);
        Assert.DoesNotContain("ITradingMutationExecutor executor",source,StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(AutomaticMutationPath.StartupRecovery,1,"authorization.recovery-approval-chain-required")]
    [InlineData(AutomaticMutationPath.ProtectionAudit,1,"authorization.protection-approval-chain-required")]
    [InlineData(AutomaticMutationPath.PositionManagement,2,"authorization.position-management-approval-required")]
    [InlineData(AutomaticMutationPath.AutoMainPlan,1,"authorization.auto-risk-receipt-unavailable")]
    public void UnverifiedAutomaticPaths_FailClosedWithoutMutation(AutomaticMutationPath path,int count,string code)
    {
        var setup=Setup(TradingAuthorizationMode.Auto);

        var result=setup.Gateway.AssessUnverifiedAutomaticMutation(path,count);

        Assert.False(result.SafeToIncreaseRisk);Assert.Equal(code,result.Code);Assert.Equal(0,setup.Exchange.MutationCount);
    }

    [Theory]
    [InlineData(AutomaticMutationPath.StartupRecovery)]
    [InlineData(AutomaticMutationPath.ProtectionAudit)]
    public void EmptyMaintenancePath_RequiresNoMutation(AutomaticMutationPath path)
    {
        var setup=Setup(TradingAuthorizationMode.Review);

        var result=setup.Gateway.AssessUnverifiedAutomaticMutation(path,0);

        Assert.True(result.SafeToIncreaseRisk);Assert.Equal(0,setup.Exchange.MutationCount);
    }

    [Fact]
    public void ReviewRequest_BindsUserDeviceSessionCorrelationAndIntentHash()
    {
        var request=TradingExecutionGateway.CreateReviewRequest("correlation",IntentHash(),"user","device","session",Now,TimeSpan.FromMinutes(5));

        Assert.Equal(TradingAuthorizationMode.Review,request.Mode);Assert.Equal("correlation",request.CorrelationId);Assert.Equal(IntentHash(),request.IntentHash);Assert.Equal("user",request.UserId);Assert.Equal("device",request.DeviceId);Assert.Equal("session",request.SessionId);Assert.Equal(Now.AddMinutes(5),request.ExpiresAtUtc);
    }

    [Fact]
    public async Task EmergencyReduction_ConfirmedTestnetClosePersistsConsumesAndExecutes()
    {
        var setup=Setup(TradingAuthorizationMode.Review);

        var result=await setup.Gateway.ExecuteEmergencyReductionAsync(EmergencyCommand(),CancellationToken.None);

        Assert.True(result.Executed);Assert.True(setup.Exchange.MutationCount>0);
        Assert.Equal(1,await ApprovalCount("trading_approval_requests","consumed_at IS NOT NULL"));
        Assert.Equal(1,await ApprovalCount("trading_approval_receipts","consumed_at IS NOT NULL"));
        Assert.Equal("emergency-correlation",await ExecutionCorrelation());
    }

    [Fact]
    public async Task EmergencyReduction_ReplayDoesNotMutateAgain()
    {
        var setup=Setup(TradingAuthorizationMode.Review);var command=EmergencyCommand();
        var first=await setup.Gateway.ExecuteEmergencyReductionAsync(command,CancellationToken.None);var mutations=setup.Exchange.MutationCount;

        var replay=await setup.Gateway.ExecuteEmergencyReductionAsync(command,CancellationToken.None);

        Assert.True(first.Executed);Assert.False(replay.Executed);Assert.Equal("approval.request-not-persisted",replay.Code);Assert.Equal(mutations,setup.Exchange.MutationCount);
    }

    [Fact]
    public async Task EmergencyReduction_MainnetFailsBeforeApprovalOrMutation()
    {
        var setup=Setup(TradingAuthorizationMode.Review,ExchangeEnvironment.Mainnet);

        var result=await setup.Gateway.ExecuteEmergencyReductionAsync(EmergencyCommand(),CancellationToken.None);

        Assert.False(result.Executed);Assert.Equal("authorization.testnet-required",result.Code);Assert.Equal(0,setup.Exchange.MutationCount);Assert.Equal(0,await ApprovalCount("trading_approval_requests","1=1"));
    }

    [Fact]
    public async Task EmergencyReduction_ExpiredConfirmationFailsBeforeApprovalOrMutation()
    {
        var setup=Setup(TradingAuthorizationMode.Review);var command=EmergencyCommand() with{Confirmation=Confirmation(Now.AddMinutes(-2))};

        var result=await setup.Gateway.ExecuteEmergencyReductionAsync(command,CancellationToken.None);

        Assert.False(result.Executed);Assert.Equal("emergency.confirmation-expired",result.Code);Assert.Equal(0,setup.Exchange.MutationCount);Assert.Equal(0,await ApprovalCount("trading_approval_requests","1=1"));
    }

    [Theory]
    [InlineData("opening")]
    [InlineData("oversize")]
    [InlineData("wrong-side")]
    [InlineData("limit")]
    public async Task EmergencyReduction_InvalidReductionFailsBeforeApprovalOrMutation(string mutation)
    {
        var setup=Setup(TradingAuthorizationMode.Review);var command=EmergencyCommand();var intent=command.Intent;
        intent=mutation switch
        {
            "opening"=>intent with{ReduceOnly=false,Action=DecisionAction.OpenLong},
            "oversize"=>intent with{Quantity=.011m},
            "wrong-side"=>intent with{Side=PositionSide.Short,Action=DecisionAction.CloseShort},
            "limit"=>intent with{OrderType=ExecutionOrderType.Limit,LimitPrice=49_000m},
            _=>intent
        };

        var result=await setup.Gateway.ExecuteEmergencyReductionAsync(command with{Intent=intent},CancellationToken.None);

        Assert.False(result.Executed);Assert.Equal("emergency.reduction-invalid",result.Code);Assert.Equal(0,setup.Exchange.MutationCount);Assert.Equal(0,await ApprovalCount("trading_approval_requests","1=1"));
    }

    [Fact]
    public async Task TestnetSmoke_ExplicitAuthorizationBindsOpeningAndCleanupToOneCorrelation()
    {
        var setup=Setup(TradingAuthorizationMode.Review);var authorization=SmokeAuthorization();

        var opening=await setup.Gateway.ExecuteTestnetSmokeAsync(SmokeOpeningCommand(authorization),CancellationToken.None);
        var cleanup=await setup.Gateway.ExecuteTestnetSmokeAsync(SmokeCleanupCommand(authorization),CancellationToken.None);

        Assert.True(opening.Executed);Assert.True(cleanup.Executed);Assert.True(setup.Exchange.MutationCount>0);
        Assert.Equal(2,await ApprovalCount("trading_approval_requests","consumed_at IS NOT NULL"));
        Assert.Equal(2,await ApprovalCount("trading_approval_receipts","consumed_at IS NOT NULL"));
        Assert.Equal("smoke-correlation",await ExecutionCorrelation());
    }

    [Fact]
    public async Task TestnetSmoke_ReduceOnlyCleanupDoesNotReconfigureAccountMode()
    {
        var setup=Setup(TradingAuthorizationMode.Review);

        var cleanup=await setup.Gateway.ExecuteTestnetSmokeAsync(SmokeCleanupCommand(SmokeAuthorization()),CancellationToken.None);

        Assert.True(cleanup.Executed);
        Assert.Equal(0,setup.Exchange.AccountModeMutationCount);
        Assert.True(setup.Exchange.MutationCount>0);
    }

    [Fact]
    public async Task TestnetSmoke_MissingAuthorizationFailsBeforeApprovalOrMutation()
    {
        var setup=Setup(TradingAuthorizationMode.Review);

        var result=await setup.Gateway.ExecuteTestnetSmokeAsync(SmokeOpeningCommand(null),CancellationToken.None);

        Assert.False(result.Executed);Assert.Equal("smoke.authorization-required",result.Code);Assert.Equal(0,setup.Exchange.MutationCount);Assert.Equal(0,await ApprovalCount("trading_approval_requests","1=1"));
    }

    [Fact]
    public async Task TestnetSmoke_ExpiredAuthorizationFailsBeforeApprovalOrMutation()
    {
        var setup=Setup(TradingAuthorizationMode.Review);var expired=SmokeAuthorization() with{AuthorizedAtUtc=Now.AddMinutes(-11),ExpiresAtUtc=Now.AddMinutes(-1)};

        var result=await setup.Gateway.ExecuteTestnetSmokeAsync(SmokeOpeningCommand(expired),CancellationToken.None);

        Assert.False(result.Executed);Assert.Equal("smoke.authorization-expired",result.Code);Assert.Equal(0,setup.Exchange.MutationCount);Assert.Equal(0,await ApprovalCount("trading_approval_requests","1=1"));
    }

    [Fact]
    public async Task TestnetSmoke_MainnetFailsBeforeApprovalOrMutation()
    {
        var setup=Setup(TradingAuthorizationMode.Review,ExchangeEnvironment.Mainnet);

        var result=await setup.Gateway.ExecuteTestnetSmokeAsync(SmokeOpeningCommand(SmokeAuthorization()),CancellationToken.None);

        Assert.False(result.Executed);Assert.Equal("authorization.testnet-required",result.Code);Assert.Equal(0,setup.Exchange.MutationCount);Assert.Equal(0,await ApprovalCount("trading_approval_requests","1=1"));
    }

    [Fact]
    public async Task TestnetSmoke_ReplayedAuthorizationAndIntentDoesNotMutateAgain()
    {
        var setup=Setup(TradingAuthorizationMode.Review);var command=SmokeOpeningCommand(SmokeAuthorization());
        var first=await setup.Gateway.ExecuteTestnetSmokeAsync(command,CancellationToken.None);var mutations=setup.Exchange.MutationCount;

        var replay=await setup.Gateway.ExecuteTestnetSmokeAsync(command,CancellationToken.None);

        Assert.True(first.Executed);Assert.False(replay.Executed);Assert.Equal("approval.request-not-persisted",replay.Code);Assert.Equal(mutations,setup.Exchange.MutationCount);
    }

    [Fact]
    public async Task TestnetSmoke_NonMinimalOpeningFailsBeforeApprovalOrMutation()
    {
        var setup=Setup(TradingAuthorizationMode.Review);var command=SmokeOpeningCommand(SmokeAuthorization());

        var result=await setup.Gateway.ExecuteTestnetSmokeAsync(command with{Intent=command.Intent with{Quantity=.002m}},CancellationToken.None);

        Assert.False(result.Executed);Assert.Equal("smoke.intent-invalid",result.Code);Assert.Equal(0,setup.Exchange.MutationCount);Assert.Equal(0,await ApprovalCount("trading_approval_requests","1=1"));
    }

    private SetupResult Setup(TradingAuthorizationMode mode,ExchangeEnvironment environment=ExchangeEnvironment.Testnet)
    {
        ServiceLocator.SystemState.Status=AgentStatus.Running;var store=new AgentSqliteStore(DatabasePath,()=>Now);var exchange=new RecordingExchange(environment);var executor=new ReliableOrderExecutor(exchange,store,new RiskLimits(),SystemOrderPollScheduler.Instance);var gateway=new TradingExecutionGateway(executor,store,()=>Now);return new(store,exchange,executor,gateway);
    }
    private static async Task PersistApproval(AgentSqliteStore store){Assert.True((await store.SaveTradingApprovalRequestAsync(Request(),CancellationToken.None)).Succeeded);Assert.True((await store.SaveTradingApprovalReceiptAsync("request-1",Receipt(),CancellationToken.None)).Succeeded);}
    private static TradingExecutionCommand Command(TradingAuthorizationMode mode)=>new(mode==TradingAuthorizationMode.Review?"request-1":null,Authorization(mode),[Intent()],5,true);
    private static TradingAuthorizationRequest Authorization(TradingAuthorizationMode mode)=>new(mode,true,"correlation-1",IntentHash(),"user-1","device-1","session-1",Risk(),mode==TradingAuthorizationMode.Review?Receipt():null);
    private static DeterministicRiskReceipt Risk()=>new("risk-1","correlation-1",IntentHash(),true,Now.AddMinutes(-5),Now.AddMinutes(5));
    private static TradingApprovalRequest Request()=>new("request-1",TradingAuthorizationMode.Review,"correlation-1",IntentHash(),"user-1","device-1","session-1",Now.AddMinutes(-5),Now.AddMinutes(5));
    private static TradingApprovalReceipt Receipt()=>new("receipt-1","correlation-1",IntentHash(),"user-1","device-1","session-1",true,Now.AddMinutes(-4),Now.AddMinutes(4));
    private static ExecutionIntent Intent()=>new("BTCUSDT",PositionSide.Long,.001m,false,49_000m,51_000m,"client-1","gateway test",DecisionAction.OpenLong,ExpectedPrice:50_000m);
    private static DurableExecutionArtifactV2 AutomaticArtifact()=>new(2,"correlation-1",
        [new(0,"BTCUSDT","Long",.001m,false,49_000m,51_000m,"client-1","strategy.entry","OpenLong","Market",0,50_000m)],
        5,true,"binance","Testnet","strategy","v1",Now.AddSeconds(-20),"market-v1",Now.AddSeconds(-10),Now.AddMinutes(2));
    private static string IntentHash()=>TradingExecutionGateway.ComputeIntentHash([Intent()],5,true);
    private static ManualEmergencyConfirmation Confirmation(DateTimeOffset? confirmedAt=null)=>new("confirmation-1","emergency-correlation","user-1","device-1","session-1",confirmedAt??Now.AddMinutes(-1));
    private static EmergencyReductionCommand EmergencyCommand()=>new(Confirmation(),new("BTCUSDT",PositionSide.Long,.01m,49_000m,50_000m,10m,5m,true,20_000m),new("BTCUSDT",PositionSide.Long,.01m,true,0,0,"emergency-client-1","manual emergency close",DecisionAction.CloseLong,ExpectedPrice:50_000m),5,true);
    private static TestnetSmokeAuthorization SmokeAuthorization()=>new("smoke-authorization-1","smoke-correlation","user-1","device-1","smoke-session-1",Now.AddMinutes(-1),Now.AddMinutes(9));
    private static TestnetSmokeCommand SmokeOpeningCommand(TestnetSmokeAuthorization? authorization){var rule=new TradingRule("BTCUSDT",.001m,.1m,.001m,5m,20);var market=new MarketEvidence("BTCUSDT",50_000m,49_000m,51_000m,50,0,0,0,new(0,1,1,1,1,1,0),Now.UtcDateTime);var intent=new ExecutionIntent("BTCUSDT",PositionSide.Long,.001m,false,49_000m,51_000m,"smoke-open-1","testnet smoke",DecisionAction.OpenLong,ExpectedPrice:50_000m);return new(authorization,intent,10,true,rule,market);}
    private static TestnetSmokeCommand SmokeCleanupCommand(TestnetSmokeAuthorization authorization){var position=new ManagedPosition("BTCUSDT",PositionSide.Long,.001m,50_000m,50_000m,0,10,true,20_000m);var intent=new ExecutionIntent("BTCUSDT",PositionSide.Long,.001m,true,0,0,"smoke-close-1","testnet smoke cleanup",DecisionAction.CloseLong,ExpectedPrice:50_000m);return new(authorization,intent,10,true,ObservedPosition:position);}
    private async Task<string?> ExecutionCorrelation(){await using var connection=new SqliteConnection($"Data Source={DatabasePath}");await connection.OpenAsync();await using var command=connection.CreateCommand();command.CommandText="SELECT cycle_id FROM execution_events ORDER BY id DESC LIMIT 1";return (await command.ExecuteScalarAsync())?.ToString();}
    private async Task<int> ApprovalCount(string table,string condition){await using var connection=new SqliteConnection($"Data Source={DatabasePath}");await connection.OpenAsync();await using var command=connection.CreateCommand();command.CommandText=$"SELECT COUNT(*) FROM {table} WHERE {condition}";return Convert.ToInt32(await command.ExecuteScalarAsync());}
    public void Dispose(){ServiceLocator.SystemState.Status=_originalStatus;SqliteConnection.ClearAllPools();if(Directory.Exists(_directory))Directory.Delete(_directory,true);}
    private sealed record SetupResult(AgentSqliteStore Store,RecordingExchange Exchange,ReliableOrderExecutor Executor,TradingExecutionGateway Gateway);

    private sealed class RecordingExchange(ExchangeEnvironment environment):IExchangeAdapter
    {
        public ExchangeEnvironment Environment{get;}=environment;
        public int MutationCount{get;private set;}
        public int AccountModeMutationCount{get;private set;}
        public ExchangeOrder? ObservedOrder{get;set;}
        public Task<AccountSnapshot> GetAccountAsync(CancellationToken ct)=>Task.FromResult(new AccountSnapshot(1_000,1_000,1_000,DateTime.UtcNow));
        public Task<IReadOnlyList<ManagedPosition>> GetPositionsAsync(CancellationToken ct)=>Task.FromResult<IReadOnlyList<ManagedPosition>>([]);
        public Task<IReadOnlyList<ExchangeOrder>> GetOpenOrdersAsync(string? symbol,CancellationToken ct)=>Task.FromResult<IReadOnlyList<ExchangeOrder>>([]);
        public Task<TradingRule> GetRulesAsync(string symbol,CancellationToken ct)=>Task.FromResult(new TradingRule(symbol,.001m,.1m,.001m,5m,20));
        public Task<MarketEvidence> GetMarketAsync(string symbol,CancellationToken ct)=>Task.FromResult(new MarketEvidence(symbol,50_000m,49_000m,51_000m,50,0,0,0,new(0,1,1,1,1,1,0),DateTime.UtcNow){Quality=new(){QualityScore=100,LiquidityScore=1,SpreadBps=1,AtrPercent=.01,BestBid=49_999m,BestAsk=50_001m}});
        public Task<IReadOnlyList<DerivativesSnapshot>> GetDerivativeHistoryAsync(string symbol,CancellationToken ct)=>Task.FromResult<IReadOnlyList<DerivativesSnapshot>>([]);
        public Task<IReadOnlyList<CandleEvidence>> GetCandlesAsync(string symbol,string interval,int limit,CancellationToken ct)=>Task.FromResult<IReadOnlyList<CandleEvidence>>([]);
        public Task<IReadOnlyList<CandleEvidence>> GetCandlesRangeAsync(string symbol,string interval,DateTime start,DateTime end,int limit,CancellationToken ct)=>Task.FromResult<IReadOnlyList<CandleEvidence>>([]);
        public Task SetLeverageAsync(string symbol,int leverage,CancellationToken ct){MutationCount++;AccountModeMutationCount++;return Task.CompletedTask;}
        public Task SetMarginModeAsync(string symbol,bool isolated,CancellationToken ct){MutationCount++;AccountModeMutationCount++;return Task.CompletedTask;}
        public Task SetHedgeModeAsync(bool enabled,CancellationToken ct){MutationCount++;AccountModeMutationCount++;return Task.CompletedTask;}
        public Task<ExchangeOrder> PlaceMarketAsync(string symbol,PositionSide side,decimal quantity,string clientOrderId,bool reduceOnly,CancellationToken ct){MutationCount++;return Task.FromResult(new ExchangeOrder(symbol,"order-1",clientOrderId,"FILLED",quantity,50_000m,"MARKET",side,false,DateTime.UtcNow));}
        public Task<ExchangeOrder> PlaceLimitAsync(string symbol,PositionSide side,decimal quantity,decimal price,string clientOrderId,bool reduceOnly,CancellationToken ct)=>PlaceMarketAsync(symbol,side,quantity,clientOrderId,reduceOnly,ct);
        public Task<ExchangeOrder> PlaceProtectionAsync(string symbol,PositionSide sideToClose,decimal stopLoss,decimal takeProfit,string groupId,CancellationToken ct){MutationCount++;return Task.FromResult(new ExchangeOrder(symbol,"protection-1",groupId,"NEW",0,0,"OCO",sideToClose,true,DateTime.UtcNow));}
        public Task<ExchangeOrder?> FindOrderAsync(string symbol,string clientOrderId,CancellationToken ct)=>Task.FromResult(ObservedOrder);
        public Task CancelOrderAsync(string symbol,string orderId,CancellationToken ct){MutationCount++;return Task.CompletedTask;}
        public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }
}
