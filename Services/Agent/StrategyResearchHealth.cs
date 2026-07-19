using System.Text.Json;

namespace 币安量化机器人.Services.Agent;

public static class StrategyResearchHealth
{
    public static bool IsHealthy(string? stateJson, DateTime utcNow, TimeSpan maxAge)
    {
        if (string.IsNullOrWhiteSpace(stateJson)) return false;
        try
        {
            using var doc = JsonDocument.Parse(stateJson);
            var root = doc.RootElement;
            if (!root.TryGetProperty("status", out var status) ||
                !string.Equals(status.GetString(), "WAITING", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(status.GetString(), "RUNNING", StringComparison.OrdinalIgnoreCase)) return false;
            if (!root.TryGetProperty("heartbeatAtUtc", out var heartbeat) || !heartbeat.TryGetDateTime(out var timestamp)) return false;
            return utcNow - timestamp <= maxAge && timestamp <= utcNow.AddMinutes(1);
        }
        catch (JsonException) { return false; }
    }
}
