using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace 币安量化机器人.Services.Agent;

public sealed record SmokeRunResult(bool Success,string ReportPath);
public sealed record SmokeStep(string Name,string Status,long DurationMs,string Detail);
public sealed class SmokeReport
{
    public DateTime StartedAtUtc { get; init; }
    public DateTime CompletedAtUtc { get; set; }
    public string Environment { get; set; } = "Testnet";
    public bool Success { get; set; }
    public string Failure { get; set; } = string.Empty;
    public List<SmokeStep> Steps { get; } = new();
}

public static class SmokeTestRunner
{
    public static async Task<SmokeRunResult> RunAsync(CancellationToken ct=default)
    {
        var report=new SmokeReport{StartedAtUtc=DateTime.UtcNow};var reportDir=Path.Combine(AppContext.BaseDirectory,"Data","smoke-tests");Directory.CreateDirectory(reportDir);var reportPath=Path.Combine(reportDir,$"smoke-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json");IExchangeAdapter? exchange=null;ReliableOrderExecutor? executor=null;string? symbol=null;var openedBySmoke=false;
        try
        {
            var store=new AgentSettingsStore();var settings=store.Load();store.ImportDesktopTestnetIfEmpty(settings);store.ImportDesktopDeepSeekIfEmpty(settings);if(settings.Environment!=ExchangeEnvironment.Testnet)throw new InvalidOperationException("冒烟入口只允许 Testnet，当前配置不是测试网");var(key,secret)=store.GetCredentials(settings);if(string.IsNullOrWhiteSpace(key)||string.IsNullOrWhiteSpace(secret))throw new InvalidOperationException("测试网 API 凭据不完整");
            exchange=new BinanceFuturesAdapter(ExchangeEnvironment.Testnet,key,secret);var db=new AgentSqliteStore();executor=new ReliableOrderExecutor(exchange,db);await Step(report,"测试网账户与持仓读取",async()=>{var a=await exchange.GetAccountAsync(ct);var p=await exchange.GetPositionsAsync(ct);var o=await exchange.GetOpenOrdersAsync(null,ct);if(a.Equity<=0)throw new InvalidOperationException("测试网账户权益为0，需要 Faucet Token");return $"equity={a.Equity:F2}, available={a.AvailableBalance:F2}, positions={p.Count}, openOrders={o.Count}";});
            var positions=await exchange.GetPositionsAsync(ct);symbol=new[]{"BTCUSDT","ETHUSDT"}.FirstOrDefault(s=>positions.All(p=>!p.Symbol.Equals(s,StringComparison.OrdinalIgnoreCase)));if(symbol is null)throw new InvalidOperationException("BTCUSDT 与 ETHUSDT 都已有仓位，无法选择不干扰现有仓位的冒烟品种");
            TradingRule? rule=null;MarketEvidence? market=null;await Step(report,"交易规则与市场证据",async()=>{rule=await exchange.GetRulesAsync(symbol,ct);market=await exchange.GetMarketAsync(symbol,ct);if(rule.StepSize<=0||rule.MinQuantity<=0||market.Price<=0)throw new InvalidOperationException("交易规则或市场价格无效");return $"{symbol} price={market.Price:F2}, step={rule.StepSize}, minQty={rule.MinQuantity}, minNotional={rule.MinNotional}, basis={market.Derivatives.Basis:P4}";});
            EvidencePack? evidence=null;await Step(report,"完整证据包与新闻",async()=>{evidence=await new EvidenceCollector(exchange).CollectAsync(ct);if(evidence.Markets.Count==0)throw new InvalidOperationException("没有可用市场证据");return $"completeness={evidence.Completeness}/100, markets={evidence.Markets.Count}, news={evidence.News.Count}, missing={string.Join(',',evidence.MissingSources)}";});
            IReadOnlyList<MarketDecisionAssessment>? assessments=null;await Step(report,"市场状态、信号聚合与冲突解释",()=>{assessments=new SignalAggregationSkill().Analyze(evidence!,settings.Decision);if(assessments.Count==0)throw new InvalidOperationException("没有生成本地市场评估");if(assessments.Any(x=>x.Signals.Count<8))throw new InvalidOperationException("信号贡献不完整");return Task.FromResult(string.Join(" | ",assessments.Select(x=>x.Summary)));});
            if(!settings.Brains.TryGetValue(settings.ActiveBrain,out var brainSlot))throw new InvalidOperationException("当前大脑未配置");var brainKey=SecretVaultService.Decrypt(brainSlot.EncryptedKey);var brain=new HttpBrainProvider(brainSlot,brainKey);await Step(report,"当前唯一大脑健康检查",async()=>{var health=await brain.HealthCheckAsync(ct);if(!health.Healthy)throw new InvalidOperationException(health.Message);return $"{brain.Name}: {health.Message}";});
            await Step(report,"Planner、Critic 与 Reviewer 决策治理",async()=>{var result=await brain.DecideAsync(evidence!,new(brain.Name,false,null,Array.Empty<string>(),assessments!,0),ct);if(result.Decision.Instrument is not("BTCUSDT" or "ETHUSDT"))throw new InvalidOperationException("大脑返回了无效品种");var review=new DecisionGovernanceSkill().Review(result.Decision,assessments!,evidence!,settings.Decision);if(string.IsNullOrWhiteSpace(review.Explanation))throw new InvalidOperationException("Reviewer 没有生成决策解释");var reason=result.Decision.Reason.Length>220?result.Decision.Reason[..220]+"…":result.Decision.Reason;return $"proposed={result.Decision.Action}, final={review.Decision.Action}, instrument={result.Decision.Instrument}, confidence={result.Decision.Confidence:F2}, verdict={review.Verdict}, reason={reason}, review={review.Explanation}";});
            await Step(report,"最小仓位开仓、成交与保护单",async()=>
            {
                var quantity=CeilingToStep(Math.Max(rule!.MinQuantity,rule.MinNotional*1.10m/market!.Price),rule.StepSize);var stop=rule.RoundPrice(market.Price*.98m);var take=rule.RoundPrice(market.Price*1.02m);var id=ClientId("OPEN");var intent=new ExecutionIntent(symbol,PositionSide.Long,quantity,false,stop,take,id,"Testnet smoke open",DecisionAction.OpenLong);openedBySmoke=true;var result=await executor.ExecuteAsync("SMOKE-"+Guid.NewGuid().ToString("N"),intent,Math.Min(10,rule.MaxLeverage),true,ct);return $"{symbol} long quantity={quantity}, stop={stop}, take={take}, result={result}";
            });
            await Step(report,"平仓、保护单清理与最终核对",async()=>{await CloseSmokePosition(exchange,executor,symbol,ct);openedBySmoke=false;var remaining=await exchange.GetPositionsAsync(ct);if(remaining.Any(p=>p.Symbol==symbol&&p.Side==PositionSide.Long&&p.Quantity>0))throw new InvalidOperationException("冒烟多仓仍有残留");var protections=(await exchange.GetOpenOrdersAsync(symbol,ct)).Count(x=>x.IsProtection&&x.PositionSide==PositionSide.Long);if(protections>0)throw new InvalidOperationException($"仍有 {protections} 个多仓保护单残留");return"仓位与保护单均已清理";});
            report.Success=true;
        }
        catch(Exception ex){report.Failure=ex.Message;report.Steps.Add(new("冒烟测试终止","FAILED",0,ex.Message));}
        finally
        {
            if(openedBySmoke&&exchange is not null&&executor is not null&&symbol is not null)try{using var cleanup=new CancellationTokenSource(TimeSpan.FromSeconds(60));await CloseSmokePosition(exchange,executor,symbol,cleanup.Token);report.Steps.Add(new("失败后强制清理","PASSED",0,"已尝试平掉冒烟仓位并清理保护单"));}catch(Exception ex){report.Steps.Add(new("失败后强制清理","FAILED",0,ex.Message));report.Success=false;}
            if(exchange is not null)await exchange.DisposeAsync();report.CompletedAtUtc=DateTime.UtcNow;await File.WriteAllTextAsync(reportPath,JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true}),CancellationToken.None);
        }
        return new(report.Success,reportPath);
    }

    private static async Task Step(SmokeReport report,string name,Func<Task<string>> action){var sw=Stopwatch.StartNew();try{var detail=await action();report.Steps.Add(new(name,"PASSED",sw.ElapsedMilliseconds,detail));}catch(Exception ex){report.Steps.Add(new(name,"FAILED",sw.ElapsedMilliseconds,ex.Message));throw;}}
    private static async Task CloseSmokePosition(IExchangeAdapter exchange,ReliableOrderExecutor executor,string symbol,CancellationToken ct){var position=(await exchange.GetPositionsAsync(ct)).FirstOrDefault(p=>p.Symbol==symbol&&p.Side==PositionSide.Long);if(position is null||position.Quantity<=0)return;var intent=new ExecutionIntent(symbol,PositionSide.Long,position.Quantity,true,0,0,ClientId("CLOSE"),"Testnet smoke cleanup",DecisionAction.CloseLong);_=await executor.ExecuteAsync("SMOKE-CLEAN-"+Guid.NewGuid().ToString("N"),intent,Math.Max(1,(int)position.Leverage),position.Isolated,ct);}
    private static decimal CeilingToStep(decimal value,decimal step)=>step<=0?value:Math.Ceiling(value/step)*step;
    private static string ClientId(string action){var raw=$"WPE-SMOKE-{action}-{DateTime.UtcNow:HHmmss}-{Guid.NewGuid():N}";return raw[..Math.Min(36,raw.Length)];}
}
