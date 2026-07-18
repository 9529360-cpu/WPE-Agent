using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace 币安量化机器人.Services.Agent;

public sealed record MaturityTestCase(string Name,string Status,long DurationMs,string Detail);
public sealed class MaturityTestReport
{
    public DateTime StartedAtUtc { get; init; }=DateTime.UtcNow;
    public DateTime CompletedAtUtc { get; set; }
    public bool Success { get; set; }
    public List<MaturityTestCase> Cases { get; }=[];
}

public static class MaturityTestRunner
{
    public static async Task<SmokeRunResult> RunAsync(CancellationToken ct=default)
    {
        var report=new MaturityTestReport();var dir=Path.Combine(AppContext.BaseDirectory,"Data","maturity-tests");Directory.CreateDirectory(dir);var path=Path.Combine(dir,$"maturity-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json");
        try
        {
            var policy=new DecisionPolicy();var skill=new SignalAggregationSkill();var governance=new DecisionGovernanceSkill();
            Test(report,"同向多周期信号形成可执行候选",()=>{var a=skill.Analyze(Evidence(Bullish("BTCUSDT")),policy).Single();Require(a.EntryReady,"强一致信号应满足入场条件");Require(a.RecommendedAction==DecisionAction.OpenLong,"应推荐 OpenLong");Require(a.ConflictRatio<.20,"同向信号冲突率应较低");return a.Summary;});
            Test(report,"同品种多空冲突保持HOLD并解释",()=>{var a=skill.Analyze(Evidence(Mixed("BTCUSDT")),policy).Single();Require(!a.EntryReady,"冲突信号不得通过");Require(a.MissingConditions.Count>0,"必须列出缺失条件");return a.Summary+" | "+string.Join("；",a.MissingConditions);});
            Test(report,"跨品种反向不被误判为同品种冲突",()=>{var all=skill.Analyze(Evidence(Bullish("BTCUSDT"),Bearish("ETHUSDT")),policy);Require(all.Count==2,"应分别评估两个品种");Require(all.All(x=>x.EntryReady),"两个品种各自一致时应分别具备候选资格");return string.Join(" | ",all.Select(x=>x.Summary));});
            Test(report,"过期行情禁止增加风险",()=>{var stale=Bullish("BTCUSDT") with{CollectedAt=DateTime.UtcNow.AddMinutes(-10)};var a=skill.Analyze(Evidence(stale),policy).Single();Require(!a.Fresh&&!a.EntryReady,"过期行情必须禁止入场");return string.Join("；",a.MissingConditions);});
            Test(report,"Reviewer否决低置信候选",()=>{var e=Evidence(Bullish("BTCUSDT"));var a=skill.Analyze(e,policy);var p=new DecisionPlan{Action=DecisionAction.OpenLong,Instrument="BTCUSDT",Confidence=.40,Reason="候选"};var r=governance.Review(p,a,e,policy);Require(r.Decision.Action==DecisionAction.Hold&&!r.Accepted,"低置信候选必须降级为 HOLD");Require(r.Explanation.Contains("允许开单还需要"),"必须解释缺失条件");return r.Explanation;});
            Test(report,"Reviewer通过一致且高置信候选",()=>{var e=Evidence(Bullish("BTCUSDT"));var a=skill.Analyze(e,policy);var p=new DecisionPlan{Action=DecisionAction.OpenLong,Instrument="BTCUSDT",Confidence=.85,Reason="多周期一致",StopLossPrice=90,TakeProfitPrice=110};var r=governance.Review(p,a,e,policy);Require(r.Accepted&&r.Decision.Action==DecisionAction.OpenLong,"一致候选应通过 Reviewer");return r.Explanation;});
            Test(report,"成熟化决策不能绕过60%保证金硬限制",()=>{var market=Bullish("BTCUSDT");var e=new EvidencePack{CollectedAt=DateTime.UtcNow,Account=new(1000,1000,1000,DateTime.UtcNow),Markets=new Dictionary<string,MarketEvidence>{{market.Symbol,market}},Completeness=100,Positions=[new("BTCUSDT",PositionSide.Short,250,100,100,0,50,true,110)]};var d=new DecisionPlan{Action=DecisionAction.OpenLong,Instrument="BTCUSDT",TargetTier=1,Confidence=.9,StopLossPrice=99,TakeProfitPrice=110,Reason="测试"};var result=new RiskAndPositionPlanner().Plan(d,e,new("BTCUSDT",.0001m,.1m,.0001m,5,50),new RiskLimits(),1000);Require(result.Intents.Count==0&&result.Result.Contains("60%"),$"聚合保证金硬限制必须拒绝计划，实际：{result.Result}");return result.Result;});
            Test(report,"关键技能已注册",()=>{var required=new[]{"EvidenceCollector","SignalAggregation","BrainPlanner","DecisionCritic","DecisionReviewer","RiskAndPositionPlanner","ReliableOrderExecutor","ProtectionRecovery"};var names=AgentSkillRegistry.Skills.Select(x=>x.Name).ToHashSet();Require(required.All(names.Contains),"关键技能注册不完整");return $"registered={AgentSkillRegistry.Skills.Count}";});
            report.Success=true;
        }
        catch(Exception ex){report.Cases.Add(new("测试运行中止","FAILED",0,ex.Message));report.Success=false;}
        report.CompletedAtUtc=DateTime.UtcNow;await File.WriteAllTextAsync(path,JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true}),ct);return new(report.Success,path);
    }

    private static void Test(MaturityTestReport report,string name,Func<string> body){var sw=Stopwatch.StartNew();try{report.Cases.Add(new(name,"PASSED",sw.ElapsedMilliseconds,body()));}catch(Exception ex){report.Cases.Add(new(name,"FAILED",sw.ElapsedMilliseconds,ex.Message));throw;}}
    private static void Require(bool condition,string message){if(!condition)throw new InvalidOperationException(message);}
    private static EvidencePack Evidence(params MarketEvidence[] markets)=>new(){CollectedAt=DateTime.UtcNow,Account=new(1000,1000,1000,DateTime.UtcNow),Markets=markets.ToDictionary(x=>x.Symbol),Completeness=100};
    private static DerivativesSnapshot Derivatives(bool bullish)=>bullish?new(-.0002m,100000,0.7m,0.8m,0.8m,1.2m,.003m):new(.0002m,100000,1.3m,1.2m,1.2m,.8m,-.003m);
    private static MarketEvidence Bullish(string symbol)=>new(symbol,100,90,110,70,.006,.012,.025,Derivatives(true),DateTime.UtcNow);
    private static MarketEvidence Bearish(string symbol)=>new(symbol,100,90,110,30,-.006,-.012,-.025,Derivatives(false),DateTime.UtcNow);
    private static MarketEvidence Mixed(string symbol)=>new(symbol,100,90,110,45,.006,-.012,.025,Derivatives(false),DateTime.UtcNow);
}
