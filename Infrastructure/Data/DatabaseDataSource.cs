using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using 币安量化机器人.Core.Data;
using 币安量化机器人.Core.Models;

namespace 币安量化机器人.Infrastructure.Data;

public class DatabaseDataSource : IDataSource
{
    private readonly string _connectionString;

    public DatabaseDataSource(string connectionString)
    {
        _connectionString = connectionString;
    }

    public string Name => "Database";

    public async IAsyncEnumerable<RawDataFrame> ReadAsync(DataQuery query, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var command = connection.CreateCommand();
        command.CommandText = "SELECT Timestamp, Open, High, Low, Close, Volume FROM MarketData WHERE Symbol = $symbol AND Timestamp BETWEEN $start AND $end ORDER BY Timestamp";
        command.Parameters.AddWithValue("$symbol", query.Symbol);
        command.Parameters.AddWithValue("$start", query.Start);
        command.Parameters.AddWithValue("$end", query.End);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var payload = new Dictionary<string, object>
            {
                ["open"] = reader.GetDouble(1),
                ["high"] = reader.GetDouble(2),
                ["low"] = reader.GetDouble(3),
                ["close"] = reader.GetDouble(4),
                ["volume"] = reader.GetDouble(5)
            };

            yield return new RawDataFrame(Name, query.Symbol, reader.GetDateTime(0), payload);
        }
    }
}
