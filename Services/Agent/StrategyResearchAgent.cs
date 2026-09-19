using System.Text.Json;
using 币安量化机器人.Core.Strategy;

namespace 币安量化机器人.Services.Agent;

public sealed class StrategyResearchAgent
{
    internal const int MaximumVariantsPerFamily = 6;
    internal const int TargetConcurrentCandidatesPerFamily = 2;
    internal static readonly TimeSpan ExplorationCooldown=TimeSpan.FromHours(6);
    internal static readonly TimeSpan ActiveRevalidationInterval=TimeSpan.FromHours(12);
    public static WpeAgent.ModelOff.ModelOffAgentOutputV1 ProduceTechnicalModelOff(ModelOffResearchInputV1 input)
        => input.Capability == ModelOffResearchCapabilityV1.Technical
            ? DeterministicResearchCapabilityProducerV1.Produce(input)
            : throw new ArgumentException("Technical producer requires the technical capability.", nameof(input));

    public static WpeAgent.ModelOff.ModelOffAgentOutputV1 ProduceBacktestModelOff(ModelOffResearchInputV1 input)
        => input.Capability == ModelOffResearchCapabilityV1.Backtest
            ? DeterministicResearchCapabilityProducerV1.Produce(input)
            : throw new ArgumentException("Backtest producer requires the backtest capability.", nameof(input));

    private readonly AgentSqliteStore _database;
    private readonly StrategyGovernor _governor;
    private readonly HistoricalResearchEngine _engine = new();
    private readonly WpeAgent.RuntimeServices.RuntimeBacktestStateStore? _runtimeBacktests;
    private volatile bool _schedulerHealthy;
    private readonly Func<DateTime> _utcNow;

    public StrategyResearchAgent(AgentSqliteStore database, StrategyGovernor? governor = null, WpeAgent.RuntimeServices.RuntimeBacktestStateStore? runtimeBacktests = null,Func<DateTime>? utcNow=null)
    {
        _database = database;
        _governor = governor ?? new StrategyGovernor();
        _runtimeBacktests = runtimeBacktests;
        _utcNow=utcNow??(()=>DateTime.UtcNow);
    }

    public bool SchedulerHealthy => _schedulerHealthy;
    public void SetSchedulerHealth(bool healthy) => _schedulerHealthy = healthy;

