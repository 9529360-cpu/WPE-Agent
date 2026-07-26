using Microsoft.Data.Sqlite;
using WpeAgent.TradingAuthorization;
using 币安量化机器人.Services.Agent;

namespace WPE.Tests;

public sealed class TradingApprovalStoreTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026,7,21,1,0,0,TimeSpan.Zero);
    private readonly string _directory=Path.Combine(Path.GetTempPath(),"wpe-approval-store-"+Guid.NewGuid().ToString("N"));
    private string DatabasePath=>Path.Combine(_directory,"agent.db");

    [Fact]
    public async Task RequestAndReceipt_SurviveStoreRestart()
    {
        var store=Store();var request=Request();var receipt=Receipt();
        Assert.True((await store.SaveTradingApprovalRequestAsync(request,CancellationToken.None)).Succeeded);
        Assert.True((await store.SaveTradingApprovalReceiptAsync(request.RequestId,receipt,CancellationToken.None)).Succeeded);
        SqliteConnection.ClearAllPools();

        var reopened=Store();var savedRequest=await reopened.GetTradingApprovalRequestAsync(request.RequestId,CancellationToken.None);var savedReceipt=await reopened.GetTradingApprovalReceiptAsync(receipt.ReceiptId,CancellationToken.None);

        Assert.Equal(request,savedRequest);Assert.Equal(request.RequestId,savedReceipt?.RequestId);Assert.Equal(receipt,savedReceipt?.Receipt);
    }

    [Fact]
    public async Task RevokedAndConsumedFields_ArePersisted()
    {
        var store=Store();var request=Request() with{RevokedAtUtc=Now.AddMinutes(-2),ConsumedAtUtc=Now.AddMinutes(-1)};

        Assert.True((await store.SaveTradingApprovalRequestAsync(request,CancellationToken.None)).Succeeded);

        Assert.Equal(request,await store.GetTradingApprovalRequestAsync(request.RequestId,CancellationToken.None));

        var activeRequest=Request() with{RequestId="request-state-2"};
        var receipt=Receipt() with{ReceiptId="receipt-state-2",RevokedAtUtc=Now.AddMinutes(-2),ConsumedAtUtc=Now.AddMinutes(-1)};
        Assert.True((await store.SaveTradingApprovalRequestAsync(activeRequest,CancellationToken.None)).Succeeded);
        Assert.True((await store.SaveTradingApprovalReceiptAsync(activeRequest.RequestId,receipt,CancellationToken.None)).Succeeded);
        Assert.Equal(receipt,(await store.GetTradingApprovalReceiptAsync(receipt.ReceiptId,CancellationToken.None))?.Receipt);
    }

    [Fact]
    public async Task ValidApproval_IsConsumedExactlyOnceAndMarksBothRows()
    {
        var store=await ReadyStore();var consumption=Consumption();

        var first=await store.TryConsumeTradingApprovalAsync(consumption,CancellationToken.None);var second=await store.TryConsumeTradingApprovalAsync(consumption,CancellationToken.None);

        Assert.True(first.Consumed);Assert.False(second.Consumed);Assert.Equal("approval.not-consumable",second.Code);
        Assert.Equal(Now,(await store.GetTradingApprovalRequestAsync(consumption.RequestId,CancellationToken.None))?.ConsumedAtUtc);
        Assert.Equal(Now,(await store.GetTradingApprovalReceiptAsync(consumption.ReceiptId,CancellationToken.None))?.Receipt.ConsumedAtUtc);
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("request-revoked")]
    [InlineData("receipt-revoked")]
    [InlineData("receipt-rejected")]
    [InlineData("correlation")]
    [InlineData("hash")]
    [InlineData("user")]
    [InlineData("device")]
    [InlineData("session")]
    public async Task InvalidStateOrContext_FailsWithoutConsuming(string mutation)
    {
        var request=Request();var receipt=Receipt();
        if(mutation=="expired"){request=request with{ExpiresAtUtc=Now};receipt=receipt with{ExpiresAtUtc=Now};}
        else if(mutation=="request-revoked")request=request with{RevokedAtUtc=Now.AddMinutes(-1)};
        else if(mutation=="receipt-revoked")receipt=receipt with{RevokedAtUtc=Now.AddMinutes(-1)};
        else if(mutation=="receipt-rejected")receipt=receipt with{Approved=false};
        var store=Store();Assert.True((await store.SaveTradingApprovalRequestAsync(request,CancellationToken.None)).Succeeded);
        if(mutation=="request-revoked")
        {
            Assert.False((await store.SaveTradingApprovalReceiptAsync(request.RequestId,receipt,CancellationToken.None)).Succeeded);
            return;
        }
        Assert.True((await store.SaveTradingApprovalReceiptAsync(request.RequestId,receipt,CancellationToken.None)).Succeeded);
        var consumption=Consumption();consumption=mutation switch{"correlation"=>consumption with{CorrelationId="different"},"hash"=>consumption with{IntentHash="different"},"user"=>consumption with{UserId="different"},"device"=>consumption with{DeviceId="different"},"session"=>consumption with{SessionId="different"},_=>consumption};

        var result=await store.TryConsumeTradingApprovalAsync(consumption,CancellationToken.None);

        Assert.False(result.Consumed);Assert.Equal("approval.not-consumable",result.Code);
        Assert.Null((await store.GetTradingApprovalRequestAsync(request.RequestId,CancellationToken.None))?.ConsumedAtUtc);
        Assert.Null((await store.GetTradingApprovalReceiptAsync(receipt.ReceiptId,CancellationToken.None))?.Receipt.ConsumedAtUtc);
    }

    [Fact]
    public async Task ConcurrentConsumption_AllowsOnlyOneWinner()
    {
        var store=await ReadyStore();var consumption=Consumption();

        var results=await Task.WhenAll(Enumerable.Range(0,24).Select(_=>store.TryConsumeTradingApprovalAsync(consumption,CancellationToken.None)));

        Assert.Equal(1,results.Count(x=>x.Consumed));Assert.Equal(23,results.Count(x=>!x.Consumed));
    }

    [Fact]
    public async Task ParameterizedSql_PreservesQuotedIdentifiersWithoutChangingSchema()
    {
        const string quoted="value'; DROP TABLE trading_approval_requests; --";var store=Store();var request=Request() with{RequestId=quoted,CorrelationId=quoted};var receipt=Receipt() with{CorrelationId=quoted};

        Assert.True((await store.SaveTradingApprovalRequestAsync(request,CancellationToken.None)).Succeeded);
        Assert.True((await store.SaveTradingApprovalReceiptAsync(request.RequestId,receipt,CancellationToken.None)).Succeeded);

        Assert.Equal(quoted,(await store.GetTradingApprovalRequestAsync(quoted,CancellationToken.None))?.CorrelationId);
        Assert.NotNull(await store.GetTradingApprovalRequestAsync(quoted,CancellationToken.None));
    }

    [Fact]
    public async Task FailureCode_DoesNotEchoIdentifiersOrHash()
    {
        var store=await ReadyStore();var consumption=Consumption() with{IntentHash="private-intent-hash"};

        var result=await store.TryConsumeTradingApprovalAsync(consumption,CancellationToken.None);

        Assert.False(result.Consumed);Assert.DoesNotContain(consumption.IntentHash,result.Code,StringComparison.Ordinal);Assert.DoesNotContain(consumption.RequestId,result.Code,StringComparison.Ordinal);
    }

    private async Task<AgentSqliteStore> ReadyStore()
    {
        var store=Store();var request=Request();Assert.True((await store.SaveTradingApprovalRequestAsync(request,CancellationToken.None)).Succeeded);Assert.True((await store.SaveTradingApprovalReceiptAsync(request.RequestId,Receipt(),CancellationToken.None)).Succeeded);return store;
    }
    private AgentSqliteStore Store()=>new(DatabasePath,()=>Now);
    private static TradingApprovalRequest Request()=>new("request-1",TradingAuthorizationMode.Review,"correlation-1","intent-hash-1","user-1","device-1","session-1",Now.AddMinutes(-5),Now.AddMinutes(5));
    private static TradingApprovalReceipt Receipt()=>new("receipt-1","correlation-1","intent-hash-1","user-1","device-1","session-1",true,Now.AddMinutes(-4),Now.AddMinutes(4));
    private static TradingApprovalConsumption Consumption()=>new("request-1","receipt-1","correlation-1","intent-hash-1","user-1","device-1","session-1");
    public void Dispose(){SqliteConnection.ClearAllPools();if(Directory.Exists(_directory))Directory.Delete(_directory,true);}
}
