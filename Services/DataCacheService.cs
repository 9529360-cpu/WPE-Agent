using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using 币安量化机器人.Models;

namespace 币安量化机器人.Services;

public class DataCacheService
{
    private readonly string _connectionString;

    public DataCacheService()
    {
        var dataDirectory = Path.Combine(AppContext.BaseDirectory, "Data");
        Directory.CreateDirectory(dataDirectory);
        var dbPath = Path.Combine(dataDirectory, "terminal_cache.db");
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString();
    }

    public async Task InitializeAsync()
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync().ConfigureAwait(false);

        var createFunding = @"CREATE TABLE IF NOT EXISTS funding_rates (
                symbol TEXT NOT NULL,
                timestamp INTEGER NOT NULL,
                rate REAL NOT NULL,
                PRIMARY KEY(symbol, timestamp)
            );";

        var createPrices = @"CREATE TABLE IF NOT EXISTS price_history (
                symbol TEXT NOT NULL,
                timestamp INTEGER NOT NULL,
                close REAL NOT NULL,
                PRIMARY KEY(symbol, timestamp)
            );";

        var createAccounts = @"CREATE TABLE IF NOT EXISTS accounts (
                id TEXT PRIMARY KEY,
                label TEXT NOT NULL,
                api_key TEXT NOT NULL,
                encrypted_secret TEXT,
                passphrase_hint TEXT,
                is_primary INTEGER NOT NULL,
                is_paper INTEGER NOT NULL,
                created_at INTEGER NOT NULL,
                notes TEXT
            );";

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = string.Join(Environment.NewLine, new[] { createFunding, createPrices, createAccounts });
        await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    public async Task SaveFundingRatesAsync(IEnumerable<FundingRateSnapshot> snapshots)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync().ConfigureAwait(false);

        foreach (var snapshot in snapshots)
        {
            foreach (var point in snapshot.History)
            {
                await using var cmd = connection.CreateCommand();
                cmd.CommandText = "INSERT OR REPLACE INTO funding_rates(symbol, timestamp, rate) VALUES ($symbol, $timestamp, $rate);";
                cmd.Parameters.AddWithValue("$symbol", snapshot.Symbol);
                cmd.Parameters.AddWithValue("$timestamp", new DateTimeOffset(point.Timestamp).ToUnixTimeMilliseconds());
                cmd.Parameters.AddWithValue("$rate", point.FundingRate);
                await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
        }

        await transaction.CommitAsync().ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<double>> LoadFundingRatesAsync(string symbol, int limit)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT rate FROM funding_rates WHERE symbol = $symbol ORDER BY timestamp DESC LIMIT $limit";
        cmd.Parameters.AddWithValue("$symbol", symbol);
        cmd.Parameters.AddWithValue("$limit", limit);

        var results = new List<double>();
        await using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
            results.Add(reader.GetDouble(0));

        results.Reverse();
        return results;
    }

    public async Task SavePricesAsync(string symbol, IEnumerable<decimal> closes)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync().ConfigureAwait(false);

        var now = DateTimeOffset.UtcNow;
        var timestamp = now.ToUnixTimeMilliseconds();

        foreach (var close in closes)
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = "INSERT OR REPLACE INTO price_history(symbol, timestamp, close) VALUES ($symbol, $timestamp, $close);";
            cmd.Parameters.AddWithValue("$symbol", symbol);
            cmd.Parameters.AddWithValue("$timestamp", timestamp--);
            cmd.Parameters.AddWithValue("$close", close);
            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        await transaction.CommitAsync().ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<double>> LoadReturnsAsync(string symbol, int limit)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT close FROM price_history WHERE symbol = $symbol ORDER BY timestamp DESC LIMIT $limit";
        cmd.Parameters.AddWithValue("$symbol", symbol);
        cmd.Parameters.AddWithValue("$limit", limit);

        var closes = new List<double>();
        await using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
            closes.Add(reader.GetDouble(0));

        closes.Reverse();
        if (closes.Count < 2)
            return Array.Empty<double>();

        return closes.Zip(closes.Skip(1), (prev, next) => Math.Log(next / prev)).ToArray();
    }

    public async Task SaveAccountProfileAsync(AccountProfile profile)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = @"INSERT OR REPLACE INTO accounts(id, label, api_key, encrypted_secret, passphrase_hint, is_primary, is_paper, created_at, notes)
                            VALUES ($id, $label, $api, $secret, $hint, $primary, $paper, $created, $notes);";
        cmd.Parameters.AddWithValue("$id", profile.Id.ToString());
        cmd.Parameters.AddWithValue("$label", profile.Label);
        cmd.Parameters.AddWithValue("$api", profile.ApiKey);
        cmd.Parameters.AddWithValue("$secret", (object?)profile.EncryptedSecret ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$hint", (object?)profile.PassphraseHint ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$primary", profile.IsPrimary ? 1 : 0);
        cmd.Parameters.AddWithValue("$paper", profile.IsPaperTrading ? 1 : 0);
        cmd.Parameters.AddWithValue("$created", new DateTimeOffset(profile.CreatedAt).ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("$notes", (object?)profile.Notes ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<AccountProfile>> LoadAccountsAsync()
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT id, label, api_key, encrypted_secret, passphrase_hint, is_primary, is_paper, created_at, notes FROM accounts";

        var result = new List<AccountProfile>();
        await using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            var profile = new AccountProfile
            {
                Label = reader.GetString(1),
                ApiKey = reader.GetString(2),
                EncryptedSecret = reader.IsDBNull(3) ? null : reader.GetString(3),
                PassphraseHint = reader.IsDBNull(4) ? null : reader.GetString(4),
                IsPrimary = reader.GetInt32(5) == 1,
                IsPaperTrading = reader.GetInt32(6) == 1,
                Notes = reader.IsDBNull(8) ? null : reader.GetString(8)
            };
            profile.Id = Guid.Parse(reader.GetString(0));
            profile.CreatedAt = DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(7)).UtcDateTime;
            result.Add(profile);
        }

        return result;
    }
}