    public async Task<StrategyResearchSnapshot> RunOnceAsync(IReadOnlyList<string> symbols, RiskLimits limits, CancellationToken ct)
    {
        var candidates = await EnsureCandidatesAsync(symbols, ct);
        foreach(var profile in candidates.Where(x=>x.Lifecycle==StrategyLifecycle.Degraded).ToArray())
        {
            var previous=profile.Lifecycle;profile.Lifecycle=StrategyLifecycle.Retired;profile.StateChangedAtUtc=_utcNow().ToUniversalTime();profile.LastReason="degraded strategy archived before bounded replacement";
            await _database.UpsertStrategyAsync(profile,ct);await _database.RecordStrategyLifecycleAsync(profile,previous,profile.LastReason,ct);
        }
        var newsBySymbol=new Dictionary<string,IReadOnlyList<NewsFeature>>(StringComparer.Ordinal);
        var validated = 0;
        var validationCandidates=new List<StrategyProfile>();
        var researchNow=_utcNow().ToUniversalTime();
        foreach(var profile in candidates)
        {
            if(profile.Lifecycle==StrategyLifecycle.Draft||(profile.BuiltIn&&profile.ValidationTrades==0)){validationCandidates.Add(profile);continue;}
            if(profile.Lifecycle!=StrategyLifecycle.Active)continue;
            var exactValidation=await _database.GetLatestStrategyValidationAsync(profile.Id,profile.Version,ct);
            var latestBacktest=await _database.GetLatestBacktestRunAsync(profile.Id,profile.Version,ct);
            var backtestCompletedAt=latestBacktest?.CompletedAtUtc.ToUniversalTime();
            if(exactValidation is null||latestBacktest is null||backtestCompletedAt is null||backtestCompletedAt>researchNow||researchNow-backtestCompletedAt.Value>=ActiveRevalidationInterval)
                validationCandidates.Add(profile);
        }
        foreach (var profile in validationCandidates)
        {
            var candles = await _database.LoadHistoricalCandlesAsync(profile.Symbol, "1h", 5000, ct);
            if(!newsBySymbol.TryGetValue(profile.Symbol,out var news))
            {
                news=candles.Count==0?[]:await _database.GetHistoricalNewsFeaturesAsync(profile.Symbol,new DateTimeOffset(candles[0].OpenTime.ToUniversalTime()).AddHours(-48),new DateTimeOffset(candles[^1].OpenTime.ToUniversalTime()),20000,ct);
                newsBySymbol[profile.Symbol]=news;
            }
            var validation = _engine.Validate(profile, candles, news, limits);
            if(!string.IsNullOrEmpty(validation.TimelineSha256))
            {
                var timelineArtifact=_engine.TimelineArtifact(profile,candles,news);
                if(timelineArtifact is null
                   ||!string.Equals(validation.TimelineSha256,timelineArtifact.CanonicalSha256,StringComparison.Ordinal))
                    throw new InvalidOperationException("Strategy validation timeline provenance mismatch.");
                await _database.SaveStrategyExposureTimelineAsync(timelineArtifact,ct);
            }
            await _database.SaveStrategyValidationAsync(validation, ct);
            var validationCompletedAt = _utcNow().ToUniversalTime();
            var coverageDays = candles.Count < 2 ? 0 : Math.Max(0, (int)Math.Floor((candles[^1].OpenTime.ToUniversalTime() - candles[0].OpenTime.ToUniversalTime()).TotalDays));
            await _database.SaveBacktestRunAsync(new PersistedBacktestRun(Guid.NewGuid().ToString("N"),profile.Id,profile.Version,profile.Symbol,validation.Passed?"PASSED":"FAILED",validationCompletedAt,coverageDays,validation.Trades,validation.OutOfSampleReturn,validation.MaxDrawdown,validation.Sharpe),ct);
            var previous=profile.Lifecycle;
            var next = profile.BuiltIn&&profile.ValidationTrades==0
                ? (_governor.CanPromote(profile,validation)?StrategyLifecycle.Shadow:StrategyLifecycle.Retired)
                : _governor.NextLifecycle(profile, validation);
            profile.QualityScore = validation.QualityScore; profile.Expectancy = validation.Expectancy;
            profile.MaxDrawdown = validation.MaxDrawdown; profile.Sharpe = validation.Sharpe; profile.ValidationTrades = validation.Trades;
            if (next != profile.Lifecycle) { profile.Lifecycle = next; profile.StateChangedAtUtc = _utcNow().ToUniversalTime(); profile.LastReason = validation.Summary; }
            await _database.UpsertStrategyAsync(profile, ct);
            if(next!=previous)await _database.RecordStrategyLifecycleAsync(profile,previous,validation.Summary,ct);
            validated++;
        }

        if (_runtimeBacktests is not null) await _runtimeBacktests.RefreshAsync(ct);

        var snapshot = await _database.GetStrategySnapshotAsync(ct);
        var completedAt = _utcNow().ToUniversalTime();
        var newsFeatureCount=newsBySymbol.Values.Sum(x=>x.Count);
        var completed = snapshot with { Status = "LOCAL_RESEARCH_COMPLETE", LastRunAtUtc = completedAt, LastMessage = $"validated={validated}; news_features={newsFeatureCount}" };
        await _database.SetStateAsync("strategy-research:last-run", JsonSerializer.Serialize(new { snapshot = completed, NewsFeatures = newsFeatureCount, validated }), ct);
        return completed;
    }

