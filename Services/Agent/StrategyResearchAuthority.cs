using 币安量化机器人.Core.Strategy;

namespace 币安量化机器人.Services.Agent;

/// <summary>
/// Projects the exact persisted validation/backtest evidence for one executable strategy identity.
/// It does not run a second backtest engine and cannot grant authority to a different strategy/version.
/// </summary>
internal sealed class StrategyResearchAuthority
{
    internal static readonly TimeSpan MaximumValidationAge=TimeSpan.FromHours(24);
    internal static readonly TimeSpan MaximumEvidencePairSkew=TimeSpan.FromMinutes(5);

    private readonly AgentSqliteStore _store;
    private readonly StrategyGovernor _governor;
    private readonly Func<DateTimeOffset> _utcNow;

    internal StrategyResearchAuthority(
        AgentSqliteStore store,
        StrategyGovernor? governor=null,
        Func<DateTimeOffset>? utcNow=null)
    {
        _store=store??throw new ArgumentNullException(nameof(store));
        _governor=governor??new StrategyGovernor();
        _utcNow=utcNow??(()=>DateTimeOffset.UtcNow);
    }

    internal async Task<ResearchValidationResult?> ReadAsync(
        string strategyId,string strategyVersion,string symbol,CancellationToken ct)
    {
        if(string.IsNullOrWhiteSpace(strategyId)||string.IsNullOrWhiteSpace(strategyVersion)||string.IsNullOrWhiteSpace(symbol))
            return null;

        var profile=(await _store.GetStrategiesAsync(ct)).SingleOrDefault(value=>
            value.Lifecycle==StrategyLifecycle.Active
            &&string.Equals(value.Id,strategyId,StringComparison.Ordinal)
            &&string.Equals(value.Version,strategyVersion,StringComparison.Ordinal)
            &&string.Equals(value.Symbol,symbol,StringComparison.Ordinal));
        if(profile is null)return null;

        var persistedValidation=await _store.GetLatestStrategyValidationAsync(strategyId,strategyVersion,ct);
        var backtest=await _store.GetLatestBacktestRunAsync(strategyId,strategyVersion,ct);
        if(persistedValidation is null||backtest is null
           ||!string.Equals(backtest.Symbol,symbol,StringComparison.Ordinal)
           ||!string.Equals(backtest.StrategyId,strategyId,StringComparison.Ordinal)
           ||!string.Equals(backtest.StrategyVersion,strategyVersion,StringComparison.Ordinal))
            return null;

        var validation=persistedValidation.Validation;
        var completedAt=Utc(backtest.CompletedAtUtc);
        if((persistedValidation.CreatedAtUtc-completedAt).Duration()>MaximumEvidencePairSkew)return null;

        var now=_utcNow().ToUniversalTime();
        var fresh=completedAt<=now&&now-completedAt<=MaximumValidationAge;
        var forward=await _store.GetStrategyObservationPerformanceAsync(profile.Id,profile.Version,ct);
        var qualified=string.Equals(backtest.Status,"PASSED",StringComparison.Ordinal)
                      &&validation.Passed
                      &&_governor.CanPromote(profile,validation)
                      &&_governor.HasForwardQualification(forward);
        var approved=fresh&&qualified;

        return new()
        {
            ValidatedAtUtc=completedAt,
            StrategyId=profile.Id,
            Symbol=profile.Symbol,
            StrategyVersion=profile.Version,
            SampleSize=validation.SampleSize,
            Trades=validation.Trades,
            OutOfSampleTrades=validation.OutOfSampleTrades,
            CoverageDays=backtest.CoverageDays,
            WinRate=validation.WinRate,
            ProfitFactor=validation.ProfitFactor,
            Expectancy=validation.Expectancy,
            MaxDrawdown=validation.MaxDrawdown,
            Sharpe=validation.Sharpe,
            OutOfSampleReturn=validation.OutOfSampleReturn,
            WalkForwardScore=validation.WalkForwardScore,
            MonteCarloLossProbability=validation.MonteCarloLossProbability,
            QualityScore=validation.QualityScore,
            Approved=approved,
            Promoted=approved&&profile.Lifecycle==StrategyLifecycle.Active,
            StrategyReturn=validation.StrategyReturn,
            BenchmarkReturn=validation.BenchmarkReturn,
            RegimeReturns=new Dictionary<string,double>(),
            Summary=$"authority=exact-strategy-validation; strategy={profile.Id}; version={profile.Version}; fresh={fresh}; forward_observations={forward.Observations}; forward_expectancy={forward.Expectancy:F6}; qualified={qualified}; {validation.Summary}"
        };
    }

    private static DateTimeOffset Utc(DateTime value)
    {
        var utc=value.Kind==DateTimeKind.Utc?value:value.ToUniversalTime();
        return new DateTimeOffset(DateTime.SpecifyKind(utc,DateTimeKind.Utc));
    }
}
