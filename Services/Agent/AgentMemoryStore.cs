using System.Text.Json;
using System.IO;

namespace 币安量化机器人.Services.Agent;

public sealed class AgentMemoryStore
{
    private readonly string _path = Path.Combine(AppContext.BaseDirectory, "Data", "agent-memory.json");
    private readonly JsonSerializerOptions _json = new() { WriteIndented = true };

    public async Task<AgentMemory?> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path)) return null;
        await using var stream = File.OpenRead(_path);
        return await JsonSerializer.DeserializeAsync<AgentMemory>(stream, _json, cancellationToken);
    }

    public async Task SaveAsync(AgentMemory memory, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        await using var stream = File.Create(_path);
        await JsonSerializer.SerializeAsync(stream, memory, _json, cancellationToken);
    }
}