    public async Task<StrategyResearchSnapshot> ObserveAsync(EvidencePack evidence, CancellationToken ct)
    {
        var profiles = await _database.GetStrategiesAsync(ct);
        foreach (var profile in profiles.Where(x => x.Lifecycle is StrategyLifecycle.Shadow or StrategyLifecycle.Active))
        {
            var market = evidence.Markets.GetValueOrDefault(profile.Symbol);
            if (market is null) continue;
            var signal = _engine.Signal(profile, market, evidence.News);
            var shadowObservation=await CreateShadowObservationAsync(profile,signal,market,ct);
            if(shadowObservation is not null
               &&await _database.SaveStrategyShadowObservationAsync(shadowObservation,ct))
                await _database.RecordStrategyObservationAsync(profile.Id, profile.Symbol, signal.Direction, market.Price, signal.Confidence, ct);
            var performance = await _database.GetStrategyObservationPerformanceAsync(profile.Id, ct);
            profile.ShadowObservations = performance.Observations; profile.Expectancy = performance.Expectancy;
            profile.MaxDrawdown = performance.MaxDrawdown; profile.FailureStreak = performance.FailureStreak; profile.QualityScore = performance.QualityScore;
            var previous=profile.Lifecycle;var next = _governor.NextLifecycle(profile);
            if(next==profile.Lifecycle&&_governor.ShouldRetireShadow(profile,_utcNow()))next=StrategyLifecycle.Retired;
            if (next != profile.Lifecycle) { profile.Lifecycle = next; profile.StateChangedAtUtc = _utcNow(); profile.LastReason = next==StrategyLifecycle.Retired?$"shadow evaluation exhausted without qualification: {performance.Summary}":$"local performance: {performance.Summary}"; }
            await _database.UpsertStrategyAsync(profile, ct);
            if(next!=previous)await _database.RecordStrategyLifecycleAsync(profile,previous,profile.LastReason,ct);
        }
        // Deterministic failover: a degraded strategy never remains the selected
        // strategy when a validated Shadow challenger has passed the same gates.
        foreach (var symbol in profiles.Select(x => x.Symbol).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var hasActive = profiles.Any(x => x.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase) && x.Lifecycle == StrategyLifecycle.Active);
            if (hasActive) continue;
            var challenger = profiles.Where(x => x.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase))
                .Where(x => _governor.CanActivateFromShadow(x))
                .OrderByDescending(x => x.QualityScore)
                .ThenByDescending(x => x.Expectancy)
                .FirstOrDefault();
            if (challenger is null) continue;
            challenger.Lifecycle = StrategyLifecycle.Active;
            challenger.StateChangedAtUtc = _utcNow();
            challenger.LastReason = "deterministic failover from degraded strategy";
            await _database.UpsertStrategyAsync(challenger, ct);
            await _database.RecordStrategyLifecycleAsync(challenger,StrategyLifecycle.Shadow,challenger.LastReason,ct);
        }
        return await _database.GetStrategySnapshotAsync(ct);
    }

    public StrategySignal GetSignal(StrategyProfile profile, MarketEvidence market, IReadOnlyList<NewsEvidence> news)
        => _schedulerHealthy ? _engine.Signal(profile, market, news) : new StrategySignal(profile.Id, profile.Symbol, 0, 0, "strategy research heartbeat is stale; hold", profile.Version);

    private async Task<StrategyShadowObservationV1?> CreateShadowObservationAsync(
        StrategyProfile profile,
        StrategySignal signal,
        MarketEvidence market,
        CancellationToken ct)
    {
        if(!MarketEvidenceProvenanceCanonicalizerV1.IsCanonical(market)
           ||market.Provenance is null
           ||!string.Equals(market.Provenance.Environment,"Testnet",StringComparison.Ordinal))
            return null;

        var persistedValidation=await _database.GetLatestStrategyValidationAsync(profile.Id,profile.Version,ct);
        var backtest=await _database.GetLatestBacktestRunAsync(profile.Id,profile.Version,ct);
        if(persistedValidation is null
           ||backtest is null
           ||!string.Equals(backtest.StrategyId,profile.Id,StringComparison.Ordinal)
           ||!string.Equals(backtest.StrategyVersion,profile.Version,StringComparison.Ordinal)
           ||!string.Equals(backtest.Symbol,profile.Symbol,StringComparison.Ordinal)
           ||!string.Equals(backtest.Status,"PASSED",StringComparison.Ordinal))
            return null;

        var validation=persistedValidation.Validation;
        if(!validation.Passed
           ||!_governor.CanPromote(profile,validation)
           ||string.IsNullOrWhiteSpace(validation.TimelineSha256))
            return null;

        var completedUtc=backtest.CompletedAtUtc.Kind==DateTimeKind.Utc
            ?backtest.CompletedAtUtc
            :backtest.CompletedAtUtc.ToUniversalTime();
        var completedAt=new DateTimeOffset(DateTime.SpecifyKind(completedUtc,DateTimeKind.Utc));
        if((persistedValidation.CreatedAtUtc-completedAt).Duration()>StrategyResearchAuthority.MaximumEvidencePairSkew)
            return null;

        var nowUtc=_utcNow().ToUniversalTime();
        var observedAt=new DateTimeOffset(DateTime.SpecifyKind(nowUtc,DateTimeKind.Utc));
        var validationFact=BacktestValidationCanonicalizerV1.Create(
            profile.Symbol,
            profile.Id,
            profile.Version,
            completedAt,
            validation.SampleSize,
            validation.Trades,
            validation.OutOfSampleTrades,
            backtest.CoverageDays,
            validation.WinRate,
            validation.ProfitFactor,
            validation.Expectancy,
            validation.MaxDrawdown,
            validation.Sharpe,
            validation.OutOfSampleReturn,
            validation.WalkForwardScore,
            validation.MonteCarloLossProbability,
            validation.QualityScore,
            approved:true,
            promoted:profile.Lifecycle==StrategyLifecycle.Active);
        if(!BacktestValidationCanonicalizerV1.IsCanonical(validationFact,observedAt))
            return null;

        var timeline=await _database.GetStrategyExposureTimelineAsync(
            validation.TimelineSha256,
            profile.Id,
            profile.Version,
            profile.Symbol,
            ct);
        if(timeline is null
           ||!string.Equals(timeline.CanonicalSha256,validation.TimelineSha256,StringComparison.Ordinal))
            return null;

        try
        {
            return StrategyShadowObservationCanonicalizerV1.Create(
                profile,signal,market,validationFact,timeline,observedAt);
        }
        catch(ArgumentException){return null;}
        catch(InvalidOperationException){return null;}
    }

    private async Task<IReadOnlyList<StrategyProfile>> EnsureCandidatesAsync(IReadOnlyList<string> symbols, CancellationToken ct)
    {
        var existing = await _database.GetStrategiesAsync(ct);
        foreach(var profile in existing.Where(x=>x.Lifecycle!=StrategyLifecycle.Retired&&!_engine.Strategies.IsProfileCompatible(x)).ToArray())
        {
            var previous=profile.Lifecycle;
            profile.Lifecycle=StrategyLifecycle.Retired;
            profile.StateChangedAtUtc=_utcNow();
            profile.LastReason=$"strategy implementation changed; current={profile.Version}; required={_engine.Strategies.Resolve(profile.Family).ImplementationVersion}; full revalidation required";
            await _database.UpsertStrategyAsync(profile,ct);
            await _database.RecordStrategyLifecycleAsync(profile,previous,profile.LastReason,ct);
        }

        var result = new List<StrategyProfile>(existing);
        foreach (var symbolValue in symbols.Distinct(StringComparer.OrdinalIgnoreCase))
        foreach (var family in _engine.Strategies.Families)
        {
            var symbol=symbolValue.ToUpperInvariant();
            var live=result.Count(x=>x.Symbol.Equals(symbol,StringComparison.OrdinalIgnoreCase)&&x.Family==family&&_engine.Strategies.IsProfileCompatible(x)&&x.Lifecycle is StrategyLifecycle.Draft or StrategyLifecycle.Shadow or StrategyLifecycle.Active);
            for(var variant=0;live<TargetConcurrentCandidatesPerFamily&&variant<MaximumVariantsPerFamily;variant++)
            {
                var id=_engine.Strategies.CandidateId(symbol,family,variant.ToString());if(result.Any(x=>x.Id.Equals(id,StringComparison.OrdinalIgnoreCase)))continue;
                var parent=result.Where(x=>x.Symbol.Equals(symbol,StringComparison.OrdinalIgnoreCase)&&x.Family==family&&_engine.Strategies.IsProfileCompatible(x)&&x.Lifecycle==StrategyLifecycle.Active&&x.ValidationTrades>=StrategyGovernor.MinimumValidationTrades&&x.ShadowObservations>=StrategyGovernor.MinimumShadowObservations&&x.QualityScore>=StrategyGovernor.MinimumQualityScore&&x.Expectancy>0&&x.MaxDrawdown<=StrategyGovernor.MaximumPromotedDrawdown)
                    .OrderByDescending(x=>x.QualityScore).ThenByDescending(x=>x.Expectancy).ThenBy(x=>x.Id,StringComparer.Ordinal).FirstOrDefault();
                var derived=variant>=2&&parent is not null;var generation=derived?parent!.Generation+1:0;
                var parameters=derived?LocalStrategyParameters.Derive(family,parent!.Parameters,variant-1):LocalStrategyParameters.For(family,variant);
                var version=_engine.Strategies.BindProfileVersion(family,$"{family.ToString().ToLowerInvariant()}-{variant+1}");
                var profile=new StrategyProfile{Id=id,Version=version,Symbol=symbol,Family=family,Parameters=parameters,ParametersHash=LocalStrategyParameters.Hash(parameters),ParentStrategyId=derived?parent!.Id:null,ParentStrategyVersion=derived?parent!.Version:null,Generation=generation,Lifecycle=StrategyLifecycle.Draft,BuiltIn=family==StrategyFamily.TrendBreakout&&variant==0,LastReason=derived?"bounded deterministic child of qualified active strategy":variant<2?"deterministic local seed":"bounded deterministic replacement"};
                result.Add(profile);await _database.UpsertStrategyAsync(profile,ct);live++;
            }
            if(live>=TargetConcurrentCandidatesPerFamily)continue;
            var familyRows=result.Where(x=>x.Symbol.Equals(symbol,StringComparison.OrdinalIgnoreCase)&&x.Family==family&&_engine.Strategies.IsProfileCompatible(x)).ToArray();
            if(familyRows.Length<MaximumVariantsPerFamily||familyRows.Max(x=>x.CreatedAtUtc)>_utcNow()-ExplorationCooldown)continue;
            var index=Math.Max(MaximumVariantsPerFamily,familyRows.Max(x=>ExplorationIndex(x.Id))+1);StrategyProfile? exploration=null;
            for(var attempt=0;attempt<512&&exploration is null;attempt++,index++)
            {
                var parameters=Explore(family,index);var hash=LocalStrategyParameters.Hash(parameters);if(familyRows.Any(x=>string.Equals(x.ParametersHash,hash,StringComparison.Ordinal)))continue;
                var id=_engine.Strategies.CandidateId(symbol,family,$"explore-{index}");
                var version=_engine.Strategies.BindProfileVersion(family,$"{family.ToString().ToLowerInvariant()}-explore-{index}");
                exploration=new StrategyProfile{Id=id,Version=version,Symbol=symbol,Family=family,Parameters=parameters,ParametersHash=hash,Generation=0,Lifecycle=StrategyLifecycle.Draft,CreatedAtUtc=_utcNow(),LastReason="budgeted deterministic exploration candidate"};
            }
            if(exploration is not null){result.Add(exploration);await _database.UpsertStrategyAsync(exploration,ct);}
        }
        return result;
    }

    private static int ExplorationIndex(string id)
    {
        var marker="-explore-";var at=id.LastIndexOf(marker,StringComparison.OrdinalIgnoreCase);return at>=0&&int.TryParse(id[(at+marker.Length)..],out var value)?value:-1;
    }

    internal static LocalStrategyParameters Explore(StrategyFamily family,int index)
    {
        if(index<0)throw new ArgumentOutOfRangeException(nameof(index));
        if(family==StrategyFamily.TrendBreakout)
        {
            var trendFast=8+2*(index%21);var trendSlow=48+8*((index/21)%15);if(trendSlow<=trendFast)trendSlow=Math.Min(160,trendFast+8);
            return new(trendFast,trendSlow,.0002+.0001*((index/315)%20),0,0);
        }
        if(family==StrategyFamily.NewsMomentum)return new(12,48,.0005,0,.05+.025*(index%19));
        var fast=8+2*(index%17);var slow=48+8*((index/17)%20);
        var entry=.8+.2*((index/340)%14);var exit=.2+.1*((index/4760)%8);if(exit>=entry)exit=Math.Max(.2,entry-.2);
        var stop=Math.Max(entry+.4,2.4+.2*((index/38080)%12));
        return new(fast,slow,0,entry,0,exit,Math.Min(5,stop),16+2*((index/456960)%9),1.5+.25*((index/4112640)%15),.6+.1*((index/61689600)%13),8+4*((index/801964800)%23));
    }
}

