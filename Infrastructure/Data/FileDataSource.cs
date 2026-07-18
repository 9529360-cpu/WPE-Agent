using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using 币安量化机器人.Core.Data;
using 币安量化机器人.Core.Models;

namespace 币安量化机器人.Infrastructure.Data;

public class FileDataSource : IDataSource
{
    private readonly string _directory;

    public FileDataSource(string directory)
    {
        _directory = directory;
    }

    public string Name => "File";

    public async IAsyncEnumerable<RawDataFrame> ReadAsync(DataQuery query, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(_directory, $"{query.Symbol}.csv");
        if (!File.Exists(path))
        {
            yield break;
        }

        using var reader = new StreamReader(path);
        string? line;
        while ((line = await reader.ReadLineAsync()) is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var parts = line.Split(',');
            if (parts.Length < 6)
            {
                continue;
            }

            if (!DateTime.TryParse(parts[0], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var timestamp))
            {
                continue;
            }

            if (timestamp < query.Start || timestamp > query.End)
            {
                continue;
            }

            var payload = new Dictionary<string, object>
            {
                ["open"] = double.Parse(parts[1], CultureInfo.InvariantCulture),
                ["high"] = double.Parse(parts[2], CultureInfo.InvariantCulture),
                ["low"] = double.Parse(parts[3], CultureInfo.InvariantCulture),
                ["close"] = double.Parse(parts[4], CultureInfo.InvariantCulture),
                ["volume"] = double.Parse(parts[5], CultureInfo.InvariantCulture)
            };

            yield return new RawDataFrame(Name, query.Symbol, timestamp, payload);
        }
    }
}
