using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace 币安量化机器人.Services.Exchange;

public static class ExchangePublicTestRunner
{
    public static async Task<ExchangeAdapterTestResult> RunAsync(CancellationToken ct=default)
    {
        var report=new ExchangeAdapterTestReport();
        var directory=Path.Combine(AppContext.BaseDirectory,"Data","exchange-public-tests");
        Directory.CreateDirectory(directory);
        var path=Path.Combine(directory,$"exchange-public-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json");
        var catalog=new ExchangeProviderCatalog();
        await Test(report,catalog,"bybit","https://api-testnet.bybit.com",new Dictionary<string,string>{{"apiKey","public-test"},{"secret","public-test"}},ct);
        await Test(report,catalog,"okx","https://www.okx.com",new Dictionary<string,string>{{"apiKey","public-test"},{"secret","public-test"},{"passphrase","public-test"}},ct);
        await Test(report,catalog,"bitget","https://api.bitget.com",new Dictionary<string,string>{{"apiKey","public-test"},{"secret","public-test"},{"passphrase","public-test"}},ct);
        await Test(report,catalog,"gate","https://api-testnet.gateapi.io",new Dictionary<string,string>{{"apiKey","public-test"},{"secret","public-test"}},ct);
        report.Success=report.Cases.All(x=>x.Status=="PASSED");
        report.CompletedAtUtc=DateTime.UtcNow;
        await File.WriteAllTextAsync(path,JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true}),CancellationToken.None);
        return new(report.Success,path);
    }

    private static async Task Test(ExchangeAdapterTestReport report,ExchangeProviderCatalog catalog,string providerId,string endpoint,IReadOnlyDictionary<string,string> credentials,CancellationToken ct)
    {
        var sw=Stopwatch.StartNew();
        try
        {
            var profile=new ExchangeConnectionProfile{Id=$"public-{providerId}",ProviderId=providerId,DisplayName=providerId,IsTestnet=true,ExecutionEnabled=false,Endpoint=endpoint,TimeoutSeconds=20};
            await using var provider=catalog.Create(profile,credentials);
            var serverTime=await provider.GetServerTimeAsync(ct);
            var rules=await provider.GetRulesAsync("BTCUSDT",ct);
            var candles=await provider.GetCandlesAsync("BTCUSDT","15m",30,ct);
            var market=await provider.GetMarketAsync("BTCUSDT",ct);
            if(Math.Abs((DateTime.UtcNow-serverTime).TotalMinutes)>10)throw new InvalidOperationException("server clock differs by more than ten minutes");
            if(rules.StepSize<=0||rules.TickSize<=0)throw new InvalidOperationException("invalid trading rules");
            if(candles.Count<20||market.Price<=0)throw new InvalidOperationException("public market data is incomplete");
            report.Cases.Add(new(providerId,"PASSED",sw.ElapsedMilliseconds,$"time={serverTime:O}; candles={candles.Count}; price={market.Price}; qtyStep={rules.StepSize}; tick={rules.TickSize}"));
        }
        catch(Exception ex)
        {
            report.Cases.Add(new(providerId,"FAILED",sw.ElapsedMilliseconds,ex.Message));
        }
    }
}