internal sealed class HistoricalResearchEngine
{
    private readonly ResearchRealityModel _reality;
    private readonly DeterministicStrategyRegistry _strategies;

    internal HistoricalResearchEngine(ResearchRealityModel? reality=null,DeterministicStrategyRegistry? strategies=null)
    {
        _reality=reality??new ResearchRealityModel();
        _strategies=strategies??new DeterministicStrategyRegistry();
    }

    internal ResearchRealityModel Reality=>_reality;
    internal DeterministicStrategyRegistry Strategies=>_strategies;
    internal sealed record StrategyRobustness(double WorstRegimeReturn,double TrainTestExpectancyGap,int PassingRegimes,int EvaluatedRegimes,bool Passed,double Score);

    public StrategyValidation Validate(StrategyProfile profile, IReadOnlyList<CandleEvidence> candles, IReadOnlyList<NewsFeature> news, RiskLimits limits)
    {
        var strategy=_strategies.Resolve(profile.Family);
        if(!_strategies.IsProfileCompatible(profile))return new(profile.Id,candles.Count,0,0,0,0,1,0,0,0,1,0,false,$"strategy implementation mismatch; current={profile.Version}; required={strategy.ImplementationVersion}; revalidation required",StrategyVersion:profile.Version);
        if (candles.Count < 500) return new(profile.Id, candles.Count, 0, 0, 0, 0, 1, 0, 0, 0, 1, 0, false, "insufficient hourly history",StrategyVersion:profile.Version);
        var timeline=strategy.BuildResearchTimeline(profile,candles,news);
        var timelineArtifact=StrategyExposureTimelineV1.CreateArtifact(timeline);
        var returns=timelineArtifact is null?[]:_reality.Simulate(timelineArtifact.Decisions);
        if(returns.Count<2)return new(
            profile.Id,candles.Count,0,0,0,0,1,0,0,0,1,0,false,
            timelineArtifact is null?"canonical research timeline unavailable or invalid":$"canonical research timeline too short; timeline_sha256={timelineArtifact.CanonicalSha256}",
            StrategyVersion:profile.Version,
            TimelineSha256:timelineArtifact?.CanonicalSha256??string.Empty);
        var split = Math.Clamp((int)(returns.Count * .65), 1, returns.Count-1); var train = returns.Take(split).ToArray(); var test = returns.Skip(split).ToArray();
        var all = Metrics(returns); var trainMetrics=Metrics(train);var oos = Metrics(test); var walk = WalkForward(profile, candles, news); var mc = MonteCarlo(returns);var robustness=EvaluateRobustness(returns,trainMetrics.Expectancy,oos.Expectancy);var benchmark=candles[0].Close>0?(double)(candles[^1].Close/candles[0].Close-1):0;
        var score = Math.Clamp(.16 * Math.Min(1, all.ProfitFactor / 1.5) + .16 * Math.Max(0, (oos.TotalReturn + .10) / .30) + .16 * (1 - Math.Min(1, all.MaxDrawdown / .25)) + .16 * walk + .16 * (1 - mc)+.20*robustness.Score, 0, 1);
        var passed = returns.Count(x => x.Trade) >= Math.Max(StrategyGovernor.MinimumValidationTrades, limits.MinimumBacktestTrades) && oos.Expectancy > 0 && all.ProfitFactor >= 1.1 && all.MaxDrawdown <= .25 && walk >= .5 && mc <= .45&&robustness.Passed;
        return new(profile.Id, candles.Count, returns.Count(x => x.Trade), all.WinRate, all.ProfitFactor, all.Expectancy, all.MaxDrawdown, all.Sharpe, oos.TotalReturn, walk, mc, score, passed, $"{profile.Id} strategy_impl={strategy.ImplementationVersion} trades={returns.Count(x => x.Trade)} OOS={oos.TotalReturn:P1} PF={all.ProfitFactor:F2} DD={all.MaxDrawdown:P1} WF={walk:F2} MC={mc:P0} regimes={robustness.PassingRegimes}/{robustness.EvaluatedRegimes} worst={robustness.WorstRegimeReturn:P1} gap={robustness.TrainTestExpectancyGap:P3} timeline_sha256={timelineArtifact!.CanonicalSha256} cost_rt={_reality.Costs.RoundTripVariableRate:P4} passed={passed}",robustness.WorstRegimeReturn,robustness.TrainTestExpectancyGap,robustness.PassingRegimes,robustness.EvaluatedRegimes,profile.Version,test.Count(x=>x.Trade),all.TotalReturn,benchmark,TimelineSha256:timelineArtifact!.CanonicalSha256);
    }

