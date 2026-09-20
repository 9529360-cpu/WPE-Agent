using Microsoft.Data.Sqlite;
using System.Globalization;
using 币安量化机器人.Core.Strategy;

namespace 币安量化机器人.Services.Agent;

public sealed partial class AgentSqliteStore
{
    private const int StrategyCalibrationPriorObservations = 8;

    private void EnsureStrategyObservationIntelligenceColumns()
    {
        using var connection = new SqliteConnection(_cs);
        connection.Open();
        EnsureColumn(connection, "strategy_observations", "regime", "TEXT NOT NULL DEFAULT 'Unknown'");
        using var index = connection.CreateCommand();
        index.CommandText = "CREATE INDEX IF NOT EXISTS ix_strategy_observations_strategy_regime_time ON strategy_observations(strategy_id,regime,observed_at)";
        index.ExecuteNonQuery();
    }

    public Task RecordStrategyObservationAsync(
        string strategyId,
        string symbol,
        int direction,
        decimal price,
        double confidence,
        MarketRegime regime,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(strategyId)) throw new ArgumentException("Strategy id is required.", nameof(strategyId));
        if (string.IsNullOrWhiteSpace(symbol)) throw new ArgumentException("Symbol is required.", nameof(symbol));
        if (direction is < -1 or > 1) throw new ArgumentOutOfRangeException(nameof(direction));
        if (price <= 0) throw new ArgumentOutOfRangeException(nameof(price));
        if (!double.IsFinite(confidence) || confidence is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(confidence));
        if (!Enum.IsDefined(regime)) throw new ArgumentOutOfRangeException(nameof(regime));

