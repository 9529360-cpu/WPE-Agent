using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using 币安量化机器人.Core.Strategy;

namespace 币安量化机器人.Services.Agent;

public sealed partial class AgentSqliteStore
{
    internal async Task<PersistedStrategyValidation?> GetLatestStrategyValidationAsync(
        string strategyId,string strategyVersion,CancellationToken ct)
    {
        if(string.IsNullOrWhiteSpace(strategyId)||string.IsNullOrWhiteSpace(strategyVersion))return null;
        await using var connection=new SqliteConnection(_cs);
        await connection.OpenAsync(ct);
        await using var command=connection.CreateCommand();
        command.CommandText="SELECT created_at,result_json FROM strategy_validations WHERE strategy_id=$id ORDER BY id DESC LIMIT 100";
        command.Parameters.AddWithValue("$id",strategyId);
        await using var reader=await command.ExecuteReaderAsync(ct);
        while(await reader.ReadAsync(ct))
        {
            StrategyValidation? validation;
            try{validation=JsonSerializer.Deserialize<StrategyValidation>(reader.GetString(1));}
            catch(JsonException){continue;}
            if(validation is null||!string.Equals(validation.StrategyId,strategyId,StringComparison.Ordinal)
                ||!string.Equals(validation.StrategyVersion,strategyVersion,StringComparison.Ordinal))continue;
            var created=DateTimeOffset.Parse(reader.GetString(0),CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind).ToUniversalTime();
            return new(created,validation);
        }
        return null;
    }

    internal async Task<PersistedBacktestRun?> GetLatestBacktestRunAsync(
        string strategyId,string strategyVersion,CancellationToken ct)
    {
        if(string.IsNullOrWhiteSpace(strategyId)||string.IsNullOrWhiteSpace(strategyVersion))return null;
        await using var connection=new SqliteConnection(_cs);
        await connection.OpenAsync(ct);
        await using var command=connection.CreateCommand();
        command.CommandText="""
            SELECT id,strategy_id,strategy_version,symbol,status,completed_at,coverage_days,trades,out_of_sample_return,max_drawdown,sharpe
            FROM backtest_runs
            WHERE strategy_id=$id AND strategy_version=$version
            ORDER BY completed_at DESC LIMIT 1
            """;
        command.Parameters.AddWithValue("$id",strategyId);
        command.Parameters.AddWithValue("$version",strategyVersion);
        await using var reader=await command.ExecuteReaderAsync(ct);
        if(!await reader.ReadAsync(ct))return null;
        return new(reader.GetString(0),reader.GetString(1),reader.GetString(2),reader.GetString(3),reader.GetString(4),
            DateTime.Parse(reader.GetString(5),CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind),reader.GetInt32(6),
            reader.GetInt32(7),reader.GetDouble(8),reader.GetDouble(9),reader.GetDouble(10));
    }
}

internal sealed record PersistedStrategyValidation(DateTimeOffset CreatedAtUtc,StrategyValidation Validation);