    internal static StrategyRobustness EvaluateRobustness(IReadOnlyList<(double Return,bool Trade)> values,double? trainExpectancy=null,double? testExpectancy=null)
    {
        const int folds=StrategyGovernor.RequiredEvaluatedRegimes;if(values.Count<folds)return new(-1,1,0,0,false,0);
        var metrics=new List<(double Return,double Drawdown,double Expectancy,int Trades)>();
        for(var fold=0;fold<folds;fold++)
        {
            var start=fold*values.Count/folds;var end=(fold+1)*values.Count/folds;var slice=values.Skip(start).Take(end-start).ToArray();var result=Metrics(slice);
            metrics.Add((result.TotalReturn,result.MaxDrawdown,result.Expectancy,slice.Count(x=>x.Trade)));
        }
        var split=Math.Clamp((int)(values.Count*.65),1,values.Count);var train=trainExpectancy??Metrics(values.Take(split).ToArray()).Expectancy;var test=testExpectancy??Metrics(values.Skip(split).ToArray()).Expectancy;
        var gap=Math.Abs(train-test);var passing=metrics.Count(x=>x.Trades>0&&x.Return>=-.08&&x.Drawdown<=.25&&x.Expectancy>=-.0005);var worst=metrics.Min(x=>x.Return);
        var passed=passing>=StrategyGovernor.MinimumPassingRegimes&&worst>=StrategyGovernor.MinimumWorstRegimeReturn&&gap<=StrategyGovernor.MaximumTrainTestExpectancyGap;var downside=Math.Max(0,-worst);var score=Math.Clamp((passing/(double)folds)*.5+Math.Max(0,1-downside/Math.Abs(StrategyGovernor.MinimumWorstRegimeReturn))*.25+Math.Max(0,1-gap/StrategyGovernor.MaximumTrainTestExpectancyGap)*.25,0,1);
        return new(worst,gap,passing,folds,passed,score);
    }