        return Exec(
            "INSERT INTO strategy_observations(strategy_id,symbol,observed_at,direction,price,confidence,regime) VALUES($i,$s,$t,$d,$p,$c,$r)",
            ct,
            ("$i", strategyId),
            ("$s", symbol.ToUpperInvariant()),
            ("$t", _utcNow().ToUniversalTime().ToString("O")),
            ("$d", direction),
            ("$p", price.ToString(CultureInfo.InvariantCulture)),
            ("$c", confidence),
            ("$r", regime.ToString()));
    }

    private async Task<StrategyObservationPerformance> GetStrategyObservationIntelligenceAsync(string strategyId, CancellationToken ct)
    {
        var rows = new List<(int Direction, decimal Price, double Confidence, string Regime)>();
        await using var connection = new SqliteConnection(_cs);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT direction,price,confidence,regime FROM strategy_observations WHERE strategy_id=$i ORDER BY observed_at,id";
        command.Parameters.AddWithValue("$i", strategyId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var direction = reader.GetInt32(0);
            var price = decimal.Parse(reader.GetString(1), CultureInfo.InvariantCulture);
            var confidence = reader.GetDouble(2);
            var regime = reader.IsDBNull(3) ? MarketRegime.Unknown.ToString() : reader.GetString(3);
            rows.Add((direction, price, confidence, NormalizeRegime(regime)));
        }

        if (rows.Count < 2)
            return PendingPerformance(rows.Count);

        var resolved = new List<ResolvedStrategyObservation>();
        for (var index = 0; index < rows.Count - 1; index++)
        {
            var current = rows[index];
            var next = rows[index + 1];
            if (current.Direction == 0 || current.Price <= 0 || next.Price <= 0)
                continue;

            var value = current.Direction * (double)(next.Price / current.Price - 1m);
            resolved.Add(new(
                value,
                Math.Clamp(current.Confidence, 0, 1),
                current.Regime));
        }

        if (resolved.Count == 0)
            return PendingPerformance(rows.Count);

        var expectancy = resolved.Average(x => x.Return);
        var (maxDrawdown, failureStreak) = DrawdownAndFailureStreak(resolved.Select(x => x.Return));
        var quality = Math.Clamp(.5 + expectancy * 50 - maxDrawdown, 0, 1);
        var calibration = Calibration(resolved);

        var regimes = resolved
            .GroupBy(x => x.Regime, StringComparer.Ordinal)
            .OrderBy(x => x.Key, StringComparer.Ordinal)
            .Select(group =>
            {
                var observations = group.ToArray();
                var regimeExpectancy = observations.Average(x => x.Return);
                var (regimeDrawdown, _) = DrawdownAndFailureStreak(observations.Select(x => x.Return));
                var regimeQuality = Math.Clamp(.5 + regimeExpectancy * 50 - regimeDrawdown, 0, 1);
                return new StrategyRegimePerformance(
                    group.Key,
                    observations.Length,
                    regimeExpectancy,
                    observations.Count(x => x.Return > 0) / (double)observations.Length,
                    observations.Average(x => x.Confidence),
                    Calibration(observations),
                    regimeQuality);
            })
            .ToArray();

        return new(
            resolved.Count,
            expectancy,
            maxDrawdown,
            quality,
            failureStreak,
            $"actionable={resolved.Count} expectancy={expectancy:P2} drawdown={maxDrawdown:P1} calibration={calibration:F2}",
            calibration,
            regimes,
            rows.Count);
    }

    internal async Task<StrategyExecutionFeedback> GetStrategyExecutionFeedbackAsync(string strategyId,CancellationToken ct)
    {
        if(string.IsNullOrWhiteSpace(strategyId))throw new ArgumentException("Strategy id is required.",nameof(strategyId));
        var returns=new List<decimal>();
        await using var connection=new SqliteConnection(_cs);
        await connection.OpenAsync(ct);
        await using var command=connection.CreateCommand();
        command.CommandText="SELECT return_pct FROM trade_outcomes WHERE strategy_id=$id AND attribution_basis='automatic-artifact' AND return_pct IS NOT NULL ORDER BY closed_at DESC,id DESC LIMIT 50";
        command.Parameters.AddWithValue("$id",strategyId);
        await using var reader=await command.ExecuteReaderAsync(ct);
        while(await reader.ReadAsync(ct))
        {
            if(decimal.TryParse(reader.GetString(0),NumberStyles.Number,CultureInfo.InvariantCulture,out var value))
                returns.Add(value);
        }

        if(returns.Count==0)return new(0,0,0,.5);
        var wins=returns.Count(value=>value>0);
        const double priorWins=4;
        const double priorTrades=8;
        var posteriorWinRate=(wins+priorWins)/(returns.Count+priorTrades);
        return new(
            returns.Count,
            (double)returns.Average(),
            wins/(double)returns.Count,
            posteriorWinRate);
    }

    internal async Task<HypothesisExecutionFeedback> GetHypothesisExecutionFeedbackAsync(
        string symbol,
        TradeHypothesisKind kind,
        CancellationToken ct)
    {
        if(string.IsNullOrWhiteSpace(symbol))throw new ArgumentException("Symbol is required.",nameof(symbol));
        if(kind==TradeHypothesisKind.None||!Enum.IsDefined(kind))return new(0,0,0,.5);

        var returns=new List<decimal>();
        var excursions=new List<(decimal Mae,decimal Mfe)>();
        var stopLosses=0;
        var takeProfits=0;
        var structureInvalidations=0;
        await using var connection=new SqliteConnection(_cs);
        await connection.OpenAsync(ct);
        await using var command=connection.CreateCommand();
        command.CommandText="SELECT return_pct,mae_return_pct,mfe_return_pct,excursion_basis,exit_reason FROM trade_outcomes WHERE strategy_id LIKE $prefix AND strategy_version=$version AND attribution_basis='automatic-artifact' AND return_pct IS NOT NULL ORDER BY closed_at DESC,id DESC LIMIT 50";
        command.Parameters.AddWithValue("$prefix",$"HYP-{symbol.Trim().ToUpperInvariant()}-{kind}-%");
        command.Parameters.AddWithValue("$version",TradeHypothesis.CurrentVersion);
        await using var reader=await command.ExecuteReaderAsync(ct);
        while(await reader.ReadAsync(ct))
        {
            if(!decimal.TryParse(reader.GetString(0),NumberStyles.Number,CultureInfo.InvariantCulture,out var value))
                continue;
            returns.Add(value);
            if(string.Equals(reader.GetString(3),"runtime-mark-observations",StringComparison.Ordinal) &&
               decimal.TryParse(reader.GetString(1),NumberStyles.Number,CultureInfo.InvariantCulture,out var mae) &&
               decimal.TryParse(reader.GetString(2),NumberStyles.Number,CultureInfo.InvariantCulture,out var mfe))
                excursions.Add((mae,mfe));
            var exitReason=reader.GetString(4);
            if(exitReason.Contains("stop-loss",StringComparison.OrdinalIgnoreCase))stopLosses++;
            if(exitReason.Contains("take-profit",StringComparison.OrdinalIgnoreCase))takeProfits++;
            if(string.Equals(exitReason,PositionExitReasonCodes.StructureInvalidated,StringComparison.Ordinal))structureInvalidations++;
        }

        if(returns.Count==0)return new(0,0,0,.5);
        var wins=returns.Count(value=>value>0);
        const double priorWins=4;
        const double priorTrades=8;
        var posteriorWinRate=(wins+priorWins)/(returns.Count+priorTrades);
        return new(
            returns.Count,
            (double)returns.Average(),
            wins/(double)returns.Count,
            posteriorWinRate,
            excursions.Count,
            excursions.Count==0?0:(double)excursions.Average(value=>value.Mae),
            excursions.Count==0?0:(double)excursions.Average(value=>value.Mfe),
            stopLosses/(double)returns.Count,
            takeProfits/(double)returns.Count,
            structureInvalidations/(double)returns.Count);
    }

    private static StrategyObservationPerformance PendingPerformance(int rawObservations)
        => new(0, 0, 0, 0, 0, "shadow actionable observations pending", .5, Array.Empty<StrategyRegimePerformance>(), rawObservations);

    private static string NormalizeRegime(string value)
        => Enum.TryParse<MarketRegime>(value, true, out var parsed) && Enum.IsDefined(parsed)
            ? parsed.ToString()
            : MarketRegime.Unknown.ToString();

    private static (double MaxDrawdown, int FailureStreak) DrawdownAndFailureStreak(IEnumerable<double> returns)
    {
        var equity = 1d;
        var high = 1d;
        var drawdown = 0d;
        var failures = 0;
        foreach (var value in returns)
        {
            equity *= Math.Max(.01, 1 + value);
            high = Math.Max(high, equity);
            drawdown = Math.Max(drawdown, (high - equity) / high);
            failures = value < 0 ? failures + 1 : 0;
        }
        return (drawdown, failures);
    }

    private static double Calibration(IReadOnlyCollection<ResolvedStrategyObservation> observations)
    {
        if (observations.Count == 0) return .5;
        var brier = observations.Average(value =>
        {
            var target = value.Return > 0 ? 1d : 0d;
            var error = value.Confidence - target;
            return error * error;
        });
        var raw = Math.Clamp(1 - 2 * brier, 0, 1);
        return (raw * observations.Count + .5 * StrategyCalibrationPriorObservations) /
               (observations.Count + StrategyCalibrationPriorObservations);
    }

    private sealed record ResolvedStrategyObservation(double Return, double Confidence, string Regime);
}

internal sealed record StrategyExecutionFeedback(int Trades,double AverageReturn,double WinRate,double PosteriorWinRate);
internal sealed record HypothesisExecutionFeedback(int Trades,double AverageReturn,double WinRate,double PosteriorWinRate,int ExcursionTrades=0,double AverageMae=0,double AverageMfe=0,double StopLossRate=0,double TakeProfitRate=0,double StructureInvalidationRate=0);