    public StrategySignal Signal(StrategyProfile profile, MarketEvidence market, IReadOnlyList<NewsEvidence> news)
    {
        var strategy=_strategies.Resolve(profile.Family);
        if(!_strategies.IsProfileCompatible(profile))return new StrategySignal(profile.Id,profile.Symbol,0,0,$"strategy implementation mismatch; current={profile.Version}; required={strategy.ImplementationVersion}; hold until revalidated",profile.Version);
        var signal=strategy.Signal(profile,market,news);
        return signal with { StrategyVersion=profile.Version };
    }

    internal IReadOnlyList<StrategyExposureDecisionV1> Timeline(StrategyProfile profile,IReadOnlyList<CandleEvidence> candles,IReadOnlyList<NewsFeature> news)
        =>_strategies.IsProfileCompatible(profile)?_strategies.Resolve(profile.Family).BuildResearchTimeline(profile,candles,news):[];

    internal StrategyExposureTimelineArtifactV1? TimelineArtifact(StrategyProfile profile,IReadOnlyList<CandleEvidence> candles,IReadOnlyList<NewsFeature> news)
        =>StrategyExposureTimelineV1.CreateArtifact(Timeline(profile,candles,news));

    internal List<(double Return, bool Trade)> Simulate(StrategyProfile profile, IReadOnlyList<CandleEvidence> candles, IReadOnlyList<NewsFeature> news)
        =>_reality.Simulate(Timeline(profile,candles,news));

    private static (double WinRate, double ProfitFactor, double Expectancy, double MaxDrawdown, double Sharpe, double TotalReturn) Metrics(IReadOnlyList<(double Return, bool Trade)> values) { var r = values.Select(x => x.Return).ToArray(); if (r.Length == 0) return (0, 0, 0, 1, 0, 0); var wins = r.Where(x => x > 0).Sum(); var losses = -r.Where(x => x < 0).Sum(); var equity = 1d; var high = 1d; var dd = 0d; foreach (var x in r) { equity *= Math.Max(.01, 1 + x); high = Math.Max(high, equity); dd = Math.Max(dd, (high - equity) / high); } var avg = r.Average(); var sd = Math.Sqrt(r.Select(x => (x - avg) * (x - avg)).Average()); return (r.Count(x => x > 0) / (double)r.Length, losses > 0 ? wins / losses : wins > 0 ? 9 : 0, avg, dd, sd > 0 ? avg / sd * Math.Sqrt(24 * 365) : 0, equity - 1); }
    private double WalkForward(StrategyProfile p, IReadOnlyList<CandleEvidence> c, IReadOnlyList<NewsFeature> n) { var scores = new List<double>(); for (var i = 0; i < 4; i++) { var start = i * c.Count / 8; var length = Math.Min(c.Count - start, c.Count / 2); scores.Add(Math.Clamp(.5 + Metrics(Simulate(p, c.Skip(start).Take(length).ToArray(), n)).Expectancy * 100 - Metrics(Simulate(p, c.Skip(start).Take(length).ToArray(), n)).MaxDrawdown, 0, 1)); } return scores.DefaultIfEmpty(0).Average(); }
    private static double MonteCarlo(IReadOnlyList<(double Return, bool Trade)> values) { var r = values.Select(x => x.Return).ToArray(); if (r.Length == 0) return 1; var random = new Random(73); var losses = 0; for (var n = 0; n < 250; n++) { var equity = 1d; for (var i = 0; i < Math.Min(r.Length, 2000); i++) equity *= Math.Max(.01, 1 + r[random.Next(r.Length)]); if (equity < 1) losses++; } return losses / 250d; }
}
